using SimpleJadePinServer.Blazor.Services;

namespace SimpleJadePinServer.Blazor.Tests.Services;

public class KeyStorageServiceTests : IDisposable
{
    readonly string _tempDir;
    readonly KeyStorageService _service;

    public KeyStorageServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _service = new KeyStorageService(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Initialize_GeneratesKeyPair_WhenNoKeysExist()
    {
        _service.Initialize();
        Assert.Equal(32, _service.PrivateKey.Length);
        Assert.Equal(33, _service.PublicKey.Length);
        Assert.True(File.Exists(Path.Combine(_tempDir, "server_keys", "private.key")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "server_keys", "public.key")));
    }

    [Fact]
    public void Initialize_LoadsExistingKeys_WhenKeysExist()
    {
        _service.Initialize();
        var originalPrivate = _service.PrivateKey.ToArray();
        var service2 = new KeyStorageService(_tempDir);
        service2.Initialize();
        Assert.Equal(originalPrivate, service2.PrivateKey.ToArray());
    }

    [Fact]
    public void PublicKeyHex_ReturnsLowercaseHexString()
    {
        _service.Initialize();
        var hex = _service.PublicKeyHex;
        Assert.Equal(66, hex.Length);
        Assert.Equal(hex, hex.ToLower());
    }
}
