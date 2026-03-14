using System.Security.Cryptography;
using NBitcoin;
using SimpleJadePinServer.Blazor.Services;

namespace SimpleJadePinServer.Blazor.Tests.Services;

public class PinCryptoServiceTests
{
    // ── DeriveSessionKey tests ───────────────────────────────────────────────

    [Fact]
    public void DeriveSessionKey_DifferentReplayCounters_ProduceDifferentKeys()
    {
        // Arrange: a fixed server private key and client key exchange pubkey
        var serverPrivateKey = new Key().ToBytes();
        var cke = new Key().PubKey.Compress().ToBytes(); // 33-byte compressed public key

        var replay1 = new byte[] { 0x01, 0x00, 0x00, 0x00 }; // counter = 1
        var replay2 = new byte[] { 0x02, 0x00, 0x00, 0x00 }; // counter = 2

        // Act: derive session keys for two different replay counters
        var sessionKey1 = PinCryptoService.DeriveSessionKey(serverPrivateKey, cke, replay1);
        var sessionKey2 = PinCryptoService.DeriveSessionKey(serverPrivateKey, cke, replay2);

        // Assert: both are valid 32-byte keys but differ due to BIP341 tweak differences
        Assert.Equal(32, sessionKey1.Length);
        Assert.Equal(32, sessionKey2.Length);
        Assert.False(sessionKey1.SequenceEqual(sessionKey2),
            "Session keys for different replay counters must be different");
    }

    [Fact]
    public void DeriveSessionKey_SameInputs_ProducesSameKey()
    {
        // Determinism check: same inputs always produce the same session key
        var serverPrivateKey = new Key().ToBytes();
        var cke = new Key().PubKey.Compress().ToBytes();
        var replay = new byte[] { 0x05, 0x00, 0x00, 0x00 };

        var key1 = PinCryptoService.DeriveSessionKey(serverPrivateKey, cke, replay);
        var key2 = PinCryptoService.DeriveSessionKey(serverPrivateKey, cke, replay);

        Assert.True(key1.SequenceEqual(key2), "Same inputs must produce the same session key");
    }

    [Fact]
    public void DeriveSessionKey_ResultIsValidPrivateKey()
    {
        // The tweaked key must itself be a valid secp256k1 private key
        var serverPrivateKey = new Key().ToBytes();
        var cke = new Key().PubKey.Compress().ToBytes();
        var replay = new byte[] { 0x01, 0x00, 0x00, 0x00 };

        var sessionKey = PinCryptoService.DeriveSessionKey(serverPrivateKey, cke, replay);

        // Should not throw — if it's invalid, Key constructor will throw
        var nbKey = new Key(sessionKey, fCompressedIn: true);
        Assert.Equal(33, nbKey.PubKey.ToBytes().Length);
    }

    // ── AesCbcWithEcdh tests ─────────────────────────────────────────────────

    [Fact]
    public void EncryptDecrypt_RoundTrip_PreservesPayload()
    {
        // Arrange: two keypairs (simulating server and client)
        var serverKey = new Key();
        var clientKey = new Key();
        var plaintext = RandomNumberGenerator.GetBytes(64); // arbitrary payload
        var iv = RandomNumberGenerator.GetBytes(16);

        // Act: encrypt with server's private key and client's public key
        var encrypted = PinCryptoService.AesCbcWithEcdh(
            serverKey.ToBytes(), iv, plaintext, clientKey.PubKey.ToBytes(),
            "blind_oracle_request", encrypt: true);

        // The encrypted output should start with the IV (16 bytes) followed by ciphertext
        Assert.True(encrypted.Length > 16, "Encrypted output must contain IV + ciphertext");
        Assert.True(encrypted[..16].SequenceEqual(iv), "First 16 bytes must be the IV");

        // Decrypt with client's private key and server's public key (ECDH is symmetric)
        var decrypted = PinCryptoService.AesCbcWithEcdh(
            clientKey.ToBytes(), null, encrypted, serverKey.PubKey.ToBytes(),
            "blind_oracle_request", encrypt: false);

        // Assert: round-trip preserves plaintext exactly
        Assert.True(plaintext.SequenceEqual(decrypted),
            "Decrypted payload must match original plaintext");
    }

