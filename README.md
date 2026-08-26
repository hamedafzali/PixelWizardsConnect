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

This section is written for someone deciding whether to deploy this on their network: what
can an attacker in each position actually do, and what stops them. No path here provides
authentication of the person at the other end of the connection — read on for what actually
gates access.

### Router path

- Connection codes are **one-time use** — deleted from the router the moment a viewer
  claims them (see T6 hardening below for the TTL-at-connect fix).
- Each code is paired with a per-session secret; the viewer must present the exact secret
  before the host shows the consent dialog.
- **What the session secret proves, precisely**: that the viewer obtained a connection code
  from your router within its TTL. **It is not user authentication.** Anyone who obtains or
  guesses a valid code within its TTL — by seeing it over someone's shoulder, intercepting
  an insecure channel it was shared over, or brute-forcing a short code against an
  internet-exposed router before it expires — passes this check identically to the intended
  recipient. The router cannot tell them apart.
- The router validates and rate-limits requests (T6: trusted-proxy-gated `X-Forwarded-For`,
  TTL enforced at connect time, `/register` field validation) but has no concept of user
  identity to check the code against.
- The router itself is plain HTTP — put it behind Caddy, Traefik, or nginx with TLS for
  internet-facing deployments; see `TRUSTED_PROXY_CIDRS` in `router-server/README.md`.

### Direct path (no router)

- **No secret exists on this path.** `MainViewModel`'s session-secret fields are both `""`
  for a direct connection — there is no out-of-band channel to share a secret over when a
  user just types an IP address, so the handshake check is a deliberate no-op here, not a
  bug (see the comment at the check site in `MainViewModel.HandleHandshake`).
- **Anyone who can reach the host's port and passes TLS pinning reaches the consent
  dialog.** On a direct connection, TLS pinning (below) and reachability are the entire
  technical gate. Past that gate, **consent is the only authorization.**

### What consent is and is not

The consent dialog authorizes a single session, at a moment in time, based on a human
looking at an endpoint string (an IP:port or a router-issued name) and clicking Allow. It
does **not** authenticate who is actually on the other end of that endpoint — the string
shown is an address, not an identity. Anyone who controls that address at the moment of
connection is who the host owner is implicitly trusting. On the router path, the session
secret narrows this to "someone with a valid code" before consent is even asked; on the
direct path, consent is reached with no prior narrowing at all.

### What the visible viewing badge provides

Independent of both paths above: the host cannot be viewed without an on-screen indicator
that a session is active. This is a genuine design strength worth stating plainly — even in
the worst case above (an attacker who obtained a valid code, or who reached a direct
connection and got consent from a confused or rushed user), the person at the host's screen
still has a visible, ongoing signal that someone is watching or controlling it. This is not
defeatable by anything described above; it fires regardless of how the session was
authorized.

### No unattended access

Every session — router or direct — requires a human at the host to click Allow. There is no
API, flag, or configuration path that starts a session without that click. This is a
deliberate choice, not a missing feature: it means the product does not, and currently
cannot, serve unattended-access use cases (e.g. remote administration of a machine with
nobody present to consent). If that use case matters to you, this tool does not cover it as
it stands.

### TLS certificate pinning (trust-on-first-use)

Each host generates a self-signed certificate on first run, stored at `PixelWizardConnect/transport.pfx` in the app data folder. The viewer does **not** trust it blindly:

- **First connection to a given `host:port`**: the presented certificate's SHA-256 fingerprint is recorded to a local pin store (`PixelWizardConnect/known_hosts.json`).
- **Every later connection to that same `host:port`**: the fingerprint must match exactly, or the connection is refused with a distinct `CertificatePinMismatchException` — never silently accepted.
- **What this defends against**: a network attacker who is *not* present at the very first connection cannot silently swap in their own certificate on a later session — the mismatch is detected and the connection is refused, rather than the app trusting a new certificate every time (which was the previous behavior).
- **What this does not defend against**: an attacker who *is* on-path during the very first connection to a host is trusted permanently from that point on. TOFU has no prior trust anchor to check the first certificate against — this is a fundamental limitation of the model, not a bug. Pin the host over a channel you trust (e.g. LAN you control) the first time.
- **If a host legitimately reinstalls or regenerates its certificate**, the viewer will refuse to connect until the old pin is forgotten. Use `TcpTransport.ForgetPin(host, port)` (or delete the corresponding entry from `known_hosts.json`) to re-trust it. There is no UI for this yet — see `docs/BACKLOG.md`.
- **If `known_hosts.json` is missing**, it is treated as an empty pin set (normal on first install). If it **exists but is empty, unparsable, or contains a malformed fingerprint**, the store is treated as corrupted and every connection is refused until it is fixed or removed — it never silently falls back to accepting all certificates.

### Which path for which deployment (today)

- **LAN, both ends trusted**: direct connection is fine. Pin the certificate over the LAN
  the first time (see TOFU above), then rely on network reachability plus consent.
- **Internet-facing**: use the router path behind TLS, and treat the session secret as
  what it is — a short-lived shared code, not identity. Anyone who can obtain a valid code
  in time reaches consent exactly like the intended viewer. Don't share codes over channels
  you don't trust, and keep `CODE_TTL` short.
- **Internet-facing, direct connection**: avoid it. There is no secret at all on this path;
  reachability plus TLS pinning plus one click is the entire gate.

**Phase 4 (WebRTC + DTLS-SRTP)** changes the transport, not this trust model by itself:
DTLS-SRTP gives you an authenticated, encrypted media channel per session, replacing the
current TCP transport and its TOFU pinning. It does not by itself add user authentication —
identity verification (if wanted) would still need to be layered on top, at signaling time.
Revisit this section when that lands; a passing session-secret or consent check today is
not evidence of what Phase 4 will guarantee.

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
