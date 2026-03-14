# SimpleJadePinServer.Blazor Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reimplement the SimpleJadePinServer Python blind PIN oracle as a C# Blazor Server application.

**Architecture:** Pure Blazor Server app with no REST endpoints. Blazor components call crypto/storage services via DI. JS interop handles only camera QR scanning; all BC-UR, CBOR, crypto, and QR generation runs server-side in C#.

**Tech Stack:** .NET 9, Blazor Server, NBitcoin, PeterO.Cbor, QRCoder, CSharpFunctionalExtensions, html5-qrcode (JS interop)

**Spec:** `docs/superpowers/specs/2026-03-14-blazor-pin-server-design.md`

**Repository structure:** Everything lives under `D:/docker/SimpleJadePinServer.Blazor/` — a single git repo. The test project is at `tests/SimpleJadePinServer.Blazor.Tests/` inside this repo.

---

## Chunk 1: Project Scaffolding & Core Models

### Task 1: Create Blazor Server Project

**Files:**
- Create: `SimpleJadePinServer.Blazor.csproj`
- Create: `Program.cs`
- Create: `.gitignore`
- Create: `Properties/launchSettings.json`

- [ ] **Step 1: Initialize .NET Blazor Server project**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet new blazorserver-empty --name SimpleJadePinServer.Blazor --output . --framework net9.0
```

- [ ] **Step 2: Add NuGet packages**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet add package NBitcoin
dotnet add package PeterO.Cbor
dotnet add package QRCoder
dotnet add package CSharpFunctionalExtensions
```

- [ ] **Step 3: Enable SDK container support in csproj**

Add to the `<PropertyGroup>` in `SimpleJadePinServer.Blazor.csproj`:

```xml
<EnableSdkContainerSupport>true</EnableSdkContainerSupport>
```

- [ ] **Step 4: Add key_data to .gitignore**

Append to `.gitignore`:

```
key_data/
```

- [ ] **Step 5: Configure port in launchSettings.json**

Set `applicationUrl` to `https://localhost:4443;http://localhost:4080` in `Properties/launchSettings.json`.

- [ ] **Step 6: Verify project builds**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet build
```

Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add -A
git commit -m "Scaffold Blazor Server project with dependencies"
```

### Task 2: Create Models

**Files:**
- Create: `Models/PinData.cs`
- Create: `Models/PinRequest.cs`

- [ ] **Step 1: Create PinData model**

Create `Models/PinData.cs` — represents the decrypted contents of a PIN file:

```csharp
namespace SimpleJadePinServer.Blazor.Models;

// Represents the decrypted contents of a .pin storage file.
// Version 0x02 format (clean-slate, not compatible with Python server v0x01).
public sealed record PinData(
    byte[] PinSecretHash,   // sha256(pin_secret), 32 bytes
    byte[] StorageKey,      // HMAC-SHA256(server_random, client_entropy), 32 bytes — never revealed directly
    byte AttemptCounter,    // wrong-attempt count, 0-2; file deleted at 3
    byte[] ReplayCounter    // 4 bytes, little-endian; must increase monotonically per request
);
```

- [ ] **Step 2: Create PinRequest model**

Create `Models/PinRequest.cs` — represents a parsed incoming request from Jade:

```csharp
namespace SimpleJadePinServer.Blazor.Models;

// Parsed result of a BC-UR/CBOR PIN request from Jade.
// The URL path determines whether this is a SetPin or GetPin operation.
public sealed record PinRequest(
    string[] Urls,           // URL(s) from CBOR — path ending determines set_pin vs get_pin
    byte[] EncryptedData     // base64-decoded encrypted payload (cke + replay_counter + encrypted_data)
);
```

- [ ] **Step 3: Verify build**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet build
```

- [ ] **Step 4: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Models/
git commit -m "Add PinData and PinRequest models"
```

---

## Chunk 2: BC-UR & CBOR Protocol Layer

### Task 3: Implement Bytewords Encoding/Decoding

**Files:**
- Create: `Crypto/Bytewords.cs`
- Create: `Crypto/Bytewords.Tests.cs` (or separate test project — see note)

> **Note on testing:** Create a test project `SimpleJadePinServer.Blazor.Tests` using xUnit alongside the main project. All test files reference this project.

- [ ] **Step 1: Create test project**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
mkdir -p tests/SimpleJadePinServer.Blazor.Tests
dotnet new xunit --name SimpleJadePinServer.Blazor.Tests --output tests/SimpleJadePinServer.Blazor.Tests --framework net9.0
cd tests/SimpleJadePinServer.Blazor.Tests
dotnet add reference ../../SimpleJadePinServer.Blazor.csproj
```

- [ ] **Step 2: Write Bytewords tests**

Create `tests/SimpleJadePinServer.Blazor.Tests/Crypto/BytewordsTests.cs`:

```csharp
using SimpleJadePinServer.Blazor.Crypto;

namespace SimpleJadePinServer.Blazor.Tests.Crypto;

public class BytewordsTests
{
    [Fact]
    public void EncodeMinimal_KnownBytes_ReturnsExpectedString()
    {
        // Byte 0x00 = "able" -> minimal "ae"
        // Byte 0x01 = "acid" -> minimal "ad"
        var input = new byte[] { 0x00, 0x01 };
        var result = Bytewords.EncodeMinimal(input);
        Assert.Equal("aead", result);
    }

    [Fact]
    public void DecodeMinimal_KnownString_ReturnsExpectedBytes()
    {
        // "ae" = 0x00 (able), "ad" = 0x01 (acid)
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
        // Byte 0xFF (255) = "zoom" -> minimal "zm"
        var result = Bytewords.EncodeMinimal(new byte[] { 0xFF });
        Assert.Equal("zm", result);
    }

