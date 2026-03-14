namespace SimpleJadePinServer.Blazor.Models;

// Represents the decrypted contents of a .pin storage file.
// Version 0x02 format (clean-slate, not compatible with Python server v0x01).
public sealed record PinData(
    byte[] PinSecretHash,   // sha256(pin_secret), 32 bytes
    byte[] StorageKey,      // HMAC-SHA256(server_random, client_entropy), 32 bytes — never revealed directly
    byte AttemptCounter,    // wrong-attempt count, 0-2; file deleted at 3
    byte[] ReplayCounter    // 4 bytes, little-endian; must increase monotonically per request
);