    [Fact]
    public void EncryptDecrypt_DifferentContextLabels_CannotDecrypt()
    {
        // Using a different context label for decryption should produce garbage (wrong AES key)
        var serverKey = new Key();
        var clientKey = new Key();
        var plaintext = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);

        var encrypted = PinCryptoService.AesCbcWithEcdh(
            serverKey.ToBytes(), iv, plaintext, clientKey.PubKey.ToBytes(),
            "blind_oracle_request", encrypt: true);

        // Attempting to decrypt with a different context label should fail (padding error or wrong data)
        Assert.ThrowsAny<Exception>(() =>
            PinCryptoService.AesCbcWithEcdh(
                clientKey.ToBytes(), null, encrypted, serverKey.PubKey.ToBytes(),
                "blind_oracle_response", encrypt: false));
    }

    // ── HMAC-SHA256 tests ────────────────────────────────────────────────────

    [Fact]
    public void HmacSha256_KnownInput_ProducesConsistentOutput()
    {
        // Determinism: same key+data always produces the same HMAC
        var key = new byte[] { 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b,
                               0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b, 0x0b,
                               0x0b, 0x0b, 0x0b, 0x0b };
        var data = System.Text.Encoding.UTF8.GetBytes("Hi There");

        var result1 = PinCryptoService.ComputeHmacSha256(key, data);
        var result2 = PinCryptoService.ComputeHmacSha256(key, data);

        Assert.Equal(32, result1.Length);
        Assert.True(result1.SequenceEqual(result2), "HMAC-SHA256 must be deterministic");

        // RFC 4231 Test Case 1 expected output
        var expected = "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7";
        Assert.Equal(expected, Convert.ToHexString(result1).ToLower());
    }

    [Fact]
    public void HmacSha256_DifferentKeys_ProduceDifferentOutputs()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var key1 = RandomNumberGenerator.GetBytes(32);
        var key2 = RandomNumberGenerator.GetBytes(32);

        var hmac1 = PinCryptoService.ComputeHmacSha256(key1, data);
        var hmac2 = PinCryptoService.ComputeHmacSha256(key2, data);

        Assert.False(hmac1.SequenceEqual(hmac2), "Different keys must produce different HMACs");
    }

    // ── RecoverPublicKey tests ────────────────────────────────────────────────

    [Fact]
    public void RecoverPublicKey_ValidSignature_RecoversCorrectKey()
    {
        // Create a key, sign a message, then recover the public key
        var key = new Key();
        var message = SHA256.HashData(new byte[] { 1, 2, 3, 4, 5 });
        var msgHash = new uint256(message);

        // Sign compact (NBitcoin's format)
        var compactSig = key.SignCompact(msgHash);

        // Convert to wallycore format: [recovery_flag][r(32)][s(32)]
        // recovery_flag = 31 + recId for compressed keys
        var sig65 = new byte[65];
        sig65[0] = (byte)(31 + compactSig.RecoveryId);
        compactSig.Signature.CopyTo(sig65, 1);

        var result = PinCryptoService.RecoverPublicKey(message, sig65);

        Assert.True(result.IsSuccess, "RecoverPublicKey should succeed");
        Assert.Equal(33, result.Value.Length); // compressed pubkey
        Assert.True(result.Value.SequenceEqual(key.PubKey.Compress().ToBytes()),
            "Recovered public key must match original");
    }

    [Fact]
    public void RecoverPublicKey_WrongLength_ReturnsFailure()
    {
        var result = PinCryptoService.RecoverPublicKey(new byte[32], new byte[64]);
        Assert.True(result.IsFailure);
        Assert.Contains("65 bytes", result.Error);
    }
}
