using System.Reflection;
using System.Security.Cryptography;
using CSharpFunctionalExtensions;
using NBitcoin;
using SimpleJadePinServer.Blazor.Models;

namespace SimpleJadePinServer.Blazor.Services;

// Core cryptographic operations for the Jade blind oracle PIN protocol.
//
// Replicates wallycore's ECDH + AES-CBC composite function and BIP341 key tweaking.
// This is the highest-risk component: if any crypto is wrong, Jade will reject all responses.
//
// Static methods handle pure crypto (testable in isolation).
// Instance methods orchestrate full set_pin / get_pin flows using injected services.
public sealed class PinCryptoService
{
    readonly KeyStorageService _keyStorage;
    readonly PinStorageService _pinStorage;

    public PinCryptoService(KeyStorageService keyStorage, PinStorageService pinStorage)
    {
        _keyStorage = keyStorage;
        _pinStorage = pinStorage;
    }

    // ── Static crypto primitives ─────────────────────────────────────────────

    /// <summary>
    /// Derives a session-specific private key by BIP341-tweaking the server's static key.
    ///
    /// tweak = sha256(hmac_sha256(cke, replay_counter))
    /// session_key = BIP341_TWEAK(server_private_key, tweak)
    ///
    /// BIP341 tweak: if the x-only public key for the private key would have odd y,
    /// negate the private key before adding the tweak scalar.
    /// </summary>
    public static byte[] DeriveSessionKey(byte[] serverPrivateKey, byte[] cke, byte[] replayCounter)
    {
        // tweak = sha256(hmac_sha256(cke, replay_counter))
        // Note: cke is the HMAC key, replay_counter is the HMAC data — matches Python wallycore call
        var hmacResult = ComputeHmacSha256(cke, replayCounter);
        var tweak = SHA256.HashData(hmacResult);

        // BIP341 key tweak via reflection because ECPrivKey is internal in NBitcoin 9.x.
        // Steps: get internal ECPrivKey, check parity, negate if odd y, then TweakAdd.
        return Bip341TweakPrivateKey(serverPrivateKey, tweak);
    }

    /// <summary>
    /// Composite ECDH + AES-CBC operation matching wallycore's aes_cbc_with_ecdh_key.
    ///
    /// 1. ECDH: compute shared point = publicKey * privateKey, take x-coordinate (32 bytes)
    /// 2. Key derivation: HMAC-SHA512(shared_x, contextLabel) → 64 bytes
    ///    - First 32 bytes = AES-256 encryption key
    ///    - Last 32 bytes = HMAC-SHA256 authentication key
    /// 3. AES-CBC encrypt or decrypt with PKCS7 padding
    /// 4. HMAC-SHA256 authentication over IV + ciphertext
    ///
    /// When encrypting: output = [IV (16 bytes)] [ciphertext] [HMAC (32 bytes)]
    /// When decrypting: verify HMAC, extract IV from first 16 bytes, strip HMAC from end
    /// </summary>
    public static byte[] AesCbcWithEcdh(byte[] privateKey, byte[]? iv, byte[] data, byte[] publicKey,
        string contextLabel, bool encrypt)
    {
        // ECDH: shared point via NBitcoin public API
        var nbPrivKey = new Key(privateKey, fCompressedIn: true);
        var nbPubKey = new PubKey(publicKey);
        var sharedPubKey = nbPubKey.GetSharedPubkey(nbPrivKey);

        // wallycore's wally_ecdh uses libsecp256k1's default ECDH hash function,
        // which returns SHA256(compressed_shared_point) — NOT the raw x-coordinate.
        // The compressed point is [0x02|0x03 prefix][32-byte x], totaling 33 bytes.
        var ecdhSecret = SHA256.HashData(sharedPubKey.ToBytes());

        // Derive 64 bytes via HMAC-SHA512(ecdh_secret, context_label) — matches wallycore
        // First 32 bytes = AES encryption key, last 32 bytes = HMAC authentication key
        var contextBytes = System.Text.Encoding.UTF8.GetBytes(contextLabel);
        var keyMaterial = ComputeHmacSha512(ecdhSecret, contextBytes);
        var aesKey = keyMaterial[..32];
        var hmacKey = keyMaterial[32..];

        if (encrypt)
        {
            // IV must be provided for encryption
            ArgumentNullException.ThrowIfNull(iv);
            var ciphertext = AesCbcEncrypt(aesKey, iv, data);

            // Output: [IV][ciphertext][HMAC-SHA256(hmacKey, IV + ciphertext)]
            var ivAndCiphertext = new byte[iv.Length + ciphertext.Length];
            iv.CopyTo(ivAndCiphertext, 0);
            ciphertext.CopyTo(ivAndCiphertext, iv.Length);

            var hmac = ComputeHmacSha256(hmacKey, ivAndCiphertext);
            var output = new byte[ivAndCiphertext.Length + hmac.Length];
            ivAndCiphertext.CopyTo(output, 0);
            hmac.CopyTo(output, ivAndCiphertext.Length);
            return output;
        }
        else
        {
            // For decryption: last 32 bytes = HMAC tag, first 16 bytes = IV, middle = ciphertext
            var hmacTag = data[^32..];
            var ivAndCiphertext = data[..^32];
            var expectedHmac = ComputeHmacSha256(hmacKey, ivAndCiphertext);

            if (!CryptographicOperations.FixedTimeEquals(hmacTag, expectedHmac))
                throw new CryptographicException("HMAC verification failed — data integrity check failed");

            var extractedIv = ivAndCiphertext[..16];
            var ciphertext = ivAndCiphertext[16..];
            return AesCbcDecrypt(aesKey, extractedIv, ciphertext);
        }
    }

