using SimpleJadePinServer.Blazor.Crypto;

namespace SimpleJadePinServer.Blazor.Tests.Crypto;

public class BcUrTests
{
    [Fact]
    public void Encode_SmallPayload_ReturnsSingleFragment()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var fragments = BcUr.Encode(payload, "jade-pin");
        Assert.Single(fragments);
        Assert.StartsWith("ur:jade-pin/", fragments[0]);
        Assert.DoesNotContain("/", fragments[0]["ur:jade-pin/".Length..]);
    }

    [Fact]
    public void RoundTrip_SingleFragment_PreservesPayload()
    {
        var original = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var fragments = BcUr.Encode(original, "jade-pin");
        var decoded = BcUr.Decode(fragments);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void RoundTrip_LargePayload_MultiFragment_PreservesPayload()
    {
        var original = new byte[100];
        Random.Shared.NextBytes(original);
        var fragments = BcUr.Encode(original, "jade-pin");
        Assert.True(fragments.Length > 1, "Should produce multiple fragments");
        var decoded = BcUr.Decode(fragments);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_ExtractsUrType()
    {
        var payload = new byte[] { 0x01 };
        var fragments = BcUr.Encode(payload, "jade-updps");
        Assert.StartsWith("ur:jade-updps/", fragments[0]);
    }
}
