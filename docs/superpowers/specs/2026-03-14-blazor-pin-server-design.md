# SimpleJadePinServer.Blazor — Design Spec

## Overview

Reimplementation of the Python-based SimpleJadePinServer as a Blazor Server application in C#/.NET 9. This is a blind PIN oracle for the Blockstream Jade hardware wallet, enabling air-gapped wallet access via QR codes.

**Clean-slate server-side storage** — new PIN file format, new storage layout. Existing Jade devices will need re-pairing. However, the **wire protocol with Jade is preserved exactly** — the server must produce and consume the same BC-UR/CBOR messages and use the same crypto operations that Jade firmware expects, since Jade uses wallycore internally.

## Architecture

Pure Blazor Server — no REST endpoints, no controllers. Blazor components call injected services directly on the server side. The SignalR connection inherent to Blazor Server provides real-time QR frame cycling without polling.

### Pages

- **PinUnlock** (`/`) — Main workflow: scan Jade QR via camera, process PIN request through crypto service, display response QR for Jade to scan back.
- **OracleSetup** (`/oracle`) — Generate oracle configuration QR code for initial Jade pairing. Auto-populates server public key, user provides URLs.

### Services (DI-injected)

- **PinCryptoService** — Core crypto protocol: ECDH key derivation, AES-CBC encrypt/decrypt, HMAC-SHA256, BIP341 tweaking, EC signature recovery. Returns `Result<T>` for expected failures, throws only for exceptional situations.
- **KeyStorageService** — Manages server EC keypair. Auto-generates on first run, persists to `key_data/server_keys/`. Loads on startup, exposes public key.
- **PinStorageService** — Reads/writes/deletes PIN files in `key_data/pins/`. Enforces 3-attempt limit (deletes PIN on breach). Enforces monotonic replay counter.

### JS/C# Boundary

JavaScript is responsible **only** for browser camera access:
- `html5-qrcode` captures raw QR strings from camera
- JS passes raw string to C# via `DotNetObjectReference` callback

Everything else runs in C#:
- BC-UR decoding (bytewords → bytes → CBOR)
- CBOR parsing (extract encrypted payload, URLs, method)
- Crypto processing (ECDH, AES, HMAC, signature recovery)
- Response CBOR encoding
- BC-UR encoding (bytes → bytewords → multi-frame QR strings)
- QR image generation via QRCoder

## Data Flow

### PinUnlock Workflow

```
Jade displays QR code(s)
  → Browser camera captures via html5-qrcode (JS interop)
  → JS calls DotNetObjectReference callback with raw QR string
  → C#: BC-UR decode (bytewords → bytes, CRC32 verify, multi-frame reassembly)
  → C#: CBOR decode → extract encrypted payload + URLs + method
  → C#: PinCryptoService.SetPin() or .GetPin()
  → C#: CBOR encode response → BC-UR encode (fragment if needed)
  → C#: QRCoder generates QR frame images as PNG byte arrays
  → Component renders cycling QR frames at 1500ms intervals
       via Timer + InvokeAsync(StateHasChanged) over SignalR
  → User points Jade camera at screen to scan response
```

### OracleSetup Workflow

```
Page loads → KeyStorageService provides server public key (hex)
  → User enters primary URL (urlA) and optional backup URL (urlB)
  → C#: CBOR-encode config → BC-UR encode with ur:jade-updps type
  → C#: QRCoder renders QR image
  → User scans with Jade to pair
```

## Wire Protocol (Jade Communication)

### UR Types

| UR Type | Direction | Usage |
|---------|-----------|-------|
| `ur:jade-pin` | Jade → Server, Server → Jade | PIN set/get requests and responses |
| `ur:jade-updps` | Server → Jade | Oracle configuration (pairing) |

### Request CBOR Structure (from Jade, `ur:jade-pin`)

```json
{
  "result": {
    "http_request": {
      "params": {
        "urls": ["http://server:4443/set_pin"],
        "data": { "data": "<base64-encoded encrypted payload>" }
      }
    }
  }
}
```

The URL path (`/set_pin` or `/get_pin`) determines which operation to perform.

### Response CBOR Structure (to Jade, `ur:jade-pin`)

```json
{
  "id": "0",
  "method": "pin",
  "params": {
    "data": "<base64-encoded encrypted response>"
  }
}
```

### Oracle Config CBOR Structure (to Jade, `ur:jade-updps`)

```json
{
  "id": "001",
  "method": "update_pinserver",
  "params": {
    "urlA": "http://server:4443",
    "urlB": "",
    "pubkey": "<33-byte compressed EC public key as hex>"
  }
}
```

## BC-UR Protocol

BC-UR (Blockchain Commons Uniform Resources) is the QR transport layer. This must be implemented in C# (`Crypto/BcUr.cs`).

### Components

