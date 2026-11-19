# Router Server

A high-performance Go-based router server for the Remote Desktop Automation application.

## Features

- **Fast Connection Routing**: Written in Go for maximum performance
- **Unique Code Generation**: Generates 6-character connection codes from letters and digits, without the lookalikes 0/O and 1/I
- **HTTP API**: Simple REST API for host registration and client connections
- **Automatic Cleanup**: Removes expired host registrations after 30 minutes
- **Health Check**: Endpoint to monitor server status

## Installation

1. Ensure Go 1.21 or later is installed
2. Navigate to the router-server directory
3. Run: `go build -o router-server.exe`

## Docker Deployment

Build and run locally:

```bash
docker build -t pixelwizard-router:local .
docker run --rm -p 9000:9000 pixelwizard-router:local
```

Or use Docker Compose:

```bash
docker compose up -d --build
```

The container exposes port `9000`.

## Usage

### Start the Server

```bash
go run main.go
```

Or build and run:

```bash
go build -o router-server.exe
./router-server.exe
```

The server will start on port 9000 by default.

## API Endpoints

### POST /register

Register a host and receive a connection code.

**Request Body:**
```json
{
  "hostId": "unique-host-id",
  "hostName": "My Computer",
  "hostEndpoint": "192.168.1.100:8888"
}
```

**Response:**
```json
{
  "connectionCode": "ABC123",
  "hostId": "unique-host-id"
}
```

### POST /connect

Connect to a host using a connection code.

**Request Body:**
```json
{
  "connectionCode": "ABC123",
  "clientId": "unique-client-id"
}
```

**Response:**
```json
{
  "hostEndpoint": "192.168.1.100:8888",
  "success": true,
  "message": "Connected"
}
```

### WebSocket /signal

Two WebRTC peers meet on a connection code and swap SDP and ICE candidates here. The router
relays those messages unread and unaltered; media never touches it. The protocol (messages,
error codes, reconnecting) is in [docs/SIGNALING.md](../docs/SIGNALING.md), and example
messages are in `testdata/signaling/`.

Codes are shared with the HTTP routes (a code is unique across both) but a code from `/register`
cannot be joined here, nor a `/signal` code on `/connect`. `register`/`resume` count against the
`/register` rate limit and `join` against the `/connect` one. Without TURN configured (see
below), `joined` and `peer-joined` omit `turn`.

Limits, with defaults, set by environment variable: `SIGNAL_MAX_FRAME_BYTES` (65536),
`SIGNAL_MSG_RATE_MAX` (100) per `SIGNAL_MSG_RATE_WINDOW` (10s), `SIGNAL_MAX_SOCKETS_PER_IP` (20),
`SIGNAL_FIRST_MSG_TIMEOUT` (10s), `SIGNAL_PING_INTERVAL` (20s), `SIGNAL_PONG_TIMEOUT` (10s) and
`SIGNAL_RESUME_WINDOW` (60s).

#### TURN credentials

When `TURN_SECRET` and `TURN_URLS` are both set, `joined` and `peer-joined` carry a `turn` object
`{urls, username, credential}`, issued fresh for each socket at pairing time. If either is unset,
`turn` is omitted (peers fall back to STUN and host candidates); there is no static fallback.

- `TURN_URLS`: comma-separated, each starting with `turn:`, `turns:` or `stun:`, for example
  `turn:turn.example.org:3478?transport=udp,turns:turn.example.org:443?transport=tcp`. Anything
  else stops the router at startup.
- `TURN_SECRET`: the secret shared with coturn.
- `TURN_CREDENTIAL_TTL`: how long a credential stays valid (default `1h`).

The scheme is coturn's REST API: `username = <expiry-unix-seconds>:<random-hex-id>` and
`credential = base64(HMAC-SHA1(TURN_SECRET, username))`. coturn must run with `use-auth-secret`
and `static-auth-secret` set to the same value as `TURN_SECRET`; it then checks the expiry and the
HMAC itself, and the router keeps no per-credential state. The secret, usernames and credentials
are never logged or exposed on `/health`.

### GET /health

Check server health and status.

**Response:**
```json
{
  "status": "healthy",
  "hosts": 5,
  "uptime": "1h23m45s"
}
```

## Configuration

- **Port**: 9000 (hardcoded, can be changed in main.go)
- **Code Length**: 6 characters
- **Host Cleanup**: 30 minutes
- **Cleanup Interval**: 5 minutes

## Integration with Remote Desktop App

1. Start the router server
2. In the Remote Desktop app, select "Server Mode"
3. Choose "Router Server (Requires Router)"
4. Enter router server address (e.g., localhost:9000)
5. Click "Register & Start" to get a connection code
6. Share the connection code with the client
7. Client enters the code and router server provides the host endpoint

## Performance

Go was chosen for this router server because:
- High performance and low latency
- Excellent concurrency support (goroutines)
- Minimal memory footprint
- Fast compilation and deployment
- Cross-platform support

## Security Notes

- This is a basic implementation without authentication
- For production use, add:
  - TLS/HTTPS support
  - Authentication tokens
  - IP whitelisting
- **Rate limiting** is built in (see `RATE_LIMIT_WINDOW`/`RATE_LIMIT_MAX` in the top-level
  [README](../README.md#1-router-server)), keyed by client IP
- **`TRUSTED_PROXY_CIDRS`** (empty by default) controls whether `X-Forwarded-For` is
  trusted for that IP. Leave it empty for direct exposure (the default, and the secure
  choice with no reverse proxy). Only set it — to your reverse proxy's actual address
  range — when this server sits behind one; see the top-level README for details and the
  rightmost-value rule used when multiple hops are present.
- **`/register` validates its fields**: `hostId` and `hostName` are required (non-empty
  after trimming whitespace) and, like `hostEndpoint`, capped at 256 characters. Invalid
  requests get a `400` with a message naming the offending field.
