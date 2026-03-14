# SimpleJadePinServer.Blazor — Design Spec

## Overview

Reimplementation of the Python-based SimpleJadePinServer as a Blazor Server application in C#/.NET 9. This is a blind PIN oracle for the Blockstream Jade hardware wallet, enabling air-gapped wallet access via QR codes.

**This is a clean-slate reimplementation** — not wire-compatible with the Python version. New file formats, new storage layout. Existing Jade devices will need re-pairing.

## Architecture

Pure Blazor Server — no REST endpoints, no controllers. Blazor components call injected services directly on the server side. The SignalR connection inherent to Blazor Server provides real-time QR frame cycling without polling.

### Pages

- **PinUnlock** (`/`) — Main workflow: scan Jade QR via camera, process PIN request through crypto service, display response QR for Jade to scan back.
- **OracleSetup** (`/oracle`) — Generate oracle configuration QR code for initial Jade pairing. Auto-populates server public key, user provides URLs.

### Services (DI-injected)

- **PinCryptoService** — Core crypto protocol: ECDH key derivation, AES-CBC encrypt/decrypt, HMAC-SHA256, BIP341 tweaking, EC signature recovery. Returns `Result<T>` for expected failures, throws only for exceptional situations.
- **KeyStorageService** — Manages server EC keypair. Auto-generates on first run, persists to `key_data/server_keys/`. Loads on startup, exposes public key.
- **PinStorageService** — Reads/writes/deletes PIN files in `key_data/pins/`. Enforces 3-attempt limit (deletes PIN on breach). Enforces monotonic replay counter.

## Data Flow

### PinUnlock Workflow

```
Jade displays QR code(s)
  → Browser camera captures via html5-qrcode (JS interop)
  → JS calls DotNetObjectReference callback
  → Blazor component receives raw QR data
  → BC-UR decode → CBOR decode → extract encrypted payload
  → PinCryptoService.SetPin() or .GetPin()
  → QRCoder generates response QR as PNG byte array
  → Component renders cycling QR frames via Timer + InvokeAsync(StateHasChanged)
  → User points Jade camera at screen to scan response
```

### OracleSetup Workflow

```
Page loads → KeyStorageService provides server public key
  → User enters primary/backup URLs
  → CBOR-encode config → BC-UR encode
  → QRCoder renders QR image
  → User scans with Jade to pair
```

## Crypto Protocol

Reimplements the same blind oracle protocol as the Python version:

### Request/Response Encryption (ECDH + AES-CBC)
- Client (Jade) generates ephemeral public key (cke)
- Server derives session private key: `BIP341_TWEAK(server_key, sha256(hmac_sha256(cke, replay_counter)))`
- ECDH shared secret → AES-CBC key
- Context strings (`blind_oracle_request` / `blind_oracle_response`) prevent cross-endpoint replay

### PIN Storage
- New format (not compatible with Python version)
- Encrypted with key derived from server private key + PIN public key
- HMAC authentication tag to prevent tampering
- Contains: PIN secret hash, AES key, attempt counter, replay counter

### Security Properties (preserved from Python version)
- Wrong PIN returns random key (no oracle leakage)
- 3 wrong attempts → PIN file deleted (irreversible)
- Monotonic replay counter prevents replay attacks
- Per-request session key derivation via BIP341 tweaking

### Key Methods on PinCryptoService
- `SetPin(byte[] encryptedRequest) → Result<byte[]>` — new PIN registration
- `GetPin(byte[] encryptedRequest) → Result<byte[]>` — PIN unlock attempt
- `DeriveSessionKey(byte[] clientPublicKey, byte[] replayCounter) → ECKey`
- `DecryptRequest(byte[] data, ECKey sessionKey, string context) → Result<byte[]>`
- `EncryptResponse(byte[] data, ECKey sessionKey, string context) → byte[]`

## NuGet Packages

| Package | Purpose |
|---------|---------|
| NBitcoin | EC/secp256k1, BIP341 taproot tweaking, ECDH, key recovery |
| PeterO.Cbor | CBOR encoding/decoding for Jade protocol |
| QRCoder | Server-side QR code image generation |
| CSharpFunctionalExtensions | `Result<T>` pattern for validation failures |

## Client-Side Dependencies

| Library | Purpose |
|---------|---------|
| html5-qrcode | Camera-based QR scanning (JS interop) |

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
│   └── BcUr.cs
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

## Decisions Log

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Framework | Blazor Server | Server-side crypto, real-time QR via SignalR |
| Crypto library | NBitcoin | Best BIP341/taproot support in .NET |
| QR scanning | JS interop (html5-qrcode) | Browser camera requires JavaScript |
| QR generation | QRCoder (C#) | No need for JS, server-side rendering |
| Wire compatibility | Clean slate | Freedom to use idiomatic C# patterns |
| API endpoints | None | Blazor components call services directly |
| Container | SDK container support | No Dockerfile maintenance needed |
| Error handling | CSharpFunctionalExtensions Result<T> | Per CLAUDE.md coding guidelines |
