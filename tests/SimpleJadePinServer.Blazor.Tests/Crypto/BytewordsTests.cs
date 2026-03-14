using SimpleJadePinServer.Blazor.Crypto;

namespace SimpleJadePinServer.Blazor.Tests.Crypto;

public class BytewordsTests
{
    [Fact]
    public void EncodeMinimal_KnownBytes_ReturnsExpectedString()
    {
        var input = new byte[] { 0x00, 0x01 };
        var result = Bytewords.EncodeMinimal(input);
        Assert.Equal("aead", result);
    }

    [Fact]
    public void DecodeMinimal_KnownString_ReturnsExpectedBytes()
    {
        var result = Bytewords.DecodeMinimal("aead");
        Assert.Equal(new byte[] { 0x00, 0x01 }, result);
    }

    [Fact]
    public void RoundTrip_RandomBytes_PreservesData()
    {
        var input = new byte[] { 0x42, 0xFF, 0x00, 0x80, 0xDE };
        var encoded = Bytewords.EncodeMinimal(input);
        var decoded = Bytewords.DecodeMinimal(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void EncodeMinimal_LastByte_MapsToZoom()
    {
        var result = Bytewords.EncodeMinimal(new byte[] { 0xFF });
        Assert.Equal("zm", result);
    }

    [Fact]
    public void EncodeMinimal_AllBytes_RoundTrips()
    {
        var allBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var encoded = Bytewords.EncodeMinimal(allBytes);
        var decoded = Bytewords.DecodeMinimal(encoded);
        Assert.Equal(allBytes, decoded);
    }
}
