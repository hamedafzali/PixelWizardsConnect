# PixelWizard Connect

A self-hostable remote desktop tool with a Go relay server, Avalonia cross-platform client, and a clear consent-first design.

For product direction, phased plan, and competitor notes see [docs/ROADMAP.md](docs/ROADMAP.md).

## Platform support

| Component | Platforms |
|---|---|
| **Router server** (Go) | Windows, Linux, macOS, Docker |
| **Client app** (Avalonia, .NET 9) | Windows, macOS, Linux |
| **Host mode** (screen capture + input) | Windows (`net9.0-windows`), macOS (CoreGraphics), Linux (Xlib/XTest) |

## Architecture

```
src/
  PixelWizard.Core        — interfaces, protocol messages, session models
  PixelWizard.Transport   — TcpTransport (TLS), RouterHttpClient
  PixelWizard.WindowsHost — Windows screen capture (System.Drawing) + input (SendInput)
  PixelWizard.LinuxHost   — Linux screen capture (Xlib XGetImage) + input (XTest)
avalonia/
  PixelWizard.AvaloniaClient — single Avalonia app: host + viewer on all platforms
router-server/            — Go HTTP relay server
```

## Features

- **Host mode** — capture your screen and accept a viewer connection via direct IP or router code
- **Viewer mode** — connect to a remote host via direct IP or 6-character connection code
- **Consent dialog** — host must explicitly allow each incoming connection
- **Session token** — router-mode connections use a per-session secret; invalid tokens are rejected before consent is shown
- **Session watchdog** — host auto-disconnects viewers that go silent for 30 s
- **Auto-reconnect** — host re-listens after a viewer disconnects without needing to restart
- **Screen delta streaming** — only changed 32×32 pixel tiles are sent each frame
- **Mouse and keyboard input** — forwarded over the encrypted TCP channel
- **TLS** — self-signed certificate generated on first run; the viewer pins each host's certificate fingerprint on first connect and refuses any later connection presenting a different one (see [Security notes](#security-notes))
- **WebSocket viewer** — built-in local WebSocket server lets a browser watch the session on port 9001
- **LAN host discovery** — the viewer's **Scan network** button finds PixelWizard hosts on the local network without typing an IP

## Quick start

### 1. Router server

```bash
cd router-server
go run main.go
# or with Docker
docker compose up -d --build
```

The server starts on port 9000. Available env vars:

| Variable | Default | Description |
|---|---|---|
| `PORT` | `9000` | Listen port |
| `CODE_TTL` | `30m` | How long a code stays valid |
| `CLEANUP_INTERVAL` | `5m` | Expired-code cleanup frequency |
| `RATE_LIMIT_WINDOW` | `1m` | Rate-limit sliding window |
| `RATE_LIMIT_MAX` | `10` | Max requests per IP per window |
| `TRUSTED_PROXY_CIDRS` | *(empty)* | Comma-separated CIDR list. See below. |

**`TRUSTED_PROXY_CIDRS`** controls whether `X-Forwarded-For` is trusted for rate-limiting identity:

- **Left empty (default)**: `X-Forwarded-For` is always ignored, regardless of its value — the rate limiter keys off the raw socket peer. Correct and secure for direct exposure (no reverse proxy in front).
- **Set to your reverse proxy's address range** (e.g. `172.16.0.0/12` for a typical Docker bridge network, or your load balancer's subnet): `X-Forwarded-For` is honoured **only** when the direct TCP peer is inside one of the listed CIDRs. If multiple values are present in the header, the **rightmost** one is used — that is the value the trusted proxy itself observed, since each hop appends to the right as it forwards the request.
- A malformed CIDR in this list fails the server at startup with a clear error — it will not silently start with proxy trust disabled.
- Never set this to `0.0.0.0/0` (or any range broader than your actual proxy) on a directly-exposed deployment — that re-opens the exact spoofing gap this setting exists to close.

### 2. Client application

From the repository root, run the launch script for your platform:

```bash
./run.sh    # macOS / Linux
```

```bat
run.cmd     :: Windows
```

These restore dependencies and start the app. The first launch shows a short onboarding overlay.

Prefer running it directly? You can still use the .NET CLI:

```bash
cd avalonia/PixelWizard.AvaloniaClient
dotnet run    # picks the correct target automatically on every platform
```

**Host mode:** choose Host → enter the router address → click Register. Share the 6-character code with the viewer.

**Viewer mode:** choose Connect → enter the code and router address → click Connect. Or enter an IP address for a direct LAN connection. You can also click **Scan network** to discover PixelWizard hosts on your local network automatically.

### Troubleshooting

After pulling new changes, a stale build can cause unexpected errors. If the app fails to start or behaves oddly, delete the `bin` and `obj` folders under `avalonia/PixelWizard.AvaloniaClient` and run the launch script again to rebuild from scratch.

### macOS permissions

- Screen capture: System Settings → Privacy → Screen Recording → grant to the app
- Input injection: System Settings → Privacy → Accessibility → grant to the app

### Linux requirements

- `libX11` and `libXtst` — usually pre-installed (`sudo apt install libx11-6 libxtst6`)
- `DISPLAY` environment variable must be set (e.g. `DISPLAY=:0`)

## Security notes

- Connection codes are **one-time use** — they are deleted from the router the moment a viewer claims them
- Each code is paired with a per-session secret; the viewer must present the exact secret before the host shows the consent dialog
- Direct-IP connections (no router) skip the secret check and rely on network-level access control
- The router is plain HTTP — put it behind Caddy, Traefik, or nginx with TLS for internet-facing deployments

### TLS certificate pinning (trust-on-first-use)

Each host generates a self-signed certificate on first run, stored at `PixelWizardConnect/transport.pfx` in the app data folder. The viewer does **not** trust it blindly:

- **First connection to a given `host:port`**: the presented certificate's SHA-256 fingerprint is recorded to a local pin store (`PixelWizardConnect/known_hosts.json`).
- **Every later connection to that same `host:port`**: the fingerprint must match exactly, or the connection is refused with a distinct `CertificatePinMismatchException` — never silently accepted.
- **What this defends against**: a network attacker who is *not* present at the very first connection cannot silently swap in their own certificate on a later session — the mismatch is detected and the connection is refused, rather than the app trusting a new certificate every time (which was the previous behavior).
- **What this does not defend against**: an attacker who *is* on-path during the very first connection to a host is trusted permanently from that point on. TOFU has no prior trust anchor to check the first certificate against — this is a fundamental limitation of the model, not a bug. Pin the host over a channel you trust (e.g. LAN you control) the first time.
- **If a host legitimately reinstalls or regenerates its certificate**, the viewer will refuse to connect until the old pin is forgotten. Use `TcpTransport.ForgetPin(host, port)` (or delete the corresponding entry from `known_hosts.json`) to re-trust it. There is no UI for this yet — see `docs/BACKLOG.md`.
- **If `known_hosts.json` is missing**, it is treated as an empty pin set (normal on first install). If it **exists but is empty, unparsable, or contains a malformed fingerprint**, the store is treated as corrupted and every connection is refused until it is fixed or removed — it never silently falls back to accepting all certificates.

## Building for release

```bash
# Windows self-contained
dotnet publish avalonia/PixelWizard.AvaloniaClient -f net9.0-windows -r win-x64 --self-contained

# macOS
dotnet publish avalonia/PixelWizard.AvaloniaClient -f net9.0 -r osx-x64 --self-contained

# Linux
dotnet publish avalonia/PixelWizard.AvaloniaClient -f net9.0 -r linux-x64 --self-contained

# Router (Docker)
cd router-server && docker compose up -d --build
```