    /// <summary>
    /// Recovers a compressed public key (33 bytes) from a message and a 65-byte compact signature.
    ///
    /// Signature format: [1 byte recovery flag] [32 bytes r] [32 bytes s]
    /// The recovery flag encodes the recovery ID (0-3).
    /// </summary>
    public static Result<byte[]> RecoverPublicKey(byte[] message, byte[] signature)
    {
        if (signature.Length != 65)
            return Result.Failure<byte[]>($"Signature must be 65 bytes, got {signature.Length}");

        // wallycore format: first byte is recovery flag (27 + recId + compressed_flag)
        // For compressed keys, flag = 31 + recId (i.e., 27 + 4 + recId)
        // NBitcoin CompactSignature expects recId 0-3
        var recoveryFlag = signature[0];
        var recId = recoveryFlag >= 31 ? recoveryFlag - 31 : recoveryFlag - 27;
        var sigBytes = signature[1..]; // 64 bytes: r (32) + s (32)

        var compactSig = new CompactSignature(recId, sigBytes);
        var msgHash = new uint256(message);
        var recovered = PubKey.RecoverCompact(msgHash, compactSig);

        if (recovered == null)
            return Result.Failure<byte[]>("Failed to recover public key from signature");

        return Result.Success(recovered.Compress().ToBytes());
    }

    /// <summary>
    /// HMAC-SHA256(key, data) — deterministic keyed hash.
    /// </summary>
    public static byte[] ComputeHmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    /// <summary>
    /// HMAC-SHA512(key, data) — used by wallycore for deriving encryption + HMAC keys from ECDH shared secret.
    /// Returns 64 bytes: first 32 = AES key, last 32 = HMAC key.
    /// </summary>
    public static byte[] ComputeHmacSha512(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA512(key);
        return hmac.ComputeHash(data);
    }

