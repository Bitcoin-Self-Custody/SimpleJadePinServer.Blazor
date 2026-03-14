using SimpleJadePinServer.Blazor.Models;
using SimpleJadePinServer.Blazor.Services;

namespace SimpleJadePinServer.Blazor.Tests.Services;

public class PinStorageServiceTests : IDisposable
{
    readonly string _tempDir;
    readonly PinStorageService _service;
    readonly byte[] _aesPinData;
    readonly byte[] _pinPubkey;
    readonly byte[] _pinPubkeyHash;

    public PinStorageServiceTests()
    {
        _tempDir    = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _aesPinData = new byte[32];
        Array.Fill(_aesPinData, (byte)0xAA);
        _service    = new PinStorageService(_tempDir, _aesPinData);
        _pinPubkey  = new byte[33];
        Array.Fill(_pinPubkey, (byte)0xBB);
        using var sha   = System.Security.Cryptography.SHA256.Create();
        _pinPubkeyHash  = sha.ComputeHash(_pinPubkey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesData()
    {
        var pinData = new PinData(new byte[32], new byte[32], 0, new byte[] { 0, 0, 0, 0 });
        _service.Save(_pinPubkeyHash, _pinPubkey, pinData);
        var loaded = _service.Load(_pinPubkeyHash, _pinPubkey);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(pinData.PinSecretHash, loaded.Value.PinSecretHash);
        Assert.Equal(pinData.StorageKey, loaded.Value.StorageKey);
        Assert.Equal(pinData.AttemptCounter, loaded.Value.AttemptCounter);
        Assert.Equal(pinData.ReplayCounter, loaded.Value.ReplayCounter);
    }

    [Fact]
    public void Load_FileNotFound_ReturnsFailure()
    {
        var result = _service.Load(_pinPubkeyHash, _pinPubkey);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        _service.Save(_pinPubkeyHash, _pinPubkey, new PinData(new byte[32], new byte[32], 0, new byte[4]));
        _service.Delete(_pinPubkeyHash);
        Assert.True(_service.Load(_pinPubkeyHash, _pinPubkey).IsFailure);
    }

    [Fact]
    public void Exists_ReturnsTrueWhenFilePresent()
    {
        Assert.False(_service.Exists(_pinPubkeyHash));
        _service.Save(_pinPubkeyHash, _pinPubkey, new PinData(new byte[32], new byte[32], 0, new byte[4]));
        Assert.True(_service.Exists(_pinPubkeyHash));
    }
}
