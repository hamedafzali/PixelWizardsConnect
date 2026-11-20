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

## coturn

Peers behind strict NATs need a TURN relay. `coturn/turnserver.conf` is a production template for
[coturn](https://github.com/coturn/coturn), pinned to `coturn/coturn:4.6.2` (the version the
spike and the CI `coturn` job were tested with). It uses the REST-API secret scheme the router
issues credentials for (see [TURN credentials](#turn-credentials)), so the router and coturn share
one secret and nothing else.

### Start it

`docker-compose.https.yml` has a `coturn` service under the `turn` profile (off unless asked
for). Put the settings in a `.env` file next to the compose file:

```
TURN_SECRET=<output of: openssl rand -hex 32>
TURN_URLS=turn:turn.example.org:3478?transport=udp,turn:turn.example.org:3478?transport=tcp
TURN_REALM=turn.example.org
TURN_LISTEN_IP=<the server's own address, see below>
TURN_EXTERNAL_IP=<the address clients reach, see below>
```

```bash
docker compose -f docker-compose.https.yml --profile turn up -d
```

The router gets `TURN_URLS` and `TURN_SECRET` from the same variables. coturn refuses to start
if any of the four coturn variables is empty. The secret is written into a private copy of
the config (`/tmp/turnserver.conf` inside the container, created with `umask 077`) rather than
passed as a command-line argument, so it does not show up in `ps` on the host; the other three
values are passed as options. It runs with host networking, because the relay
needs a UDP port range and Docker's per-port proxying of a range is slow and hides client
addresses.

The compose file is for the VPS-with-Caddy shape. If the router is reached some other way (for
example an HTTPS tunnel), run coturn the same way and set `TURN_URLS`/`TURN_SECRET` on the
router however you start it; the two only share the secret.

### Addresses: VPS or behind NAT

coturn does not expand environment variables in its config file, so these go on the command line
(the compose file does it from `.env`). They are the part that goes wrong.

| Setup | `TURN_LISTEN_IP` (listening-ip and relay-ip) | `TURN_EXTERNAL_IP` (external-ip) |
|---|---|---|
| VPS, public IP on its NIC | the public IP, `203.0.113.10` | the same, no mapping |
| VPS, public IP NAT-ed to a private NIC address | the private address, `10.0.0.5` | `203.0.113.10/10.0.0.5` |
| Home server behind a NAT router | the server's LAN address, `192.168.1.20` | `203.0.113.10/192.168.1.20` (router's public IP / LAN address) |

`/` is coturn's `PUBLIC/PRIVATE` mapping form: coturn binds the private address and tells clients
the public one. Never use `127.0.0.1` or `0.0.0.0` for the relay in production: loopback is in
the denied peer ranges, so a relay bound there could reach nothing.

### DNS

`turn.example.org` must be a **DNS-only** record (not proxied) pointing at the public IP. HTTP
proxies and tunnels do not carry UDP, so a proxied record or a tunnel hostname cannot serve TURN.

### Ports: firewall or port forwards

| Port | Protocol | Purpose |
|---|---|---|
| 3478 | UDP and TCP | TURN/STUN |
| 49160-49200 | UDP | relay ports (`min-port`/`max-port` in the template) |
| 5349 (or 443 forwarded to it) | TCP | `turns:`, only if you enable TLS |

Behind NAT, forward exactly these from the router to `TURN_LISTEN_IP` (same port numbers).

### TLS (optional)

Off by default; the template works without certificates. For `turns:`, put a certificate and key
in `coturn/tls/` (`fullchain.pem`, `privkey.pem`, for example copied from your certificate
store), uncomment the volume line in the compose file, and in `turnserver.conf` delete `no-tls`
and `no-dtls` and uncomment the TLS block. Add a `turns:turn.example.org:5349?transport=tcp`
entry to `TURN_URLS`. Renewed certificates need a coturn restart.

### Secret rotation

Generate a new secret with `openssl rand -hex 32`, put it in `.env`, then restart both together:

```bash
docker compose -f docker-compose.https.yml --profile turn up -d router coturn
```

Every credential issued under the old secret stops working at once, and restarting coturn ends
live allocations, so relayed calls drop; the next pairing gets credentials under the new secret.
Restarting only one side makes every new allocation fail.

### Checking an allocation

The template logs one line per allocation (`docker compose ... logs coturn`):

```
session ...: realm <turn.example.org> user <1795123541:f7c7702ade>: incoming packet ALLOCATE processed, success
```

The user is the credential's expiry (Unix seconds) and a random id; the secret and credentials
are never logged. `error 401` right before it is the normal first, unauthenticated request.
A relay to a denied range shows `A peer IP ... denied in the range: ...` and the client gets
`403 (Forbidden IP)`. The template denies `0/8`, `10/8`, `100.64/10`, `127/8`, `169.254/16`,
`172.16/12`, `192.168/16`, `::1`, `fc00::/7` and `fe80::/10` as peers, so the relay cannot reach
the server's own network. That also means a peer on the server's LAN cannot be relayed to; such
peers connect directly.

### CI

The `coturn` job starts coturn from this template and runs
`go test -tags coturn` (`coturn_integration_test.go`), which takes credentials from
`issueTurnCredentials` and drives `turnutils_uclient`. It prints `PASS (a)` for an allocation
with valid router-issued credentials, `PASS (b)` for expired credentials being refused and
`PASS (c)` for a relay to a 10.0.0.0/8 peer being refused.

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
