using SimpleJadePinServer.Blazor.Crypto;

namespace SimpleJadePinServer.Blazor.Tests.Crypto;

public class Crc32Tests
{
    [Fact]
    public void Compute_EmptyArray_ReturnsZero()
    {
        var result = Crc32.Compute([]);
        Assert.Equal(0u, result);
    }

    [Fact]
    public void Compute_KnownInput_MatchesExpected()
    {
        var input = "123456789"u8.ToArray();
        var result = Crc32.Compute(input);
        Assert.Equal(0xCBF43926u, result);
    }
}
