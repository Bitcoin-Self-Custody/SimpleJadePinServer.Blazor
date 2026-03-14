namespace SimpleJadePinServer.Blazor.Crypto;

// Standard CRC32 (ISO 3309 / ITU-T V.42) used by BC-UR for checksum verification.
// Uses the standard polynomial 0xEDB88320 (reversed representation of 0x04C11DB7).
public static class Crc32
{
    // Pre-computed lookup table for CRC32 — avoids per-bit computation during processing.
    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            // Apply the CRC32 polynomial 8 times (once per bit in the byte).
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    // Computes CRC32 checksum over the given data span.
    // Returns 0 for empty input, matching standard CRC32 behaviour.
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        // Process each byte: XOR into low byte of CRC, look up, shift down.
        foreach (var b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        // Final XOR inverts all bits to produce the standard CRC32 output.
        return crc ^ 0xFFFFFFFFu;
    }
}
