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
- **TLS** — self-signed certificate generated on first run; trust-on-first-use for LAN deployments
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
- TLS uses a self-signed certificate stored in the app data folder (`PixelWizardConnect/transport.pfx`); the client accepts any server certificate on first connect
- The router is plain HTTP — put it behind Caddy, Traefik, or nginx with TLS for internet-facing deployments

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