    [Fact]
    public void EncodeMinimal_AllBytes_RoundTrips()
    {
        // Verify every possible byte value round-trips correctly
        var allBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var encoded = Bytewords.EncodeMinimal(allBytes);
        var decoded = Bytewords.DecodeMinimal(encoded);
        Assert.Equal(allBytes, decoded);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "BytewordsTests"
```

Expected: FAIL — `Bytewords` class not found.

- [ ] **Step 4: Implement Bytewords**

Create `Crypto/Bytewords.cs` in the main project:

```csharp
namespace SimpleJadePinServer.Blazor.Crypto;

// BC-UR Bytewords encoding: maps each byte to a 4-letter word from a fixed 256-word alphabet.
// Minimal encoding uses only first and last letter of each word (2 chars per byte).
// Alphabet sourced from: https://github.com/AuGoldWallet/libwally-core BC-UR spec.
public static class Bytewords
{
    // The full bytewords alphabet — 256 four-letter words concatenated (1024 chars total).
    // Index i corresponds to byte value i.
    const string Words = "ableacidalsoapexaquaarchatomauntawayaxisbackbaldbarnbeltbetabiasbluebodybragbrewbulbbuzzcalmcashcatschefcityclawcodecolacookcostcruxcurlcuspcyandarkdatadaysdelidicedietdoordowndrawdropdrumdulldutyeacheasyechoedgeepicevenexamexiteyesfactfairfernfigsfilmfishfizzflapflewfluxfoxyfreefrogfuelfundgalagamegeargemsgiftgirlglowgoodgraygrimgurugushgyrohalfhanghardhawkheathelphighhillholyhopehornhutsicedideaidleinchinkyintoirisironitemjadejazzjoinjoltjowljudojugsjumpjunkjurykeepkenokeptkeyskickkilnkingkitekiwiknoblamblavalazyleaflegsliarlimplionlistlogoloudloveluablucklungmainmanymathmazememomenumeowmildmintmissmonknailnavyneednewsnextnoonnotenumbobeyoboeomitonyxopenovalowlspaidpartpeckplaypluspoempoolposepuffpumapurrquadquizraceramprealredorichroadrockroofrubyruinrunsrustsafesagascarsetssilkskewslotsoapsolosongstubsurfswantacotasktaxitenttiedtimetinytoiltombtoystriptunatwinuglyundouniturgeuservastveryvetovialvibeviewvisavoidvowswallwandwarmwaspwavewaxywebswhatwhenwhizwolfworkyankyawnyellyogayurtzapszerozestzinczonezoom";

    // Lookup table: index = (lastCharIndex * 26 + firstCharIndex), value = byte value.
    // Built lazily on first decode call.
    static readonly int[] LookupTable = BuildLookupTable();

    static int[] BuildLookupTable()
    {
        var table = new int[26 * 26];
        Array.Fill(table, -1);

        for (var i = 0; i < 256; i++)
        {
            var firstChar = Words[i * 4] - 'a';
            var lastChar = Words[i * 4 + 3] - 'a';
            table[lastChar * 26 + firstChar] = i;
        }

        return table;
    }

    // Encode bytes to minimal bytewords (2 chars per byte: first + last letter of each word).
    public static string EncodeMinimal(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length * 2];

        for (var i = 0; i < data.Length; i++)
        {
            var wordIndex = data[i] * 4;
            chars[i * 2] = Words[wordIndex];
            chars[i * 2 + 1] = Words[wordIndex + 3];
        }

        return new string(chars);
    }

    // Decode minimal bytewords string (2 chars per byte) back to bytes.
    public static byte[] DecodeMinimal(string encoded)
    {
        var result = new byte[encoded.Length / 2];

        for (var i = 0; i < result.Length; i++)
        {
            var first = char.ToLower(encoded[i * 2]) - 'a';
            var last = char.ToLower(encoded[i * 2 + 1]) - 'a';
            var value = LookupTable[last * 26 + first];

            if (value < 0)
                throw new ArgumentException($"Invalid byteword pair: {encoded[i * 2]}{encoded[i * 2 + 1]}");

            result[i] = (byte)value;
        }

        return result;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "BytewordsTests"
```

Expected: All 3 tests PASS.

- [ ] **Step 6: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor && git add -A && git commit -m "Add Bytewords encoding/decoding with tests"
```

### Task 4: Implement CRC32 Utility

**Files:**
- Create: `Crypto/Crc32.cs`

- [ ] **Step 1: Write CRC32 tests**

Create `D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests/Crypto/Crc32Tests.cs`:

```csharp
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
        // CRC32 of "123456789" (ASCII bytes) = 0xCBF43926
        var input = "123456789"u8.ToArray();
        var result = Crc32.Compute(input);
        Assert.Equal(0xCBF43926u, result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "Crc32Tests"
```

- [ ] **Step 3: Implement CRC32**

Create `Crypto/Crc32.cs`:

```csharp
namespace SimpleJadePinServer.Blazor.Crypto;

// Standard CRC32 (ISO 3309 / ITU-T V.42) used by BC-UR for checksum verification.
// Polynomial: 0xEDB88320 (reversed representation of 0x04C11DB7).
public static class Crc32
{
    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            var c = n;

            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;

            table[n] = c;
        }

        return table;
    }

    // Compute CRC32 checksum of the given data.
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;

        foreach (var b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];

        return crc ^ 0xFFFFFFFF;
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "Crc32Tests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Crypto/Crc32.cs
cd D:/docker/SimpleJadePinServer.Blazor && git add -A && git commit -m "Add CRC32 utility for BC-UR checksums"
```

### Task 5: Implement BC-UR Encode/Decode

**Files:**
- Create: `Crypto/BcUr.cs`

- [ ] **Step 1: Write BC-UR tests**

Create `D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests/Crypto/BcUrTests.cs`:

```csharp
using SimpleJadePinServer.Blazor.Crypto;

namespace SimpleJadePinServer.Blazor.Tests.Crypto;

public class BcUrTests
{
    [Fact]
    public void Encode_SmallPayload_ReturnsSingleFragment()
    {
        // Small payload should produce a single "ur:jade-pin/..." string with no seq/total
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var fragments = BcUr.Encode(payload, "jade-pin");

        Assert.Single(fragments);
        Assert.StartsWith("ur:jade-pin/", fragments[0]);
        // No seq-total separator in single-fragment encoding
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
        // Create a payload large enough to require multiple fragments
        // (maxFragmentLength = 34 bytes in the original JS)
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
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "BcUrTests"
```

- [ ] **Step 3: Implement BcUr**

Create `Crypto/BcUr.cs`:

```csharp
using PeterO.Cbor;

namespace SimpleJadePinServer.Blazor.Crypto;

// BC-UR (Blockchain Commons Uniform Resources) encoder/decoder.
// Handles bytewords encoding, CRC32 checksums, and multi-frame fragmentation
// for QR code transport between Jade hardware wallet and this server.
//
// Fragmentation rules (from original JS implementation):
//   - maxFragmentLength = 34 bytes per fragment
//   - minFragmentLength = 10 bytes per fragment
//   - Single-frame: "ur:<type>/<bytewords_with_crc>"
//   - Multi-frame:  "ur:<type>/<seq>-<total>/<bytewords_with_crc>"
//   - Multi-frame fragments are individually CBOR-wrapped as arrays:
//     [seqNum, totalFragments, totalUrLength, crc32OfFullUr, fragmentBytes]
public static class BcUr
{
    const int MaxFragmentLength = 34;
    const int MinFragmentLength = 10;

    // Encode raw bytes into BC-UR fragment strings ready for QR code rendering.
    public static string[] Encode(byte[] payload, string urType)
    {
        var fragmentLength = CalculateFragmentLength(payload.Length);
        var fragments = SplitIntoFragments(payload, fragmentLength);

        if (fragments.Length == 1)
        {
            // Single fragment: ur:<type>/<bytewords_with_crc>
            var withCrc = AppendCrc32(payload);
            var body = Bytewords.EncodeMinimal(withCrc);
            return [$"ur:{urType}/{body}"];
        }

        // Multi-frame: each fragment wrapped in CBOR array with metadata
        var fullCrc = Crc32.Compute(payload);
        var result = new string[fragments.Length];

        for (var i = 0; i < fragments.Length; i++)
        {
            // Pad last fragment to uniform length (matches original JS behavior)
            var fragment = fragments[i];
            if (fragment.Length < fragmentLength)
            {
                var padded = new byte[fragmentLength];
                fragment.CopyTo(padded, 0);
                fragment = padded;
            }

            // CBOR array: [seqNum (1-based), totalFragments, totalUrLength, crc32, fragmentBytes]
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

    // Decode BC-UR fragment strings back to raw bytes.
    // All fragments must be provided for multi-frame payloads.
    public static byte[] Decode(string[] fragments)
    {
        var firstParts = fragments[0].Split('/');
        // firstParts[0] = "ur:<type>"

        if (firstParts.Length == 2)
        {
            // Single fragment: ur:<type>/<bytewords_with_crc>
            var decoded = Bytewords.DecodeMinimal(firstParts[1]);
            return VerifyAndStripCrc32(decoded);
        }

        // Multi-frame: ur:<type>/<seq>-<total>/<bytewords_with_crc>
        var seqParts = firstParts[1].Split('-');
        var totalFragments = int.Parse(seqParts[1]);
        var decodedFragments = new byte[totalFragments][];
        var totalUrLength = 0;

        for (var i = 0; i < totalFragments; i++)
        {
            var parts = fragments[i].Split('/');
            var bytewordsPayload = parts[2];

            // Strip CRC32 (last 4 bytes after bytewords decode)
            var decodedBytes = Bytewords.DecodeMinimal(bytewordsPayload);
            var withoutCrc = VerifyAndStripCrc32(decodedBytes);

            // Decode CBOR array: [seqNum, totalFrags, urLength, crc32, fragmentBytes]
            var cbor = CBORObject.DecodeFromBytes(withoutCrc);
            totalUrLength = cbor[2].AsInt32();
            var fragmentData = cbor[4].GetByteString();

            // Last fragment may be padded — trim to actual size
            if (i == totalFragments - 1)
            {
                var actualLength = totalUrLength - (fragmentData.Length * (totalFragments - 1));
                fragmentData = fragmentData[..actualLength];
            }

            decodedFragments[i] = fragmentData;
        }

        // Concatenate all fragments
        var result = new byte[totalUrLength];
        var offset = 0;

        foreach (var frag in decodedFragments)
        {
            frag.CopyTo(result, offset);
            offset += frag.Length;
        }

        return result;
    }

    // Determine optimal fragment size given total payload length.
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

    // Split payload into fragments of the given length.
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

    static byte[] AppendCrc32(byte[] data)
    {
        var crc = Crc32.Compute(data);
        var result = new byte[data.Length + 4];
        data.CopyTo(result, 0);
        result[data.Length] = (byte)((crc >> 24) & 0xFF);
        result[data.Length + 1] = (byte)((crc >> 16) & 0xFF);
        result[data.Length + 2] = (byte)((crc >> 8) & 0xFF);
        result[data.Length + 3] = (byte)(crc & 0xFF);
        return result;
    }

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
```

- [ ] **Step 4: Run tests**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "BcUrTests"
```

Expected: All PASS.

- [ ] **Step 5: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Crypto/BcUr.cs
cd D:/docker/SimpleJadePinServer.Blazor && git add -A && git commit -m "Add BC-UR encode/decode with multi-frame support"
```

### Task 6: Implement CBOR Protocol Helpers

**Files:**
- Create: `Crypto/CborProtocol.cs`

- [ ] **Step 1: Write CBOR protocol tests**

Create `D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests/Crypto/CborProtocolTests.cs`:

```csharp
using SimpleJadePinServer.Blazor.Crypto;
using SimpleJadePinServer.Blazor.Models;

namespace SimpleJadePinServer.Blazor.Tests.Crypto;

public class CborProtocolTests
{
    [Fact]
    public void ParsePinRequest_ValidCbor_ExtractsUrlsAndData()
    {
        // Build a CBOR structure matching what Jade sends:
        // { "result": { "http_request": { "params": { "urls": ["http://server/set_pin"], "data": { "data": "AQID" } } } } }
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
        var responseData = "AQID";
        var cbor = CborProtocol.BuildPinResponse(responseData);

        Assert.NotNull(cbor);
        Assert.True(cbor.Length > 0);

        // Round-trip verify: decode and check fields
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
        Assert.Equal("02abcdef1234567890", decoded["params"]["pubkey"].AsString());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "CborProtocolTests"
```

- [ ] **Step 3: Implement CborProtocol**

Create `Crypto/CborProtocol.cs`:

```csharp
using CSharpFunctionalExtensions;
using PeterO.Cbor;
using SimpleJadePinServer.Blazor.Models;

namespace SimpleJadePinServer.Blazor.Crypto;

// Jade-specific CBOR message encoding/decoding.
// Handles the exact CBOR map structures that Jade firmware produces and consumes.
public static class CborProtocol
{
    // Parse a Jade PIN request from raw CBOR bytes.
    // Expected structure:
    // { "result": { "http_request": { "params": { "urls": [...], "data": { "data": "<base64>" } } } } }
    public static Result<PinRequest> ParsePinRequest(byte[] cborBytes)
    {
        try
        {
            var cbor = CBORObject.DecodeFromBytes(cborBytes);
            var httpParams = cbor["result"]["http_request"]["params"];

            // urls is an array of strings
            var urlsArray = httpParams["urls"];
            var urls = new string[urlsArray.Count];
            for (var i = 0; i < urlsArray.Count; i++)
                urls[i] = urlsArray[i].AsString();

            // data.data is base64-encoded encrypted payload
            var base64Data = httpParams["data"]["data"].AsString();
            var encryptedData = Convert.FromBase64String(base64Data);

            return Result.Success(new PinRequest(urls, encryptedData));
        }
        catch (Exception ex)
        {
            return Result.Failure<PinRequest>($"Failed to parse PIN request CBOR: {ex.Message}");
        }
    }

    // Build a PIN response CBOR message for Jade.
    // Structure: { "id": "0", "method": "pin", "params": { "data": "<base64>" } }
    public static byte[] BuildPinResponse(string base64Data) =>
        CBORObject.NewMap()
            .Add("id", "0")
            .Add("method", "pin")
            .Add("params", CBORObject.NewMap().Add("data", base64Data))
            .EncodeToBytes();

    // Build an oracle configuration CBOR message for Jade pairing.
    // Structure: { "id": "001", "method": "update_pinserver", "params": { "urlA": ..., "urlB": ..., "pubkey": ... } }
    public static byte[] BuildOracleConfig(string urlA, string urlB, string pubkeyHex) =>
        CBORObject.NewMap()
            .Add("id", "001")
            .Add("method", "update_pinserver")
            .Add("params", CBORObject.NewMap()
                .Add("urlA", urlA)
                .Add("urlB", urlB)
                .Add("pubkey", pubkeyHex))
            .EncodeToBytes();

    // Helper for testing: build a mock PIN request CBOR structure.
    public static byte[] BuildMockPinRequest(string[] urls, string base64Data)
    {
        var urlsArray = CBORObject.NewArray();
        foreach (var url in urls)
            urlsArray.Add(url);

        return CBORObject.NewMap()
            .Add("result", CBORObject.NewMap()
                .Add("http_request", CBORObject.NewMap()
                    .Add("params", CBORObject.NewMap()
                        .Add("urls", urlsArray)
                        .Add("data", CBORObject.NewMap().Add("data", base64Data)))))
            .EncodeToBytes();
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "CborProtocolTests"
```

Expected: All PASS.

- [ ] **Step 5: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Crypto/CborProtocol.cs
cd D:/docker/SimpleJadePinServer.Blazor && git add -A && git commit -m "Add CBOR protocol helpers for Jade wire format"
```

---

## Chunk 3: Storage Services

### Task 7: Implement KeyStorageService

**Files:**
- Create: `Services/KeyStorageService.cs`

- [ ] **Step 1: Write KeyStorageService tests**

Create `D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests/Services/KeyStorageServiceTests.cs`:

```csharp
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

    public void Dispose() => Directory.Delete(_tempDir, true);

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

        // Create a new service pointing to same directory
        var service2 = new KeyStorageService(_tempDir);
        service2.Initialize();

        Assert.Equal(originalPrivate, service2.PrivateKey.ToArray());
    }

    [Fact]
    public void PublicKeyHex_ReturnsLowercaseHexString()
    {
        _service.Initialize();

        var hex = _service.PublicKeyHex;
        Assert.Equal(66, hex.Length); // 33 bytes = 66 hex chars
        Assert.Equal(hex, hex.ToLower());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "KeyStorageServiceTests"
```

- [ ] **Step 3: Implement KeyStorageService**

Create `Services/KeyStorageService.cs`:

```csharp
using NBitcoin.Secp256k1;

namespace SimpleJadePinServer.Blazor.Services;

// Manages the server's static EC keypair (secp256k1).
// Auto-generates on first run, persists to key_data/server_keys/.
// The private key is used for ECDH session key derivation and PIN file encryption.
// The public key is shared with Jade during oracle pairing.
public sealed class KeyStorageService
{
    readonly string _basePath;
    string ServerKeysPath => Path.Combine(_basePath, "server_keys");
    string PrivateKeyPath => Path.Combine(ServerKeysPath, "private.key");
    string PublicKeyPath => Path.Combine(ServerKeysPath, "public.key");

    byte[] _privateKey = [];
    byte[] _publicKey = [];

    // Derived key for PIN data encryption: HMAC-SHA256(private_key, "pin_data")
    byte[] _aesPinData = [];

    public ReadOnlySpan<byte> PrivateKey => _privateKey;
    public ReadOnlySpan<byte> PublicKey => _publicKey;
    public ReadOnlySpan<byte> AesPinData => _aesPinData;

    // Lowercase hex representation of the compressed public key (66 chars).
    public string PublicKeyHex => Convert.ToHexString(_publicKey).ToLower();

    public KeyStorageService(string basePath) => _basePath = basePath;

    // Load existing keys or generate new ones. Must be called at startup.
    public void Initialize()
    {
        Directory.CreateDirectory(ServerKeysPath);

        if (File.Exists(PrivateKeyPath))
        {
            _privateKey = File.ReadAllBytes(PrivateKeyPath);
        }
        else
        {
            _privateKey = GeneratePrivateKey();
            File.WriteAllBytes(PrivateKeyPath, _privateKey);
        }

        // Derive public key from private key
        ECPrivKey.TryCreate(new ReadOnlySpan<byte>(_privateKey), out var ecPrivKey);
        var pubKey = ecPrivKey!.CreatePubKey();
        _publicKey = new byte[33];
        pubKey.WriteToSpan(true, _publicKey, out _);
        File.WriteAllBytes(PublicKeyPath, _publicKey);

        // Derive the intermediate key for PIN data encryption
        using var hmac = new System.Security.Cryptography.HMACSHA256(_privateKey);
        _aesPinData = hmac.ComputeHash("pin_data"u8.ToArray());
    }

    // Generate a valid secp256k1 private key (32 random bytes, verified).
    static byte[] GeneratePrivateKey()
    {
        var key = new byte[32];

        while (true)
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(key);
            if (ECPrivKey.TryCreate(key, out _))
                return key;
        }
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "KeyStorageServiceTests"
```

Expected: All PASS.

- [ ] **Step 5: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Services/KeyStorageService.cs
cd D:/docker/SimpleJadePinServer.Blazor && git add -A && git commit -m "Add KeyStorageService with auto-generation and persistence"
```

### Task 8: Implement PinStorageService

**Files:**
- Create: `Services/PinStorageService.cs`

- [ ] **Step 1: Write PinStorageService tests**

Create `D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests/Services/PinStorageServiceTests.cs`:

```csharp
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
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // Use a deterministic key for testing
        _aesPinData = new byte[32];
        Array.Fill(_aesPinData, (byte)0xAA);

        _service = new PinStorageService(_tempDir, _aesPinData);

        // Mock pubkey and hash
        _pinPubkey = new byte[33];
        Array.Fill(_pinPubkey, (byte)0xBB);
        using var sha = System.Security.Cryptography.SHA256.Create();
        _pinPubkeyHash = sha.ComputeHash(_pinPubkey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesData()
    {
        var pinData = new PinData(
            PinSecretHash: new byte[32],
            StorageKey: new byte[32],
            AttemptCounter: 0,
            ReplayCounter: new byte[] { 0, 0, 0, 0 }
        );

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
        var pinData = new PinData(new byte[32], new byte[32], 0, new byte[4]);
        _service.Save(_pinPubkeyHash, _pinPubkey, pinData);

        _service.Delete(_pinPubkeyHash);

        var result = _service.Load(_pinPubkeyHash, _pinPubkey);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Exists_ReturnsTrueWhenFilePresent()
    {
        Assert.False(_service.Exists(_pinPubkeyHash));

        _service.Save(_pinPubkeyHash, _pinPubkey, new PinData(new byte[32], new byte[32], 0, new byte[4]));

        Assert.True(_service.Exists(_pinPubkeyHash));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "PinStorageServiceTests"
```

- [ ] **Step 3: Implement PinStorageService**

Create `Services/PinStorageService.cs`:

```csharp
using System.Security.Cryptography;
using CSharpFunctionalExtensions;
using SimpleJadePinServer.Blazor.Models;

namespace SimpleJadePinServer.Blazor.Services;

// Reads/writes/deletes PIN files in key_data/pins/.
// File format v0x02 (clean-slate):
//   [1 byte version] [32 bytes HMAC auth tag] [16 bytes IV] [N bytes AES-CBC encrypted payload]
// Encrypted payload:
//   [32 bytes pin_secret_hash] [32 bytes storage_key] [1 byte attempt_counter] [4 bytes replay_counter LE]
public sealed class PinStorageService
{
    const byte Version = 0x02;
    const int PlaintextLength = 32 + 32 + 1 + 4; // 69 bytes

    readonly string _pinsPath;
    readonly byte[] _aesPinData; // HMAC-SHA256(server_private_key, "pin_data")

    public PinStorageService(string basePath, byte[] aesPinData)
    {
        _pinsPath = Path.Combine(basePath, "pins");
        _aesPinData = aesPinData;
    }

    string GetFilePath(byte[] pinPubkeyHash) =>
        Path.Combine(_pinsPath, $"{Convert.ToHexString(pinPubkeyHash).ToLower()}.pin");

    public bool Exists(byte[] pinPubkeyHash) =>
        File.Exists(GetFilePath(pinPubkeyHash));

    // Save PIN data to an encrypted file.
    // storage_aes_key = HMAC-SHA256(aesPinData, pinPubkey)
    // pin_auth_key = HMAC-SHA256(aesPinData, pinPubkeyHash)
    public void Save(byte[] pinPubkeyHash, byte[] pinPubkey, PinData pinData)
    {
        Directory.CreateDirectory(_pinsPath);

        var storageAesKey = HmacSha256(_aesPinData, pinPubkey);
        var pinAuthKey = HmacSha256(_aesPinData, pinPubkeyHash);

        // Build plaintext: hash + key + counter + replay
        var plaintext = new byte[PlaintextLength];
        pinData.PinSecretHash.CopyTo(plaintext, 0);
        pinData.StorageKey.CopyTo(plaintext, 32);
        plaintext[64] = pinData.AttemptCounter;
        pinData.ReplayCounter.CopyTo(plaintext, 65);

        // Encrypt with AES-CBC
        var iv = RandomNumberGenerator.GetBytes(16);
        var encrypted = AesCbcEncrypt(storageAesKey, iv, plaintext);

        // HMAC auth tag covers: version + IV + encrypted
        var versionByte = new[] { Version };
        var authPayload = new byte[1 + iv.Length + encrypted.Length];
        authPayload[0] = Version;
        iv.CopyTo(authPayload, 1);
        encrypted.CopyTo(authPayload, 1 + iv.Length);
        var authTag = HmacSha256(pinAuthKey, authPayload);

        // Write file: version + authTag + IV + encrypted
        using var fs = File.Create(GetFilePath(pinPubkeyHash));
        fs.Write(versionByte);
        fs.Write(authTag);
        fs.Write(iv);
        fs.Write(encrypted);
    }

    // Load and decrypt PIN data from file. Returns failure if file not found or auth fails.
    public Result<PinData> Load(byte[] pinPubkeyHash, byte[] pinPubkey)
    {
        var path = GetFilePath(pinPubkeyHash);
        if (!File.Exists(path))
            return Result.Failure<PinData>("PIN file not found");

        var data = File.ReadAllBytes(path);
        if (data.Length < 1 + 32 + 16 + 16) // minimum: version + hmac + iv + 1 block
            return Result.Failure<PinData>("PIN file too short");

        var version = data[0];
        if (version != Version)
            return Result.Failure<PinData>($"Unsupported PIN file version: {version}");

        var hmacReceived = data[1..33];
        var iv = data[33..49];
        var encrypted = data[49..];

        // Verify HMAC
        var pinAuthKey = HmacSha256(_aesPinData, pinPubkeyHash);
        var authPayload = new byte[1 + iv.Length + encrypted.Length];
        authPayload[0] = version;
        iv.CopyTo(authPayload, 1);
        encrypted.CopyTo(authPayload, 1 + iv.Length);
        var hmacComputed = HmacSha256(pinAuthKey, authPayload);

        if (!CryptographicOperations.FixedTimeEquals(hmacReceived, hmacComputed))
            return Result.Failure<PinData>("PIN file HMAC verification failed");

        // Decrypt
        var storageAesKey = HmacSha256(_aesPinData, pinPubkey);
        var plaintext = AesCbcDecrypt(storageAesKey, iv, encrypted);

        if (plaintext.Length != PlaintextLength)
            return Result.Failure<PinData>($"Decrypted payload unexpected length: {plaintext.Length}");

        return Result.Success(new PinData(
            PinSecretHash: plaintext[..32],
            StorageKey: plaintext[32..64],
            AttemptCounter: plaintext[64],
            ReplayCounter: plaintext[65..69]
        ));
    }

    public void Delete(byte[] pinPubkeyHash)
    {
        var path = GetFilePath(pinPubkeyHash);
        if (File.Exists(path))
            File.Delete(path);
    }

    static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.CreateEncryptor().TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.CreateDecryptor().TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "PinStorageServiceTests"
```

Expected: All PASS.

- [ ] **Step 5: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Services/PinStorageService.cs
cd D:/docker/SimpleJadePinServer.Blazor && git add -A && git commit -m "Add PinStorageService with encrypted file storage"
```

---

## Chunk 4: Crypto Service

### Task 9: Implement PinCryptoService

**Files:**
- Create: `Services/PinCryptoService.cs`

This is the highest-risk component. It must replicate wallycore's ECDH + AES-CBC composite function exactly.

- [ ] **Step 1: Write PinCryptoService unit tests**

Create `D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests/Services/PinCryptoServiceTests.cs`:

```csharp
using NBitcoin.Secp256k1;
using SimpleJadePinServer.Blazor.Services;
using System.Security.Cryptography;

namespace SimpleJadePinServer.Blazor.Tests.Services;

public class PinCryptoServiceTests
{
    [Fact]
    public void DeriveSessionKey_DifferentReplayCounters_ProduceDifferentKeys()
    {
        // Generate a test server key
        var serverKey = new byte[32];
        RandomNumberGenerator.Fill(serverKey);
        while (!ECPrivKey.TryCreate(serverKey, out _))
            RandomNumberGenerator.Fill(serverKey);

        var cke = new byte[33];
        ECPrivKey.TryCreate(RandomNumberGenerator.GetBytes(32), out var tempKey);
        tempKey!.CreatePubKey().WriteToSpan(true, cke, out _);

        var counter1 = new byte[] { 1, 0, 0, 0 };
        var counter2 = new byte[] { 2, 0, 0, 0 };

        var key1 = PinCryptoService.DeriveSessionKey(serverKey, cke, counter1);
        var key2 = PinCryptoService.DeriveSessionKey(serverKey, cke, counter2);

        // Different counters must produce different session keys
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_PreservesPayload()
    {
        // Generate server and client keys
        var serverPriv = GenerateValidKey();
        var clientPriv = GenerateValidKey();
        ECPrivKey.TryCreate(clientPriv, out var clientEcKey);
        var clientPub = new byte[33];
        clientEcKey!.CreatePubKey().WriteToSpan(true, clientPub, out _);

        var plaintext = RandomNumberGenerator.GetBytes(64);

        // Encrypt with server key
        var iv = RandomNumberGenerator.GetBytes(16);
        var encrypted = PinCryptoService.AesCbcWithEcdh(
            serverPriv, iv, plaintext, clientPub, "test_context", encrypt: true);

        // Decrypt with same keys
        var decrypted = PinCryptoService.AesCbcWithEcdh(
            serverPriv, null, encrypted, clientPub, "test_context", encrypt: false);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void HmacSha256_KnownInput_ProducesConsistentOutput()
    {
        var key = new byte[32];
        var data = "pin_data"u8.ToArray();
        var result1 = PinCryptoService.ComputeHmacSha256(key, data);
        var result2 = PinCryptoService.ComputeHmacSha256(key, data);
        Assert.Equal(result1, result2);
        Assert.Equal(32, result1.Length);
    }

    static byte[] GenerateValidKey()
    {
        var key = new byte[32];
        while (true)
        {
            RandomNumberGenerator.Fill(key);
            if (ECPrivKey.TryCreate(key, out _))
                return key;
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "PinCryptoServiceTests"
```

- [ ] **Step 3: Implement PinCryptoService**

Create `Services/PinCryptoService.cs`:

```csharp
using System.Security.Cryptography;
using CSharpFunctionalExtensions;
using NBitcoin.Secp256k1;
using SimpleJadePinServer.Blazor.Models;

namespace SimpleJadePinServer.Blazor.Services;

// Core blind oracle crypto protocol. Reimplements wallycore's operations using NBitcoin.
//
// Key operations:
// - Session key derivation via BIP341 tweaking
// - ECDH + AES-CBC encrypt/decrypt (replicates wallycore's aes_cbc_with_ecdh_key)
// - EC signature recovery for PIN identity
// - SetPin / GetPin orchestration
//
// CRITICAL: The ECDH key derivation must match wallycore exactly or Jade will reject responses.
public sealed class PinCryptoService
{
    readonly KeyStorageService _keyStorage;
    readonly PinStorageService _pinStorage;

    public PinCryptoService(KeyStorageService keyStorage, PinStorageService pinStorage)
    {
        _keyStorage = keyStorage;
        _pinStorage = pinStorage;
    }

    // Process a SetPin request: register a new PIN or re-register an existing one.
    // Input: raw encrypted data from Jade (cke + replay_counter + encrypted_payload).
    // Returns: encrypted AES key response for Jade.
    public Result<byte[]> SetPin(byte[] data)
    {
        if (data.Length <= 37)
            return Result.Failure<byte[]>("SetPin data too short");

        var cke = data[..33];               // client ephemeral public key
        var replayCounter = data[33..37];   // 4-byte little-endian replay counter
        var encryptedData = data[37..];

        // Derive session key via BIP341 tweak
        var sessionKey = DeriveSessionKey(_keyStorage.PrivateKey.ToArray(), cke, replayCounter);

        // Decrypt request payload
        var payload = AesCbcWithEcdh(sessionKey, null, encryptedData, cke, "blind_oracle_request", encrypt: false);
        if (payload.Length != 129) // 32 pin_secret + 32 entropy + 65 signature
            return Result.Failure<byte[]>($"SetPin payload unexpected length: {payload.Length}");

        var pinSecret = payload[..32];
        var entropy = payload[32..64];
        var signature = payload[64..];

        // Recover signer's public key from signature
        var signedMsg = SHA256.HashData([.. cke, .. replayCounter, .. pinSecret, .. entropy]);
        var pinPubkeyResult = RecoverPublicKey(signedMsg, signature);
        if (pinPubkeyResult.IsFailure)
            return Result.Failure<byte[]>(pinPubkeyResult.Error);

        var pinPubkey = pinPubkeyResult.Value;
        var pinPubkeyHash = SHA256.HashData(pinPubkey);

        // Replay counter check (only if PIN file already exists)
        if (_pinStorage.Exists(pinPubkeyHash))
        {
            var existing = _pinStorage.Load(pinPubkeyHash, pinPubkey);
            if (existing.IsSuccess)
            {
                var clientCounter = BitConverter.ToUInt32(replayCounter);
                var serverCounter = BitConverter.ToUInt32(existing.Value.ReplayCounter);
                if (clientCounter <= serverCounter)
                    return Result.Failure<byte[]>("Replay counter violation");
            }
        }

        // Generate storage key from server randomness + client entropy
        var serverRandom = RandomNumberGenerator.GetBytes(32);
        var newKey = ComputeHmacSha256(serverRandom, entropy);

        // Save PIN file with replay counter reset to 0
        var pinData = new PinData(
            PinSecretHash: SHA256.HashData(pinSecret),
            StorageKey: newKey,
            AttemptCounter: 0,
            ReplayCounter: new byte[4] // reset to 0
        );
        _pinStorage.Save(pinPubkeyHash, pinPubkey, pinData);

        // Blind oracle response: HMAC(new_key, pin_secret) — never reveal new_key directly
        var aesKey = ComputeHmacSha256(newKey, pinSecret);

        // Encrypt response
        var iv = RandomNumberGenerator.GetBytes(16);
        var encryptedKey = AesCbcWithEcdh(sessionKey, iv, aesKey, cke, "blind_oracle_response", encrypt: true);

        return Result.Success(encryptedKey);
    }

    // Process a GetPin request: unlock attempt.
    // Returns encrypted AES key (real if correct PIN, random if wrong).
    public Result<byte[]> GetPin(byte[] data)
    {
        if (data.Length <= 37)
            return Result.Failure<byte[]>("GetPin data too short");

        var cke = data[..33];
        var replayCounter = data[33..37];
        var encryptedData = data[37..];

        var sessionKey = DeriveSessionKey(_keyStorage.PrivateKey.ToArray(), cke, replayCounter);

        var payload = AesCbcWithEcdh(sessionKey, null, encryptedData, cke, "blind_oracle_request", encrypt: false);
        if (payload.Length != 97) // 32 pin_secret + 65 signature
            return Result.Failure<byte[]>($"GetPin payload unexpected length: {payload.Length}");

        var pinSecret = payload[..32];
        var signature = payload[32..];

        var signedMsg = SHA256.HashData([.. cke, .. replayCounter, .. pinSecret]);
        var pinPubkeyResult = RecoverPublicKey(signedMsg, signature);
        if (pinPubkeyResult.IsFailure)
            return Result.Failure<byte[]>(pinPubkeyResult.Error);

        var pinPubkey = pinPubkeyResult.Value;
        var pinPubkeyHash = SHA256.HashData(pinPubkey);

        byte[] savedKey;
        var loadResult = _pinStorage.Load(pinPubkeyHash, pinPubkey);

        if (loadResult.IsFailure)
        {
            // PIN not found — return random key (indistinguishable from wrong PIN)
            savedKey = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            var pinData = loadResult.Value;

            // Replay counter check
            var clientCounter = BitConverter.ToUInt32(replayCounter);
            var serverCounter = BitConverter.ToUInt32(pinData.ReplayCounter);
            if (clientCounter <= serverCounter)
                return Result.Failure<byte[]>("Replay counter violation");

            var hashPinSecret = SHA256.HashData(pinSecret);

            if (CryptographicOperations.FixedTimeEquals(hashPinSecret, pinData.PinSecretHash))
            {
                // Correct PIN — reset attempt counter, save client's replay counter
                savedKey = pinData.StorageKey;
                var updated = new PinData(pinData.PinSecretHash, savedKey, 0, replayCounter);
                _pinStorage.Save(pinPubkeyHash, pinPubkey, updated);
            }
            else
            {
                // Wrong PIN — increment attempt counter, persist both counter and replay
                var newAttempts = (byte)(pinData.AttemptCounter + 1);

                if (newAttempts >= 3)
                {
                    // Too many wrong attempts — delete PIN file (irreversible)
                    _pinStorage.Delete(pinPubkeyHash);
                }
                else
                {
                    var updated = new PinData(pinData.PinSecretHash, pinData.StorageKey, newAttempts, replayCounter);
                    _pinStorage.Save(pinPubkeyHash, pinPubkey, updated);
                }

                // Return random key (no oracle leakage)
                savedKey = RandomNumberGenerator.GetBytes(32);
            }
        }

        // Blind oracle: HMAC(saved_key, pin_secret)
        var aesKey = ComputeHmacSha256(savedKey, pinSecret);
        var iv = RandomNumberGenerator.GetBytes(16);
        var encryptedKey = AesCbcWithEcdh(sessionKey, iv, aesKey, cke, "blind_oracle_response", encrypt: true);

        return Result.Success(encryptedKey);
    }

    // BIP341 tweak derivation for per-request session key.
    // tweak = sha256(hmac_sha256(cke, replay_counter))
    // session_key = BIP341_TWEAK(server_private_key, tweak)
    public static byte[] DeriveSessionKey(byte[] serverPrivateKey, byte[] cke, byte[] replayCounter)
    {
        var hmac = ComputeHmacSha256(cke, replayCounter);
        var tweak = SHA256.HashData(hmac);

        ECPrivKey.TryCreate(serverPrivateKey, out var ecKey);
        var tweakScalar = new Scalar(tweak, out _);
        var tweaked = ecKey!.TweakAdd(tweakScalar.IsZero ? throw new ArgumentException("Invalid tweak") : tweakScalar);
        var result = new byte[32];
        tweaked.WriteToSpan(result);
        return result;
    }

    // Replicates wallycore's aes_cbc_with_ecdh_key:
    // 1. ECDH shared secret (x-coordinate)
    // 2. Key derivation: HMAC-SHA256(shared_secret_x, context_label)
    // 3. AES-CBC encrypt/decrypt
    //
    // When decrypting, IV is extracted from first 16 bytes of data.
    // When encrypting, IV is provided and prepended to output.
    public static byte[] AesCbcWithEcdh(byte[] privateKey, byte[]? iv, byte[] data,
        byte[] publicKey, string contextLabel, bool encrypt)
    {
        // ECDH: compute shared point, take x-coordinate
        ECPrivKey.TryCreate(privateKey, out var ecPriv);
        ECPubKey.TryCreate(publicKey, out _, out var ecPub);
        var sharedPoint = ecPub!.GetSharedPubkey(ecPriv!);
        var sharedBytes = new byte[33];
        sharedPoint.WriteToSpan(true, sharedBytes, out _);
        var sharedX = sharedBytes[1..33]; // skip prefix byte, take x-coordinate

        // Key derivation: HMAC-SHA256(shared_x, context_label)
        var aesKey = ComputeHmacSha256(sharedX, System.Text.Encoding.UTF8.GetBytes(contextLabel));

        if (encrypt)
        {
            var encrypted = AesCbcEncrypt(aesKey, iv!, data);
            // Prepend IV to output (matches wallycore behavior)
            var result = new byte[iv!.Length + encrypted.Length];
            iv.CopyTo(result, 0);
            encrypted.CopyTo(result, iv.Length);
            return result;
        }
        else
        {
            // IV is first 16 bytes of data when decrypting
            var extractedIv = data[..16];
            var ciphertext = data[16..];
            return AesCbcDecrypt(aesKey, extractedIv, ciphertext);
        }
    }

    // Recover EC public key from a 65-byte recoverable ECDSA signature.
    // Format: [1 byte recovery flag] [32 bytes r] [32 bytes s]
    public static Result<byte[]> RecoverPublicKey(byte[] message, byte[] signature)
    {
        try
        {
            if (signature.Length != 65)
                return Result.Failure<byte[]>($"Signature must be 65 bytes, got {signature.Length}");

            var recId = signature[0];
            var compactSig = signature[1..]; // 64 bytes: r + s

            SecpRecoverableECDSASignature.TryCreateFromCompact(compactSig, recId, out var recoverableSig);
            if (recoverableSig is null)
                return Result.Failure<byte[]>("Failed to parse recoverable signature");

            ECPubKey.TryRecover(Context.Instance, recoverableSig, message, out var recoveredPub);
            if (recoveredPub is null)
                return Result.Failure<byte[]>("Failed to recover public key from signature");

            var pubBytes = new byte[33];
            recoveredPub.WriteToSpan(true, pubBytes, out _);
            return Result.Success(pubBytes);
        }
        catch (Exception ex)
        {
            return Result.Failure<byte[]>($"Public key recovery failed: {ex.Message}");
        }
    }

    public static byte[] ComputeHmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.CreateEncryptor().TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.CreateDecryptor().TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "PinCryptoServiceTests"
```

Expected: All PASS.

- [ ] **Step 5: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Services/PinCryptoService.cs
cd D:/docker/SimpleJadePinServer.Blazor && git add -A && git commit -m "Add PinCryptoService with ECDH, BIP341 tweaking, and blind oracle logic"
```

---

## Chunk 4b: Wallycore Compatibility Verification

### Task 9b: Generate and Verify Wallycore Test Vectors

This is the **highest-risk item** in the project. The C# ECDH key derivation and BIP341 tweaking must produce identical output to wallycore. We capture test vectors from the running Python server and verify them in C#.

**Files:**
- Create: `tests/SimpleJadePinServer.Blazor.Tests/Services/WallycoreCompatibilityTests.cs`

- [ ] **Step 1: Capture test vectors from Python server**

Create a small Python script that exercises the key crypto operations and prints test vectors. Run it against the existing Python server's wallycore installation:

```bash
cd D:/docker/SimpleJadePinServer/SimpleJadePinServer
python3 -c "
import wallycore as wally
from hashlib import sha256
import os
import json

# Fixed test inputs (deterministic for reproducibility)
server_priv = bytes.fromhex('a]a' * 32)  # placeholder — use a real 32-byte hex value
# Actually, let's generate valid keys for the test
server_priv = bytes(range(1, 33))  # 0x0102...20
try:
    wally.ec_private_key_verify(server_priv)
except:
    server_priv = bytes([0x01] * 32)
    wally.ec_private_key_verify(server_priv)

server_pub = wally.ec_public_key_from_private_key(server_priv)
print('server_priv:', server_priv.hex())
print('server_pub:', bytes(server_pub).hex())

# Test HMAC-SHA256
hmac_result = wally.hmac_sha256(server_priv, b'pin_data')
print('hmac_pin_data:', bytes(hmac_result).hex())

# Test BIP341 tweak
cke = server_pub  # use server pub as mock client key
replay_counter = bytes([0x01, 0x00, 0x00, 0x00])
tweak_input = wally.hmac_sha256(cke, replay_counter)
tweak = sha256(bytes(tweak_input)).digest()
tweaked_key = wally.ec_private_key_bip341_tweak(server_priv, tweak, 0)
print('tweak:', tweak.hex())
print('tweaked_key:', bytes(tweaked_key).hex())

# Test aes_cbc_with_ecdh_key (encrypt)
plaintext = bytes(range(32))  # 0x00..0x1F
iv = bytes([0xAA] * 16)
encrypted = wally.aes_cbc_with_ecdh_key(
    tweaked_key, iv, plaintext, cke,
    b'blind_oracle_response', wally.AES_FLAG_ENCRYPT
)
print('plaintext:', plaintext.hex())
print('iv:', iv.hex())
print('encrypted:', bytes(encrypted).hex())

# Test decrypt
decrypted = wally.aes_cbc_with_ecdh_key(
    tweaked_key, None, bytes(encrypted), cke,
    b'blind_oracle_response', wally.AES_FLAG_DECRYPT
)
print('decrypted:', bytes(decrypted).hex())
print('decrypt_matches:', plaintext.hex() == bytes(decrypted).hex())
"
```

Copy the output hex values into the test below.

- [ ] **Step 2: Write compatibility tests using captured vectors**

Create `tests/SimpleJadePinServer.Blazor.Tests/Services/WallycoreCompatibilityTests.cs`:

```csharp
using SimpleJadePinServer.Blazor.Services;
using System.Security.Cryptography;

namespace SimpleJadePinServer.Blazor.Tests.Services;

// These tests verify that our C# crypto implementation produces identical
// output to wallycore. Test vectors were captured from the Python server.
// If ANY of these tests fail, the server WILL NOT interoperate with Jade.
public class WallycoreCompatibilityTests
{
    // TODO: Replace these with actual hex values captured from Step 1
    const string ServerPrivHex = "TODO_FROM_PYTHON";
    const string ServerPubHex = "TODO_FROM_PYTHON";
    const string HmacPinDataHex = "TODO_FROM_PYTHON";
    const string TweakHex = "TODO_FROM_PYTHON";
    const string TweakedKeyHex = "TODO_FROM_PYTHON";
    const string PlaintextHex = "TODO_FROM_PYTHON";
    const string IvHex = "TODO_FROM_PYTHON";
    const string EncryptedHex = "TODO_FROM_PYTHON";

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
```

- [ ] **Step 3: Run the Python vector generation, fill in the TODO values, and run tests**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test --filter "WallycoreCompatibilityTests"
```

Expected: All 4 tests PASS. If any fail, the ECDH key derivation or BIP341 tweak logic needs adjustment before proceeding.

- [ ] **Step 4: If tests fail — investigate wallycore source**

If `AesCbcWithEcdh_Encrypt_MatchesWallycore` fails, the key derivation inside `aes_cbc_with_ecdh_key` differs from our assumption of `HMAC-SHA256(shared_x, label)`. Check the [wallycore source](https://github.com/AuGoldWallet/libwally-core) for the exact implementation and adjust `PinCryptoService.AesCbcWithEcdh` accordingly.

If `Bip341Tweak_MatchesWallycore` fails, NBitcoin's `TweakAdd` may handle the even-y-coordinate negation differently from wallycore's `ec_private_key_bip341_tweak` with flags=0. Adjust accordingly.

- [ ] **Step 5: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor && git add -A && git commit -m "Add wallycore compatibility test vectors — verify ECDH and BIP341 tweak"
```

---

## Chunk 5: Blazor UI & Integration

### Task 10: Configure DI and Program.cs

**Files:**
- Modify: `Program.cs`

- [ ] **Step 1: Wire up DI in Program.cs**

Update `Program.cs` to register services:

```csharp
using SimpleJadePinServer.Blazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register services
var keyDataPath = Path.Combine(builder.Environment.ContentRootPath, "key_data");
var keyStorageService = new KeyStorageService(keyDataPath);
keyStorageService.Initialize();

builder.Services.AddSingleton(keyStorageService);
builder.Services.AddSingleton(new PinStorageService(keyDataPath, keyStorageService.AesPinData.ToArray()));
builder.Services.AddSingleton<PinCryptoService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<SimpleJadePinServer.Blazor.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
```

- [ ] **Step 2: Verify build**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet build
```

- [ ] **Step 3: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Program.cs
git commit -m "Configure DI with KeyStorage, PinStorage, and PinCrypto services"
```

### Task 11: Create App Shell and Layout

**Files:**
- Create: `Components/App.razor`
- Create: `Components/Layout/MainLayout.razor`
- Create: `Components/_Imports.razor`

- [ ] **Step 1: Create _Imports.razor**

Create `Components/_Imports.razor`:

```razor
@using System.Net.Http
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.JSInterop
@using SimpleJadePinServer.Blazor.Components
```

- [ ] **Step 2: Create App.razor**

Create `Components/App.razor`:

```razor
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>SimpleJadePinServer</title>
    <base href="/" />
    <link rel="stylesheet" href="css/app.css" />
    <HeadOutlet @rendermode="InteractiveServer" />
</head>
<body>
    <Routes @rendermode="InteractiveServer" />
    <script src="https://cdnjs.cloudflare.com/ajax/libs/html5-qrcode/2.3.8/html5-qrcode.min.js" integrity="sha512-r6rDA7W6ZeQhvl8S7yRVQUKVHdexq+GAlNkNNqVC7YyIV+NwqCTJe2hDWCiffTyRNOeGEzRRJ9ifvRm/HCzGYg==" crossorigin="anonymous" referrerpolicy="no-referrer"></script>
    <script src="js/qr-interop.js"></script>
    <script src="_framework/blazor.web.js"></script>
</body>
</html>
```

- [ ] **Step 3: Create Routes.razor**

Create `Components/Routes.razor`:

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)" />
    </Found>
</Router>
```

- [ ] **Step 4: Create MainLayout.razor**

Create `Components/Layout/MainLayout.razor`:

```razor
@inherits LayoutComponentBase

<div class="header">
    <div class="container">
        <h1>SimpleJadePinServer</h1>
        <div class="subtitle">Self-hosted Blockstream Jade PIN Oracle with QR Pin Unlock Support</div>
    </div>
</div>

<div class="container">
    <div class="nav">
        <a href="/">QR Pin Unlock</a>
        <a href="/oracle">Generate Oracle QR Code</a>
    </div>

    @Body
</div>

<div class="footer">
    <div class="container">
        <small><em>*Collaboration by Claude*</em></small>
    </div>
</div>
```

- [ ] **Step 5: Verify build**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet build
```

- [ ] **Step 6: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Components/
git commit -m "Add Blazor app shell, routing, and main layout"
```

### Task 12: Create JS Interop for QR Scanning

**Files:**
- Create: `wwwroot/js/qr-interop.js`

- [ ] **Step 1: Create QR interop JavaScript**

Create `wwwroot/js/qr-interop.js`:

```javascript
// JS interop for html5-qrcode camera scanning.
// Only responsibility: capture raw QR strings and pass to C# via DotNetObjectReference.
// All decoding (BC-UR, CBOR) happens server-side in C#.

let scanner = null;

window.QrInterop = {
    // Start scanning with camera. Calls dotNetRef.invokeMethodAsync('OnQrScanned', result)
    // for each QR code detected.
    startScanning: function (elementId, dotNetRef) {
        scanner = new Html5QrcodeScanner(elementId, {
            fps: 10,
            formatsToSupport: [Html5QrcodeSupportedFormats.QR_CODE],
            verbose: false
        });

        scanner.render(
            function (result) {
                dotNetRef.invokeMethodAsync('OnQrScanned', result);
            },
            function (err) {
                // Scan errors are normal (no QR in frame) — ignore
            }
        );
    },

    // Stop scanning and release camera.
    stopScanning: function () {
        if (scanner) {
            scanner.clear();
            scanner = null;
        }
    }
};
```

- [ ] **Step 2: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add wwwroot/js/qr-interop.js
git commit -m "Add JS interop for QR camera scanning"
```

### Task 13: Create PinUnlock Page

**Files:**
- Create: `Components/Pages/PinUnlock.razor`

- [ ] **Step 1: Implement PinUnlock page**

Create `Components/Pages/PinUnlock.razor`:

```razor
@page "/"
@using SimpleJadePinServer.Blazor.Crypto
@using SimpleJadePinServer.Blazor.Services
@using QRCoder
@inject PinCryptoService CryptoService
@inject IJSRuntime JS
@implements IDisposable

<div class="main-layout">
    <div class="unlock-section">
        <div class="card">
            <h3>QR Pin Unlock</h3>

            <!-- Step 1: Scan PIN Request -->
            <div class="step @(_step >= 1 ? "active" : "inactive")" id="step1">
                <div class="step-number">1</div>
                <div class="step-title">Step 1: Scan PIN Request</div>
                <div class="step-description">Enter your PIN on Jade, then click below to scan the QR codes</div>

                @if (!_scanning && _step == 1)
                {
                    <button class="btn" @onclick="StartScanning">Start Step 1</button>
                }

                <div id="qrreader1" style="display: @(_scanning ? "block" : "none");"></div>

                <div class="status status-info">
                    <strong>Status:</strong> @_statusMessage
                </div>
            </div>

            <!-- Step 2: Display PIN Reply -->
            <div class="step @(_step >= 2 ? "active" : "inactive")" id="step2">
                <div class="step-number">2</div>
                <div class="step-title">Step 2: Scan PIN Reply</div>
                <div class="step-description">Scan these QR codes with your Jade device</div>

                @if (_step == 2 && !_processing)
                {
                    <button class="btn" @onclick="ProcessAndDisplay">Start Step 2</button>
                }

                <div class="status status-info">
                    <strong>Status:</strong> @_step2Status
                </div>

                @if (_responseQrFrames is not null)
                {
                    <div class="qr-container">
                        <img src="data:image/png;base64,@_currentQrBase64" alt="QR Response" style="max-width:280px;" />
                    </div>
                }
            </div>
        </div>
    </div>
</div>

@code {
    int _step = 1;
    bool _scanning;
    bool _processing;
    string _statusMessage = "Ready to begin...";
    string _step2Status = "Waiting for Step 1...";
    string? _currentQrBase64;

    // Collected BC-UR fragments from camera
    string?[]? _fragments;
    int _totalFragments;
    string[]? _completedFragments;

    // Response QR frames for cycling display
    string[]? _responseQrFrames;
    int _currentFrameIndex;
    System.Threading.Timer? _cycleTimer;

    // Reference for JS interop callback
    DotNetObjectReference<PinUnlock>? _dotNetRef;

    // Parsed request data
    string? _pinFunc;
    byte[]? _requestData;

    protected override void OnInitialized()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
    }

    async Task StartScanning()
    {
        _scanning = true;
        _statusMessage = "Camera starting...";
        _fragments = null;
        StateHasChanged();

        // html5-qrcode is loaded via script tag in App.razor — no eval needed
        await JS.InvokeVoidAsync("QrInterop.startScanning", "qrreader1", _dotNetRef);
        _statusMessage = "Ready to scan QR codes from Jade...";
        StateHasChanged();
    }

    // Called from JS when a QR code is scanned
    [JSInvokable]
    public async Task OnQrScanned(string result)
    {
        var parts = result.Split('/');
        if (!parts[0].Equals("ur:jade-pin", StringComparison.OrdinalIgnoreCase))
            return;

        if (parts.Length == 3)
        {
            // Multi-frame: ur:jade-pin/seq-total/data
            var seqParts = parts[1].Split('-');
            var seq = int.Parse(seqParts[0]);
            var total = int.Parse(seqParts[1]);

            _fragments ??= new string?[total];
            _totalFragments = total;
            _fragments[seq - 1] = result;

            var scanned = _fragments.Count(f => f is not null);
            _statusMessage = $"Scanned {scanned}/{total} codes...";

            if (_fragments.All(f => f is not null))
            {
                _completedFragments = _fragments!;
                await FinishScanning();
            }
        }
        else if (parts.Length == 2)
        {
            // Single frame
            _completedFragments = [result];
            await FinishScanning();
        }

        await InvokeAsync(StateHasChanged);
    }

    async Task FinishScanning()
    {
        await JS.InvokeVoidAsync("QrInterop.stopScanning");
        _scanning = false;

        // Decode BC-UR → CBOR → extract request
        var cborBytes = BcUr.Decode(_completedFragments!);
        var parseResult = CborProtocol.ParsePinRequest(cborBytes);

        if (parseResult.IsFailure)
        {
            _statusMessage = $"Error: {parseResult.Error}";
            return;
        }

        var request = parseResult.Value;
        _requestData = request.EncryptedData;
        _pinFunc = request.Urls.Any(u => u.EndsWith("set_pin")) ? "set_pin" : "get_pin";

        _statusMessage = "PIN request received!";
        _step2Status = "Ready to display QR codes";
        _step = 2;
    }

    async Task ProcessAndDisplay()
    {
        _processing = true;
        _step2Status = "Generating QR codes...";
        StateHasChanged();

        // Process through crypto service
        var result = _pinFunc == "set_pin"
            ? CryptoService.SetPin(_requestData!)
            : CryptoService.GetPin(_requestData!);

        if (result.IsFailure)
        {
            _step2Status = $"Error: {result.Error}";
            _processing = false;
            return;
        }

        // Build response CBOR and BC-UR encode
        var base64Response = Convert.ToBase64String(result.Value);
        var responseCbor = CborProtocol.BuildPinResponse(base64Response);
        var urFragments = BcUr.Encode(responseCbor, "jade-pin");

        // Generate QR images for each fragment
        _responseQrFrames = urFragments.Select(GenerateQrBase64).ToArray();
        _currentFrameIndex = 0;
        _currentQrBase64 = _responseQrFrames[0];

        _step2Status = "Displaying QR codes for Jade to scan...";

        // Start cycling if multi-frame
        if (_responseQrFrames.Length > 1)
        {
            _cycleTimer = new System.Threading.Timer(async _ =>
            {
                _currentFrameIndex = (_currentFrameIndex + 1) % _responseQrFrames.Length;
                _currentQrBase64 = _responseQrFrames[_currentFrameIndex];
                await InvokeAsync(StateHasChanged);
            }, null, 1500, 1500);
        }

        _processing = false;
        StateHasChanged();
    }

    // Generate a base64-encoded PNG QR code image from a string.
    static string GenerateQrBase64(string data)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(data.ToUpper(), QRCodeGenerator.ECCLevel.L);
        using var qrCode = new PngByteQRCode(qrData);
        var pngBytes = qrCode.GetGraphic(6);
        return Convert.ToBase64String(pngBytes);
    }

    public void Dispose()
    {
        _cycleTimer?.Dispose();
        _dotNetRef?.Dispose();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet build
```

- [ ] **Step 3: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Components/Pages/PinUnlock.razor
git commit -m "Add PinUnlock page with QR scanning and response display"
```

### Task 14: Create OracleSetup Page

**Files:**
- Create: `Components/Pages/OracleSetup.razor`

- [ ] **Step 1: Implement OracleSetup page**

Create `Components/Pages/OracleSetup.razor`:

```razor
@page "/oracle"
@using SimpleJadePinServer.Blazor.Crypto
@using SimpleJadePinServer.Blazor.Services
@using QRCoder
@inject KeyStorageService KeyStorage
@inject NavigationManager Nav

<div class="card">
    <h3>Oracle QR Code Setup</h3>
    <p>Generate a QR code to pair your Jade hardware wallet with this PIN server.</p>

    <div class="form-group">
        <label>Primary URL (urlA):</label>
        <input type="text" @bind="_urlA" placeholder="http://your-server:4443" class="form-control" />
    </div>

    <div class="form-group">
        <label>Backup URL (urlB, optional):</label>
        <input type="text" @bind="_urlB" placeholder="" class="form-control" />
    </div>

    <div class="form-group">
        <label>Server Public Key:</label>
        <input type="text" value="@KeyStorage.PublicKeyHex" readonly class="form-control" />
    </div>

    <button class="btn" @onclick="GenerateQr">Generate Oracle QR Code</button>

    @if (_qrBase64 is not null)
    {
        <div class="qr-container" style="margin-top: 1rem;">
            <img src="data:image/png;base64,@_qrBase64" alt="Oracle QR" style="max-width:280px;" />
        </div>
        <p class="step-description">Scan this QR code with your Jade to configure it to use this PIN server.</p>
    }
</div>

@code {
    string _urlA = "";
    string _urlB = "";
    string? _qrBase64;

    protected override void OnInitialized()
    {
        // Auto-populate primary URL from current server address
        _urlA = Nav.BaseUri.TrimEnd('/');
    }

    void GenerateQr()
    {
        // Build oracle config CBOR, then BC-UR encode with jade-updps type
        var cborBytes = CborProtocol.BuildOracleConfig(_urlA, _urlB, KeyStorage.PublicKeyHex);
        var urFragments = BcUr.Encode(cborBytes, "jade-updps");

        // Oracle config is typically small enough for a single QR
        using var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(urFragments[0].ToUpper(), QRCodeGenerator.ECCLevel.L);
        using var qrCode = new PngByteQRCode(qrData);
        var pngBytes = qrCode.GetGraphic(6);
        _qrBase64 = Convert.ToBase64String(pngBytes);
    }
}
```

- [ ] **Step 2: Verify build**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet build
```

- [ ] **Step 3: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add Components/Pages/OracleSetup.razor
git commit -m "Add OracleSetup page for Jade pairing"
```

### Task 15: Add CSS Styling

**Files:**
- Create: `wwwroot/css/app.css`

- [ ] **Step 1: Create app.css**

Port the styling from the original `style.css`, adapted for Blazor layout. Create `wwwroot/css/app.css` with the same CSS variables, dark/light mode support, card styles, step indicators, QR container styles, form controls, buttons, header/footer, and responsive layout from the original. Keep the same visual design.

- [ ] **Step 2: Verify build and run**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet build
```

- [ ] **Step 3: Commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add wwwroot/css/app.css
git commit -m "Add responsive CSS with dark/light mode"
```

### Task 16: Final Integration Test

- [ ] **Step 1: Run all tests**

```bash
cd D:/docker/SimpleJadePinServer.Blazor/tests/SimpleJadePinServer.Blazor.Tests
dotnet test
```

Expected: All tests PASS.

- [ ] **Step 2: Run the application**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
dotnet run &
```

Verify:
- App starts on configured port
- `key_data/server_keys/` contains generated keys
- Home page (`/`) loads with QR scanning UI
- Oracle page (`/oracle`) loads with form and auto-populated public key
- Oracle QR code generation works

- [ ] **Step 3: Final commit**

```bash
cd D:/docker/SimpleJadePinServer.Blazor
git add -A
git commit -m "Complete SimpleJadePinServer.Blazor implementation"
```