1. **Bytewords encoding** — maps each byte to a 4-letter word from a fixed 256-word alphabet. Minimal encoding uses only first and last letter of each word (2 chars per byte).
2. **CRC32 checksum** — appended to payload before bytewords encoding, verified on decode.
3. **Multi-frame fragmentation** — large payloads are split into numbered fragments, each individually CBOR-wrapped with `seqNum/totalFrags` metadata. Scanner must reassemble.
4. **UR type prefix** — each QR string starts with `ur:<type>/` (e.g., `ur:jade-pin/...`).

### Encode Flow
```
raw bytes → append CRC32 → bytewords minimal encode → split into fragments if needed
  → each fragment: "ur:<type>/" + seqNum + "-" + totalFrags + "/" + bytewords_payload
```

### Decode Flow
```
scan QR string → strip "ur:<type>/" prefix → extract seqNum/totalFrags
  → collect all fragments → concatenate → bytewords minimal decode
  → verify CRC32 → return raw bytes
```

## Crypto Protocol

Reimplements the same blind oracle protocol as the Python version. **Must match wallycore behavior exactly** for Jade interoperability.

### Key Derivation Chain

```
Server startup:
  server_private_key (32 bytes, auto-generated, persisted)
  server_public_key = EC_PUBLIC_KEY(server_private_key) (33 bytes compressed)
  server_aes_pin_data = HMAC-SHA256(server_private_key, "pin_data")

Per-PIN storage keys:
  storage_aes_key = HMAC-SHA256(server_aes_pin_data, pin_pubkey)
  pin_auth_key = HMAC-SHA256(server_aes_pin_data, sha256(pin_pubkey))
```

### Request/Response Encryption (ECDH + AES-CBC)

This replicates wallycore's `aes_cbc_with_ecdh_key` composite function:

1. **Input:** server session private key, client ephemeral public key (cke, 33 bytes), IV (16 bytes), data, context label
2. **ECDH:** compute shared point = `cke * session_private_key`, take x-coordinate (32 bytes)
3. **Key derivation:** `aes_key = HMAC-SHA256(shared_secret_x, context_label)` where context_label is `b"blind_oracle_request"` (decrypt) or `b"blind_oracle_response"` (encrypt)
4. **AES-CBC:** encrypt or decrypt data using derived `aes_key` and provided IV

