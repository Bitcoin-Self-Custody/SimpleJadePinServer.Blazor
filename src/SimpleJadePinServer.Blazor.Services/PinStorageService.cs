using System.Security.Cryptography;
using CSharpFunctionalExtensions;
using SimpleJadePinServer.Blazor.Crypto;

namespace SimpleJadePinServer.Blazor.Services;

// Reads/writes/deletes PIN files in <basePath>/pins/.
//
// File format v0x02 (clean-slate, NOT compatible with Python server v0x01):
//
//   Offset  Length  Field
//   ------  ------  -----
//   0       1       Version byte (0x02)
//   1       32      HMAC-SHA256 auth tag  (key = HMAC(aesPinData, pinPubkeyHash))
//   33      16      AES-CBC IV
//   49      80      AES-CBC(PKCS7) ciphertext of 69-byte plaintext
//
// Encrypted plaintext layout (69 bytes):
//   0..31   pin_secret_hash  (sha256 of the client's PIN secret)
//   32..63  storage_key      (HMAC-derived key, never returned to client)
//   64      attempt_counter  (0-2; file deleted on 3rd wrong attempt)
//   65..68  replay_counter   (4-byte little-endian monotonic counter)
//
// AES key for encryption  = HMAC-SHA256(aesPinData, pinPubkey)       [32 bytes]
// HMAC key for auth       = HMAC-SHA256(aesPinData, pinPubkeyHash)   [32 bytes]
public sealed class PinStorageService
{
    const byte Version       = 0x02;
    const int  PlaintextLength = 32 + 32 + 1 + 4; // 69 bytes

    readonly string _pinsPath;
    readonly byte[] _aesPinData;

    public PinStorageService(string basePath, byte[] aesPinData)
    {
        // PIN files live in a "pins" sub-directory of the configured base path.
        _pinsPath   = Path.Combine(basePath, "pins");
        _aesPinData = aesPinData;
    }

    // Each PIN file is named after the lowercase hex of sha256(pinPubkey).
    string GetFilePath(byte[] pinPubkeyHash) =>
        Path.Combine(_pinsPath, $"{Convert.ToHexString(pinPubkeyHash).ToLower()}.pin");

    /// <summary>Returns true if a PIN file exists for the given pubkey hash.</summary>
    public bool Exists(byte[] pinPubkeyHash) => File.Exists(GetFilePath(pinPubkeyHash));

    /// <summary>
    /// Encrypts and writes a PIN file.
    /// A fresh random IV is used on every save so ciphertexts are never repeated.
    /// </summary>
    public void Save(byte[] pinPubkeyHash, byte[] pinPubkey, PinData pinData)
    {
        Directory.CreateDirectory(_pinsPath);

        // Derive per-client keys from the server's global AES-pin-data secret.
        var storageAesKey = HmacSha256(_aesPinData, pinPubkey);      // encrypts payload
        var pinAuthKey    = HmacSha256(_aesPinData, pinPubkeyHash);  // authenticates file

        // Assemble the 69-byte plaintext.
        var plaintext = new byte[PlaintextLength];
        pinData.PinSecretHash.CopyTo(plaintext, 0);
        pinData.StorageKey.CopyTo(plaintext, 32);
        plaintext[64] = pinData.AttemptCounter;
        pinData.ReplayCounter.CopyTo(plaintext, 65);

        // Encrypt with a fresh random IV each save.
        var iv        = RandomNumberGenerator.GetBytes(16);
        var encrypted = AesCbcEncrypt(storageAesKey, iv, plaintext);

        // Build auth payload = [version | iv | ciphertext] then HMAC it.
        var authPayload = new byte[1 + iv.Length + encrypted.Length];
        authPayload[0] = Version;
        iv.CopyTo(authPayload, 1);
        encrypted.CopyTo(authPayload, 1 + iv.Length);
        var authTag = HmacSha256(pinAuthKey, authPayload);

        // Write file: [version][authTag][iv][ciphertext]
        using var fs = File.Create(GetFilePath(pinPubkeyHash));
        fs.Write(new[] { Version });
        fs.Write(authTag);
        fs.Write(iv);
        fs.Write(encrypted);
    }

    /// <summary>
    /// Reads, authenticates (HMAC), and decrypts a PIN file.
    /// Returns Failure if the file is missing, corrupt, or has a bad auth tag.
    /// </summary>
    public Result<PinData> Load(byte[] pinPubkeyHash, byte[] pinPubkey)
    {
        var path = GetFilePath(pinPubkeyHash);
        if (!File.Exists(path))
            return Result.Failure<PinData>("PIN file not found");

        var data = File.ReadAllBytes(path);

        // Minimum size: 1 (version) + 32 (HMAC) + 16 (IV) + 16 (one AES block minimum) = 65
        if (data.Length < 1 + 32 + 16 + 16)
            return Result.Failure<PinData>("PIN file too short");

        if (data[0] != Version)
            return Result.Failure<PinData>($"Unsupported PIN file version: {data[0]}");

        var hmacReceived = data[1..33];
        var iv           = data[33..49];
        var encrypted    = data[49..];

        // Re-derive auth key and verify HMAC in constant time to prevent timing attacks.
        var pinAuthKey   = HmacSha256(_aesPinData, pinPubkeyHash);
        var authPayload  = new byte[1 + iv.Length + encrypted.Length];
        authPayload[0]   = Version;
        iv.CopyTo(authPayload, 1);
        encrypted.CopyTo(authPayload, 1 + iv.Length);
        var hmacComputed = HmacSha256(pinAuthKey, authPayload);

        if (!CryptographicOperations.FixedTimeEquals(hmacReceived, hmacComputed))
            return Result.Failure<PinData>("PIN file HMAC verification failed");

        // Decrypt payload.
        var storageAesKey = HmacSha256(_aesPinData, pinPubkey);
        var plaintext     = AesCbcDecrypt(storageAesKey, iv, encrypted);

        if (plaintext.Length != PlaintextLength)
            return Result.Failure<PinData>($"Decrypted payload unexpected length: {plaintext.Length}");

        return Result.Success(new PinData(
            plaintext[..32],   // pin_secret_hash
            plaintext[32..64], // storage_key
            plaintext[64],     // attempt_counter
            plaintext[65..69]  // replay_counter
        ));
    }

    /// <summary>Deletes the PIN file for the given pubkey hash if it exists.</summary>
    public void Delete(byte[] pinPubkeyHash)
    {
        var path = GetFilePath(pinPubkeyHash);
        if (File.Exists(path)) File.Delete(path);
    }

    // ── Crypto helpers ────────────────────────────────────────────────────────

    static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key     = key;
        aes.IV      = iv;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.CreateEncryptor().TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key     = key;
        aes.IV      = iv;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.CreateDecryptor().TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }
}
