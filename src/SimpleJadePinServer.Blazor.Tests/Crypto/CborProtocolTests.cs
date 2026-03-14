using SimpleJadePinServer.Blazor.Crypto;

namespace SimpleJadePinServer.Blazor.Tests.Crypto;

public class CborProtocolTests
{
    [Fact]
    public void ParsePinRequest_ValidCbor_ExtractsUrlsAndData()
    {
        var request = CborProtocol.BuildMockPinRequest(["http://server/set_pin"], "AQID");
        var parsed = CborProtocol.ParsePinRequest(request);
        Assert.True(parsed.IsSuccess);
        Assert.Single(parsed.Value.Urls);
        Assert.Equal("http://server/set_pin", parsed.Value.Urls[0]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, parsed.Value.EncryptedData);
    }

    [Fact]
    public void BuildPinResponse_ReturnsValidCbor()
    {
        var cbor = CborProtocol.BuildPinResponse("AQID");
        Assert.NotNull(cbor);
        var decoded = PeterO.Cbor.CBORObject.DecodeFromBytes(cbor);
        Assert.Equal("0", decoded["id"].AsString());
        Assert.Equal("pin", decoded["method"].AsString());
        Assert.Equal("AQID", decoded["params"]["data"].AsString());
    }

    [Fact]
    public void BuildOracleConfig_ReturnsValidCbor()
    {
        var cbor = CborProtocol.BuildOracleConfig("http://server:4443", "", "02abcdef1234567890");
        var decoded = PeterO.Cbor.CBORObject.DecodeFromBytes(cbor);
        Assert.Equal("001", decoded["id"].AsString());
        Assert.Equal("update_pinserver", decoded["method"].AsString());
        Assert.Equal("http://server:4443", decoded["params"]["urlA"].AsString());
        // pubkey is encoded as CBOR bytes, not text string
        Assert.Equal(Convert.FromHexString("02abcdef1234567890"), decoded["params"]["pubkey"].GetByteString());
    }
}