**Note:** The exact key derivation step (how wallycore combines ECDH output with the label) must be verified against the [wallycore source](https://github.com/AuGoldWallet/libwally-core). If the derivation differs from HMAC-SHA256, this is the highest-risk item in the project.

### Session Key Derivation (per-request)

```
tweak = sha256(hmac_sha256(cke, replay_counter))
session_private_key = BIP341_TWEAK(server_private_key, tweak)
```

### EC Signature Recovery

Jade signs each request with a recoverable ECDSA signature (65 bytes: 1 byte recovery flag + 32 bytes r + 32 bytes s). The server recovers the signer's public key to identify which PIN file to load.

```
signed_msg = sha256(cke + replay_counter + pin_secret [+ entropy])
pin_pubkey = EC_RECOVER(signed_msg, signature)
pin_pubkey_hash = sha256(pin_pubkey)  → used as filename for PIN storage
```

NBitcoin provides `ECKey.RecoverCompact` for this operation.

### SetPin Operation

1. Decrypt request (context: `blind_oracle_request`)
2. Extract: `pin_secret` (32 bytes) + `entropy` (32 bytes) + `signature` (65 bytes) = 129 bytes total
3. Recover `pin_pubkey` from signature
4. If PIN file exists: verify `client_replay_counter > server_replay_counter`
5. If PIN file does not exist: skip replay check (first registration)
6. Generate 32 bytes of server randomness, derive storage key: `new_key = HMAC-SHA256(server_random, client_entropy)`
7. Save PIN file: store `sha256(pin_secret)` as PIN hash, `new_key` as storage key, attempt counter = 0, **replay counter reset to 0** (not client's counter)
8. Derive response: `aes_key = HMAC-SHA256(new_key, pin_secret)` — this is the blind oracle operation; the raw `new_key` is never revealed
9. Encrypt response `aes_key` with random 16-byte IV (context: `blind_oracle_response`); output includes IV prepended

### GetPin Operation

1. Decrypt request (context: `blind_oracle_request`)
2. Extract: `pin_secret` (32 bytes) + `signature` (65 bytes) = 97 bytes total
3. Recover `pin_pubkey` from signature
4. Load PIN file, verify `client_replay_counter > server_replay_counter`
5. Verify PIN: compare `sha256(pin_secret)` against stored hash
6. If wrong: increment attempt counter, **persist both attempt counter AND client's replay counter**. If attempts >= 3: **delete PIN file**
7. If wrong: return **random 32 bytes** as response (no oracle leakage)
8. If correct: reset attempt counter to 0, save client's replay counter. Derive response: `aes_key = HMAC-SHA256(stored_key, pin_secret)` — same blind oracle operation as SetPin
9. Encrypt response `aes_key` with random 16-byte IV (context: `blind_oracle_response`); output includes IV prepended

## PIN Storage Format (Clean Slate)

New format, version `0x02` to avoid confusion with Python version (`0x01`):

```
[1 byte: version 0x02]
[32 bytes: HMAC-SHA256 auth tag]
[16 bytes: random IV]
[N bytes: AES-CBC encrypted payload]
```

Encrypted payload contains:
```
[32 bytes: sha256(pin_secret)]
[32 bytes: AES key (the wallet decryption key)]
[1 byte: wrong attempt counter]
[4 bytes: replay counter, little-endian (matches Jade wire format)]
```

**Auth tag:** `HMAC-SHA256(pin_auth_key, version + IV + encrypted_payload)`
**Encryption key:** `storage_aes_key` (derived from server key + pin_pubkey)

Filename: `{hex(sha256(pin_pubkey))}.pin` in `key_data/pins/`

## Key Methods on PinCryptoService

- `SetPin(byte[] encryptedRequest) → Result<byte[]>` — new PIN registration
- `GetPin(byte[] encryptedRequest) → Result<byte[]>` — PIN unlock attempt
- `DeriveSessionKey(byte[] clientPublicKey, byte[] replayCounter) → ECKey` — BIP341 tweak
- `DecryptRequest(byte[] data, ECKey sessionKey, string context) → Result<byte[]>` — ECDH + AES-CBC unwrap
- `EncryptResponse(byte[] data, ECKey sessionKey, string context) → byte[]` — AES-CBC wrap
- `RecoverPublicKey(byte[] message, byte[] signature) → Result<ECPubKey>` — recoverable ECDSA

## NuGet Packages

| Package | Purpose |
|---------|---------|
| NBitcoin | EC/secp256k1, BIP341 taproot tweaking, ECDH, `ECKey.RecoverCompact` |
| PeterO.Cbor | CBOR encoding/decoding for Jade protocol |
| QRCoder | Server-side QR code image generation (supports alphanumeric mode) |
| CSharpFunctionalExtensions | `Result<T>` pattern for validation failures |

## Client-Side Dependencies

| Library | Purpose |
|---------|---------|
| html5-qrcode | Camera-based QR scanning (JS interop, raw string capture only) |

## Project Structure

```
SimpleJadePinServer.Blazor/
├── SimpleJadePinServer.Blazor.csproj
├── Program.cs
├── Components/
│   ├── App.razor
│   ├── Layout/
│   │   └── MainLayout.razor
│   └── Pages/
│       ├── PinUnlock.razor
│       └── OracleSetup.razor
├── Services/
│   ├── PinCryptoService.cs
│   ├── PinStorageService.cs
│   └── KeyStorageService.cs
├── Models/
│   ├── PinData.cs
│   └── OracleConfig.cs
├── Crypto/
│   ├── BcUr.cs          (BC-UR encode/decode, bytewords, CRC32, multi-frame)
│   └── CborProtocol.cs  (Jade-specific CBOR message structures)
├── wwwroot/
│   ├── css/
│   │   └── app.css
│   └── js/
│       └── qr-interop.js
├── Properties/
│   └── launchSettings.json
└── key_data/                  (runtime, gitignored)
    ├── server_keys/
    └── pins/
```

## Deployment

- **Target:** .NET 9 (`net9.0`)
- **Container support:** `<EnableSdkContainerSupport>true</EnableSdkContainerSupport>` in csproj (no standalone Dockerfile)
- **Direct run:** `dotnet run` for local development
- **Port:** Configurable, default 4443
- **TLS:** Optional, configurable via appsettings or command line

## Risk Register

| Risk | Severity | Mitigation |
|------|----------|------------|
| `aes_cbc_with_ecdh_key` key derivation mismatch with wallycore | **Critical** | Verify against wallycore C source before implementing; write integration test against known test vectors from Python server |
| NBitcoin `RecoverCompact` format mismatch with wallycore signatures | High | Test with captured Jade request data; wallycore uses standard 65-byte recoverable format |
| BC-UR implementation bugs causing Jade to reject QR codes | High | Port bytewords table exactly from original JS; test with known encoded/decoded pairs |
| QRCoder not supporting alphanumeric mode for oracle QR | Low | QRCoder supports all QR modes; verify during implementation |

## Decisions Log

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Framework | Blazor Server | Server-side crypto, real-time QR via SignalR |
| Crypto library | NBitcoin | Best BIP341/taproot support in .NET |
| QR scanning | JS interop (html5-qrcode) | Browser camera requires JavaScript |
| QR generation | QRCoder (C#) | No need for JS, server-side rendering |
| Storage compatibility | Clean slate (version 0x02) | Freedom to use idiomatic C# patterns |
| Wire compatibility | Must match Jade exactly | Jade firmware dictates the protocol |
| API endpoints | None | Blazor components call services directly |
| Container | SDK container support | No Dockerfile maintenance needed |
| Error handling | CSharpFunctionalExtensions Result<T> | Per coding guidelines |
| QR cycle interval | 1500ms | Match original implementation |
