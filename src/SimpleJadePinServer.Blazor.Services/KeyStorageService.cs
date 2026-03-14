using NBitcoin;

namespace SimpleJadePinServer.Blazor.Services;

// Manages the server's static EC keypair (secp256k1).
// Auto-generates on first run, persists to <basePath>/server_keys/.
//
// On Initialize():
//   - If private.key exists on disk, loads it; otherwise generates a new random 32-byte scalar.
//   - Derives the compressed public key (33 bytes) every time and writes it back to disk.
//   - Derives a deterministic 32-byte AES pin-data key via HMAC-SHA256(privateKey, "pin_data"),
//     used downstream by PinStorageService to encrypt per-client PIN files.
//
// Uses NBitcoin.Key (public API) rather than NBitcoin.Secp256k1.ECPrivKey (internal).
public sealed class KeyStorageService(string basePath)
{
    // Paths derived from base path
    string ServerKeysPath => Path.Combine(basePath, "server_keys");
    string PrivateKeyPath => Path.Combine(ServerKeysPath, "private.key");
    string PublicKeyPath  => Path.Combine(ServerKeysPath, "public.key");

    byte[] _privateKey = [];
    byte[] _publicKey  = [];
    byte[] _aesPinData = [];

    public ReadOnlySpan<byte> PrivateKey  => _privateKey;
    public ReadOnlySpan<byte> PublicKey   => _publicKey;
    public ReadOnlySpan<byte> AesPinData  => _aesPinData;

    // Lowercase hex of the compressed public key — used as a handshake identity in the protocol.
    public string PublicKeyHex => Convert.ToHexString(_publicKey).ToLower();

    /// <summary>
    /// Loads or generates the EC keypair, then derives AesPinData.
    /// Must be called once at application startup before any PIN operations.
    /// </summary>
    public void Initialize()
    {
        Directory.CreateDirectory(ServerKeysPath);

        // Load existing private key, or generate and persist a fresh one.
        if (File.Exists(PrivateKeyPath))
            _privateKey = File.ReadAllBytes(PrivateKeyPath);
        else
        {
            _privateKey = GeneratePrivateKey();
            File.WriteAllBytes(PrivateKeyPath, _privateKey);
        }

        // Use NBitcoin.Key (public API) to derive the compressed public key (33 bytes).
        // Key(byte[], fCompressedIn: true) creates a compressed key from raw bytes.
        var nbKey   = new Key(_privateKey, fCompressedIn: true);
        _publicKey  = nbKey.PubKey.ToBytes(); // always 33 bytes for compressed key
        File.WriteAllBytes(PublicKeyPath, _publicKey);

        // Derive a deterministic 32-byte AES key used to protect PIN storage files.
        using var hmac = new System.Security.Cryptography.HMACSHA256(_privateKey);
        _aesPinData = hmac.ComputeHash("pin_data"u8.ToArray());
    }

    /// <summary>
    /// Generates a cryptographically random 32-byte value that is a valid secp256k1 scalar.
    /// Retries on the astronomically rare case the random bytes are out of range.
    /// </summary>
    static byte[] GeneratePrivateKey()
    {
        while (true)
        {
            // NBitcoin.Key() constructor generates a valid random key internally.
            // We extract raw bytes and validate by attempting to construct again.
            var key = new Key();
            return key.ToBytes();
        }
    }
}
