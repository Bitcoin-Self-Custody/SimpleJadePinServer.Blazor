using CSharpFunctionalExtensions;
using PeterO.Cbor;
using SimpleJadePinServer.Blazor.Models;

namespace SimpleJadePinServer.Blazor.Crypto;

// Jade-specific CBOR message encoding/decoding.
// Jade communicates over BLE using CBOR-encoded RPC-style messages.
// The wire format wraps HTTP-like requests with URL lists, base64 data payloads,
// and method/id fields that mirror JSON-RPC conventions but encoded as CBOR.
public static class CborProtocol
{
    // Parses an incoming BC-UR CBOR PIN request from Jade.
    // Extracts URL list (set_pin vs get_pin is determined by the path) and the encrypted payload.
    // Returns Failure if the CBOR structure doesn't match expected Jade wire format.
    public static Result<PinRequest> ParsePinRequest(byte[] cborBytes)
    {
        try
        {
            var cbor = CBORObject.DecodeFromBytes(cborBytes);
            var httpParams = cbor["result"]["http_request"]["params"];
            var urlsArray = httpParams["urls"];

            // Extract all URLs from the CBOR array — Jade may send one or two (primary + fallback).
            var urls = new string[urlsArray.Count];
            for (var i = 0; i < urlsArray.Count; i++)
                urls[i] = urlsArray[i].AsString();

            // The encrypted data arrives base64-encoded inside a nested "data" map.
            var base64Data = httpParams["data"]["data"].AsString();
            var encryptedData = Convert.FromBase64String(base64Data);

            return Result.Success(new PinRequest(urls, encryptedData));
        }
        catch (Exception ex)
        {
            return Result.Failure<PinRequest>($"Failed to parse PIN request CBOR: {ex.Message}");
        }
    }

    // Builds a CBOR-encoded PIN response to send back to Jade via BC-UR.
    // Jade expects: { "id": "0", "method": "pin", "params": { "data": "<base64>" } }
    public static byte[] BuildPinResponse(string base64Data) =>
        CBORObject.NewMap()
            .Add("id", "0")
            .Add("method", "pin")
            .Add("params", CBORObject.NewMap().Add("data", base64Data))
            .EncodeToBytes();

    // Builds a CBOR-encoded oracle configuration update message for Jade.
    // Jade expects: { "id": "001", "method": "update_pinserver", "params": { urlA, urlB, pubkey } }
    // urlB may be empty string if only one pinserver URL is configured.
    // pubkey must be CBOR bytes (not text string) — Jade expects the raw 33-byte compressed key.
    // Uses CBORObject.NewOrderedMap to preserve insertion order (Jade may not handle canonical sorting).
    public static byte[] BuildOracleConfig(string urlA, string urlB, string pubkeyHex)
    {
        var pubkeyBytes = Convert.FromHexString(pubkeyHex);

        var paramsMap = CBORObject.NewOrderedMap()
            .Add("urlA", urlA)
            .Add("urlB", urlB)
            .Add("pubkey", pubkeyBytes);

        return CBORObject.NewOrderedMap()
            .Add("id", "001")
            .Add("method", "update_pinserver")
            .Add("params", paramsMap)
            .EncodeToBytes();
    }

    // Constructs a synthetic PIN request CBOR blob for testing purposes.
    // Mirrors the exact structure Jade sends over BLE so tests can exercise the real parse path.
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
