using PeterO.Cbor;

namespace SimpleJadePinServer.Blazor.Crypto;

// BC-UR (Blockchain Commons Uniform Resources) encoder/decoder.
// Handles bytewords encoding, CRC32 checksums, and multi-frame fragmentation.
//
// Fragmentation rules (from original JS):
//   maxFragmentLength = 34, minFragmentLength = 10
//   Single-frame: "ur:<type>/<bytewords_with_crc>"
//   Multi-frame:  "ur:<type>/<seq>-<total>/<bytewords_with_crc>"
//   Multi-frame fragments CBOR-wrapped: [seqNum, totalFrags, totalUrLength, crc32, fragmentBytes]
public static class BcUr
{
    const int MaxFragmentLength = 34;
    const int MinFragmentLength = 10;

    // Encodes a raw payload into a single BC-UR string with no fragmentation.
    // Used for oracle config (ur:jade-updps) where the entire CBOR fits in one QR code.
    // Matches the original JS oracle_qr.html bcur_encode_object which never fragments.
    public static string EncodeSingleFrame(byte[] payload, string urType)
    {
        var withCrc = AppendCrc32(payload);
        var body = Bytewords.EncodeMinimal(withCrc);
        return $"ur:{urType}/{body}";
    }

    // Encodes a raw payload into one or more BC-UR fragment strings.
    // Small payloads produce a single fragment; large payloads are split and CBOR-wrapped.
    public static string[] Encode(byte[] payload, string urType)
    {
        var fragmentLength = CalculateFragmentLength(payload.Length);
        var fragments = SplitIntoFragments(payload, fragmentLength);

        if (fragments.Length == 1)
        {
            // Single-fragment: just append CRC32 and encode as bytewords — no CBOR wrapping needed.
            var withCrc = AppendCrc32(payload);
            var body = Bytewords.EncodeMinimal(withCrc);
            return [$"ur:{urType}/{body}"];
        }

        // Multi-fragment: each chunk is CBOR-wrapped with sequence metadata + full-payload CRC.
        var fullCrc = Crc32.Compute(payload);
        var result = new string[fragments.Length];

        for (var i = 0; i < fragments.Length; i++)
        {
            var fragment = fragments[i];

            // Pad the last fragment to the same length as others so the receiver can reassemble.
            if (fragment.Length < fragmentLength)
            {
                var padded = new byte[fragmentLength];
                fragment.CopyTo(padded, 0);
                fragment = padded;
            }

            // CBOR array: [seqNum (1-based), totalFrags, totalPayloadLength, crc32OfFullPayload, fragmentBytes]
            var cbor = CBORObject.NewArray()
                .Add(i + 1)
                .Add(fragments.Length)
                .Add(payload.Length)
                .Add((long)fullCrc)
                .Add(fragment);

            var cborBytes = cbor.EncodeToBytes();
            var withCrc = AppendCrc32(cborBytes);
            var body = Bytewords.EncodeMinimal(withCrc);
            var seq = $"{i + 1}-{fragments.Length}";
            result[i] = $"ur:{urType}/{seq}/{body}";
        }

        return result;
    }

    // Decodes an array of BC-UR fragment strings back to the original payload.
    // Handles both single-fragment (no CBOR) and multi-fragment (CBOR-wrapped) formats.
    public static byte[] Decode(string[] fragments)
    {
        var firstParts = fragments[0].Split('/');

        // Single-fragment: "ur:<type>/<bytewords>" — only 2 parts after split on '/'
        if (firstParts.Length == 2)
        {
            var decoded = Bytewords.DecodeMinimal(firstParts[1]);
            return VerifyAndStripCrc32(decoded);
        }

        // Multi-fragment: "ur:<type>/<seq>-<total>/<bytewords>"
        var seqParts = firstParts[1].Split('-');
        var totalFragments = int.Parse(seqParts[1]);
        var decodedFragments = new byte[totalFragments][];
        var totalUrLength = 0;

        for (var i = 0; i < totalFragments; i++)
        {
            var parts = fragments[i].Split('/');
            var bytewordsPayload = parts[2];
            var decodedBytes = Bytewords.DecodeMinimal(bytewordsPayload);
            var withoutCrc = VerifyAndStripCrc32(decodedBytes);
            var cbor = CBORObject.DecodeFromBytes(withoutCrc);

            // CBOR array: [seqNum, totalFrags, totalPayloadLength, crc32, fragmentBytes]
            totalUrLength = cbor[2].AsInt32();
            var fragmentData = cbor[4].GetByteString();

            // The last fragment may be padded — trim it to the actual remaining bytes.
            if (i == totalFragments - 1)
            {
                var actualLength = totalUrLength - (fragmentData.Length * (totalFragments - 1));
                fragmentData = fragmentData[..actualLength];
            }

            decodedFragments[i] = fragmentData;
        }

        // Reassemble fragments in order into the original payload buffer.
        var result = new byte[totalUrLength];
        var offset = 0;
        foreach (var frag in decodedFragments)
        {
            frag.CopyTo(result, offset);
            offset += frag.Length;
        }
        return result;
    }

    // Determines the per-fragment byte length that keeps fragments <= MaxFragmentLength
    // while not making them smaller than MinFragmentLength.
    static int CalculateFragmentLength(int totalLength)
    {
        var maxFragmentCount = (int)Math.Ceiling((double)totalLength / MinFragmentLength);
        for (var count = 1; count <= maxFragmentCount; count++)
        {
            var length = (int)Math.Ceiling((double)totalLength / count);
            if (length <= MaxFragmentLength)
                return length;
        }
        return MaxFragmentLength;
    }

    // Splits data into chunks of the given length (last chunk may be shorter).
    static byte[][] SplitIntoFragments(byte[] data, int fragmentLength)
    {
        var count = (int)Math.Ceiling((double)data.Length / fragmentLength);
        var result = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var start = i * fragmentLength;
            var length = Math.Min(fragmentLength, data.Length - start);
            result[i] = data[start..(start + length)];
        }
        return result;
    }

    // Appends a 4-byte big-endian CRC32 checksum to the data for transmission integrity.
    static byte[] AppendCrc32(byte[] data)
    {
        var crc = Crc32.Compute(data);
        var result = new byte[data.Length + 4];
        data.CopyTo(result, 0);
        // Big-endian byte order matches the BC-UR spec reference implementation.
        result[data.Length] = (byte)((crc >> 24) & 0xFF);
        result[data.Length + 1] = (byte)((crc >> 16) & 0xFF);
        result[data.Length + 2] = (byte)((crc >> 8) & 0xFF);
        result[data.Length + 3] = (byte)(crc & 0xFF);
        return result;
    }

    // Verifies the trailing 4-byte CRC32 and returns the payload without it.
    // Throws InvalidOperationException on checksum mismatch (data corruption / wrong format).
    static byte[] VerifyAndStripCrc32(byte[] data)
    {
        var payload = data[..^4];
        var expectedCrc = (uint)((data[^4] << 24) | (data[^3] << 16) | (data[^2] << 8) | data[^1]);
        var actualCrc = Crc32.Compute(payload);
        if (actualCrc != expectedCrc)
            throw new InvalidOperationException($"CRC32 mismatch: expected {expectedCrc:X8}, got {actualCrc:X8}");
        return payload;
    }
}