    /// <summary>
    /// BIP341 tagged hash: SHA256(SHA256(tag) || SHA256(tag) || data).
    /// Used by BIP341 key tweaking to domain-separate the tweak from other uses of SHA256.
    /// </summary>
    static byte[] TaggedHash(string tag, byte[] data)
    {
        var tagHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag));
        var preimage = new byte[tagHash.Length * 2 + data.Length];
        tagHash.CopyTo(preimage, 0);
        tagHash.CopyTo(preimage, tagHash.Length);
        data.CopyTo(preimage, tagHash.Length * 2);
        return SHA256.HashData(preimage);
    }

    // ── Instance orchestration methods ───────────────────────────────────────

    /// <summary>
    /// Handles the set_pin protocol:
    /// 1. Parse cke (33), replayCounter (4), encryptedData from input
    /// 2. DeriveSessionKey, decrypt with "blind_oracle_request"
    /// 3. Extract pin_secret (32), entropy (32), signature (65)
    /// 4. Recover pin_pubkey, compute pin_pubkey_hash
    /// 5. If PIN file exists, verify client_replay_counter > server_replay_counter
    /// 6. new_key = HMAC-SHA256(random_32, entropy)
    /// 7. Save PIN file with replay counter reset to 0
    /// 8. aes_key = HMAC-SHA256(new_key, pin_secret)
    /// 9. Encrypt response with "blind_oracle_response", return
    /// </summary>
    public Result<byte[]> SetPin(byte[] data)
    {
        if (data.Length <= 37)
            return Result.Failure<byte[]>("Data too short for set_pin");

        // Step 1: Parse input
        var cke = data[..33];
        var replayCounter = data[33..37];
        var encryptedData = data[37..];

        // Step 2: Derive session key and decrypt
        var sessionKey = DeriveSessionKey(_keyStorage.PrivateKey.ToArray(), cke, replayCounter);
        var payload = AesCbcWithEcdh(sessionKey, null, encryptedData, cke, "blind_oracle_request", encrypt: false);

        // Step 3: Extract fields — set_pin payload is pin_secret(32) + entropy(32) + signature(65) = 129 bytes
        if (payload.Length != 32 + 32 + 65)
            return Result.Failure<byte[]>($"Decrypted set_pin payload unexpected length: {payload.Length}");

        var pinSecret = payload[..32];
        var entropy = payload[32..64];
        var sig = payload[64..];

        // Step 4: Recover pin_pubkey from the signed message
        // signed_msg = sha256(cke + replay_counter + pin_secret + entropy)
        var signedMsg = SHA256.HashData([.. cke, .. replayCounter, .. pinSecret, .. entropy]);
        var pinPubkeyResult = RecoverPublicKey(signedMsg, sig);
        if (pinPubkeyResult.IsFailure)
            return Result.Failure<byte[]>($"Failed to recover pin pubkey: {pinPubkeyResult.Error}");

        var pinPubkey = pinPubkeyResult.Value;
        var pinPubkeyHash = SHA256.HashData(pinPubkey);

        // Step 5: If PIN file already exists, enforce anti-replay
        if (_pinStorage.Exists(pinPubkeyHash))
        {
            var existingResult = _pinStorage.Load(pinPubkeyHash, pinPubkey);
            if (existingResult.IsSuccess)
            {
                var clientCounter = BitConverter.ToUInt32(replayCounter, 0);
                var serverCounter = BitConverter.ToUInt32(existingResult.Value.ReplayCounter, 0);
                if (clientCounter <= serverCounter)
                    return Result.Failure<byte[]>("Replay counter not greater than server counter");
            }
        }

        // Step 6: Generate new storage key from server random + client entropy
        var ourRandom = RandomNumberGenerator.GetBytes(32);
        var newKey = ComputeHmacSha256(ourRandom, entropy);

        // Step 7: Save PIN file with replay counter reset to 0
        var hashPinSecret = SHA256.HashData(pinSecret);
        var replayBytes = new byte[4]; // 0x00000000
        _pinStorage.Save(pinPubkeyHash, pinPubkey, new PinData(hashPinSecret, newKey, 0, replayBytes));

        // Step 8: Blind oracle — derive response key
        var aesKey = ComputeHmacSha256(newKey, pinSecret);

        // Step 9: Encrypt response and return
        var responseIv = RandomNumberGenerator.GetBytes(16);
        return Result.Success(
            AesCbcWithEcdh(sessionKey, responseIv, aesKey, cke, "blind_oracle_response", encrypt: true));
    }

    /// <summary>
    /// Handles the get_pin protocol:
    /// 1. Parse cke (33), replayCounter (4), encryptedData from input
    /// 2. DeriveSessionKey, decrypt with "blind_oracle_request"
    /// 3. Extract pin_secret (32), signature (65)
    /// 4. Recover pin_pubkey, compute pin_pubkey_hash
    /// 5. Load PIN file, verify replay counter
    /// 6. If correct PIN: reset attempts, save replay counter, use stored key
    /// 7. If wrong PIN: increment attempts (delete if >=3), use random key
    /// 8. If file not found: use random key
    /// 9. aes_key = HMAC-SHA256(saved_key, pin_secret)
    /// 10. Encrypt response with "blind_oracle_response", return
    /// </summary>
    public Result<byte[]> GetPin(byte[] data)
    {
        if (data.Length <= 37)
            return Result.Failure<byte[]>("Data too short for get_pin");

        // Step 1: Parse input
        var cke = data[..33];
        var replayCounter = data[33..37];
        var encryptedData = data[37..];

        // Step 2: Derive session key and decrypt
        var sessionKey = DeriveSessionKey(_keyStorage.PrivateKey.ToArray(), cke, replayCounter);
        var payload = AesCbcWithEcdh(sessionKey, null, encryptedData, cke, "blind_oracle_request", encrypt: false);

        // Step 3: Extract fields — get_pin payload is pin_secret(32) + signature(65) = 97 bytes
        if (payload.Length != 32 + 65)
            return Result.Failure<byte[]>($"Decrypted get_pin payload unexpected length: {payload.Length}");

        var pinSecret = payload[..32];
        var sig = payload[32..];

        // Step 4: Recover pin_pubkey from the signed message
        // signed_msg = sha256(cke + replay_counter + pin_secret) — note: no entropy for get_pin
        var signedMsg = SHA256.HashData([.. cke, .. replayCounter, .. pinSecret]);
        var pinPubkeyResult = RecoverPublicKey(signedMsg, sig);
        if (pinPubkeyResult.IsFailure)
            return Result.Failure<byte[]>($"Failed to recover pin pubkey: {pinPubkeyResult.Error}");

        var pinPubkey = pinPubkeyResult.Value;
        var pinPubkeyHash = SHA256.HashData(pinPubkey);

        // Steps 5-8: Load PIN file and determine the key to use
        byte[] savedKey;
        var loadResult = _pinStorage.Load(pinPubkeyHash, pinPubkey);
        if (loadResult.IsFailure)
        {
            // Step 8: File not found — return random key (Jade won't be able to decrypt)
            savedKey = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            var pinData = loadResult.Value;

            // Step 5: Enforce anti-replay
            var clientCounter = BitConverter.ToUInt32(replayCounter, 0);
            var serverCounter = BitConverter.ToUInt32(pinData.ReplayCounter, 0);
            if (clientCounter <= serverCounter)
                return Result.Failure<byte[]>("Replay counter not greater than server counter");

            // Step 6/7: Compare PIN
            var hashPinSecret = SHA256.HashData(pinSecret);
            if (CryptographicOperations.FixedTimeEquals(hashPinSecret, pinData.PinSecretHash))
            {
                // Correct PIN: reset attempt counter, save new replay counter, use stored key
                _pinStorage.Save(pinPubkeyHash, pinPubkey,
                    new PinData(pinData.PinSecretHash, pinData.StorageKey, 0, replayCounter));
                savedKey = pinData.StorageKey;
            }
            else
            {
                // Wrong PIN
                if (pinData.AttemptCounter >= 2)
                {
                    // 3rd wrong attempt: delete the PIN file entirely
                    _pinStorage.Delete(pinPubkeyHash);
                }
                else
                {
                    // Increment attempt counter, save with current replay counter
                    _pinStorage.Save(pinPubkeyHash, pinPubkey,
                        new PinData(pinData.PinSecretHash, pinData.StorageKey,
                            (byte)(pinData.AttemptCounter + 1), replayCounter));
                }

                // Return random key so Jade can't decrypt — indistinguishable from correct response
                savedKey = RandomNumberGenerator.GetBytes(32);
            }
        }

        // Step 9: Blind oracle — derive response key
        var aesKey = ComputeHmacSha256(savedKey, pinSecret);

        // Step 10: Encrypt response and return
        var responseIv = RandomNumberGenerator.GetBytes(16);
        return Result.Success(
            AesCbcWithEcdh(sessionKey, responseIv, aesKey, cke, "blind_oracle_response", encrypt: true));
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    // Cached reflection members for accessing NBitcoin's internal ECPrivKey.
    // ECPrivKey is internal in NBitcoin 9.x, but we need it for BIP341 key tweaking.
    static readonly Type EcPrivKeyType = typeof(Key).Assembly.GetType("NBitcoin.Secp256k1.ECPrivKey")!;
    static readonly FieldInfo EcKeyField = typeof(Key).GetField("_ECKey", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // Delegate types for calling internal ECPrivKey methods that take Span/ReadOnlySpan
    // (can't use reflection Invoke with ref structs, so we use CreateDelegate instead).
    delegate object TweakAddDelegate(ReadOnlySpan<byte> tweak);
    delegate void WriteToSpanDelegate(Span<byte> span);

    /// <summary>
    /// BIP341 private key tweak: if public key has odd y, negate the key, then add tweak scalar.
    /// Uses reflection to access internal ECPrivKey because it's internal in NBitcoin 9.x.
    /// Uses CreateDelegate for methods that take Span/ReadOnlySpan (can't box ref structs).
    /// </summary>
    static byte[] Bip341TweakPrivateKey(byte[] privateKeyBytes, byte[] tweak)
    {
        // Get internal ECPrivKey from NBitcoin.Key
        var nbKey = new Key(privateKeyBytes, fCompressedIn: true);
        var ecPrivKey = EcKeyField.GetValue(nbKey)!;

        // Call CreateXOnlyPubKey(out bool parity) to determine if we need to negate.
        // This method returns an ECXOnlyPubKey; we only care about the parity out-param.
        // Also extract the x-only pubkey bytes for the tagged hash.
        var createXOnlyMethod = EcPrivKeyType.GetMethod("CreateXOnlyPubKey",
            [typeof(bool).MakeByRefType()])!;
        var args = new object?[] { false };
        var xOnlyPubKey = createXOnlyMethod.Invoke(ecPrivKey, args)!;
        var parity = (bool)args[0]!;

        // Extract x-only pubkey bytes (32 bytes) for the BIP341 tagged hash
        var xOnlyPubKeyType = xOnlyPubKey.GetType();
        var xWriteMethod = xOnlyPubKeyType.GetMethod("WriteToSpan", [typeof(Span<byte>)])!;
        var xWriteDel = xWriteMethod.CreateDelegate<WriteToSpanDelegate>(xOnlyPubKey);
        var xOnlyBytes = new byte[32];
        xWriteDel(xOnlyBytes);

        // BIP341 tagged hash: t = tagged_hash('TapTweak', xonly_pubkey || tweak)
        // This domain-separates the tweak so TweakAdd(t) matches wallycore's behavior
        var taggedData = new byte[32 + tweak.Length];
        xOnlyBytes.CopyTo(taggedData, 0);
        tweak.CopyTo(taggedData, 32);
        var t = TaggedHash("TapTweak", taggedData);

        // If parity is true (odd y), negate the key first: negated = order - key
        if (parity)
        {
            var negatedKeyBytes = NegatePrivateKey(privateKeyBytes);
            var negNbKey = new Key(negatedKeyBytes, fCompressedIn: true);
            ecPrivKey = EcKeyField.GetValue(negNbKey)!;
        }

        // Call TweakAdd via bound delegate (ReadOnlySpan<byte> can't be boxed for Invoke)
        // Note: we add the tagged hash 't', NOT the raw 'tweak'
        var tweakAddMethod = EcPrivKeyType.GetMethod("TweakAdd",
            [typeof(ReadOnlySpan<byte>)])!;
        var tweakAddDel = tweakAddMethod.CreateDelegate<TweakAddDelegate>(ecPrivKey);
        var tweakedKey = tweakAddDel(t);

        // Call WriteToSpan via bound delegate to extract 32-byte raw private key
        var writeMethod = EcPrivKeyType.GetMethod("WriteToSpan", [typeof(Span<byte>)])!;
        var writeDel = writeMethod.CreateDelegate<WriteToSpanDelegate>(tweakedKey);
        var result = new byte[32];
        writeDel(result);

        return result;
    }

    /// <summary>
    /// Negates a secp256k1 private key: result = order - key.
    /// Used for BIP341 tweaking when the public key has odd y-coordinate.
    /// </summary>
    static byte[] NegatePrivateKey(byte[] privateKey)
    {
        // secp256k1 group order (n) — the modulus for scalar arithmetic
        var order = new byte[]
        {
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE,
            0xBA, 0xAE, 0xDC, 0xE6, 0xAF, 0x48, 0xA0, 0x3B,
            0xBF, 0xD2, 0x5E, 0x8C, 0xD0, 0x36, 0x41, 0x41
        };

        var orderInt = new System.Numerics.BigInteger(order, isUnsigned: true, isBigEndian: true);
        var keyInt = new System.Numerics.BigInteger(privateKey, isUnsigned: true, isBigEndian: true);
        var negated = orderInt - keyInt;

        var result = negated.ToByteArray(isUnsigned: true, isBigEndian: true);

        // Pad to 32 bytes if shorter
        if (result.Length < 32)
        {
            var padded = new byte[32];
            result.CopyTo(padded, 32 - result.Length);
            return padded;
        }

        return result;
    }

    static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.CreateEncryptor().TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.CreateDecryptor().TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }
}
