namespace SimpleJadePinServer.Blazor.Models;

// Parsed result of a BC-UR/CBOR PIN request from Jade.
// The URL path determines whether this is a SetPin or GetPin operation.
public sealed record PinRequest(
    string[] Urls,           // URL(s) from CBOR — path ending determines set_pin vs get_pin
    byte[] EncryptedData     // base64-decoded encrypted payload (cke + replay_counter + encrypted_data)
);
