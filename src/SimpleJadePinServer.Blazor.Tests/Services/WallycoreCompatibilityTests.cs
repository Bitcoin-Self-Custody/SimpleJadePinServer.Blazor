using SimpleJadePinServer.Blazor.Services;

namespace SimpleJadePinServer.Blazor.Tests.Services;

// These tests verify that our C# crypto implementation produces identical
// output to wallycore. Test vectors were captured from the Python server.
// If ANY of these tests fail, the server WILL NOT interoperate with Jade.
public class WallycoreCompatibilityTests
{
    const string ServerPrivHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";
    const string ServerPubHex = "0284bf7562262bbd6940085748f3be6afa52ae317155181ece31b66351ccffa4b0";
    const string HmacPinDataHex = "e10a13b87cc4b1e5b50041ebef59bcee5aa00f51ff195986b99fcc851fb2ab5c";
    const string TweakHex = "4147e3e92c701f65c6a7d75b8feb10fa700699260aeea84703182c86aef27b2a";
    const string TweakedKeyHex = "e11d47509bfa76160dee3519e5922cbd27372f67e6070b3c5baf7fc2b900f05b";
    const string PlaintextHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    const string IvHex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string EncryptedHex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaefd3be921860e931b396994796e2378481518ce46db6e2dda8763f6ecdf0cab7ae9536b27c395ad434f681bb64acf8628293e0c4a06572c2597f4eb453350de59720b3b1bf6247443765a48b5a3a572f";

    [Fact]
    public void HmacSha256_MatchesWallycore()
    {
        var serverPriv = Convert.FromHexString(ServerPrivHex);
        var expected = Convert.FromHexString(HmacPinDataHex);
        var result = PinCryptoService.ComputeHmacSha256(serverPriv, "pin_data"u8.ToArray());
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Bip341Tweak_MatchesWallycore()
    {
        var serverPriv = Convert.FromHexString(ServerPrivHex);
        var cke = Convert.FromHexString(ServerPubHex);
        var replayCounter = new byte[] { 0x01, 0x00, 0x00, 0x00 };
        var sessionKey = PinCryptoService.DeriveSessionKey(serverPriv, cke, replayCounter);
        var expected = Convert.FromHexString(TweakedKeyHex);
        Assert.Equal(expected, sessionKey);
    }

    [Fact]
    public void AesCbcWithEcdh_Encrypt_MatchesWallycore()
    {
        var tweakedKey = Convert.FromHexString(TweakedKeyHex);
        var cke = Convert.FromHexString(ServerPubHex);
        var plaintext = Convert.FromHexString(PlaintextHex);
        var iv = Convert.FromHexString(IvHex);
        var encrypted = PinCryptoService.AesCbcWithEcdh(
            tweakedKey, iv, plaintext, cke, "blind_oracle_response", encrypt: true);
        var expected = Convert.FromHexString(EncryptedHex);
        Assert.Equal(expected, encrypted);
    }

    [Fact]
    public void AesCbcWithEcdh_Decrypt_MatchesWallycore()
    {
        var tweakedKey = Convert.FromHexString(TweakedKeyHex);
        var cke = Convert.FromHexString(ServerPubHex);
        var encrypted = Convert.FromHexString(EncryptedHex);
        var expectedPlaintext = Convert.FromHexString(PlaintextHex);
        var decrypted = PinCryptoService.AesCbcWithEcdh(
            tweakedKey, null, encrypted, cke, "blind_oracle_response", encrypt: false);
        Assert.Equal(expectedPlaintext, decrypted);
    }
}
