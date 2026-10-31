# PixelWizard Connect — Signaling Protocol (v1)

**Status: draft for review (P3.2).** No code implements this yet.

This is the reference for the router's WebSocket endpoint, `/signal`. Two WebRTC peers use it to
find each other and exchange SDP and ICE candidates. It is meant to be complete enough to
implement a client from without reading the Go.

The endpoint does not carry a session. Media and input travel over WebRTC once ICE connects; the
router never sees them. The HTTP `/register` and `/connect` routes are unchanged and are not
described here (`router-server/README.md`).

## Transport

- A WebSocket at `/signal`. Deployed behind Caddy as `wss://`; the router itself listens in plain
  text, and the IP it uses for limits comes from `TRUSTED_PROXY_CIDRS`, as it does for the HTTP
  routes.
- Every message is one **text** frame holding one JSON object. Binary frames are an error
  (`bad-message`).
- Keepalive uses WebSocket ping control frames, not JSON. The router pings every 20 s and closes
  the socket if no pong arrives within 10 s. Clients do not need to ping.

### Envelope

```json
{ "v": 1, "type": "join", "code": "K7Q2MX" }
```

| Field | Rule |
|---|---|
| `v` | Required integer. The router supports exactly `1`. A missing or non-integer `v` is `bad-message`; any other integer is `unsupported-version`. |
| `type` | Required string. |
| others | Per type, below. Unknown fields are ignored. |

### Unknown message types

A well-formed envelope with a `type` this build does not recognise is **ignored**: not relayed,
not answered, and the socket stays open. This is the same rule as the Phase 1 binary envelope,
for the same reason: a newer peer can add a type without every peer updating at once.

| Situation | Outcome |
|---|---|
| Not JSON, not an object, binary frame, missing or bad `v` or `type` | `error bad-message`, close |
| `v` is an integer other than 1 | `error unsupported-version`, close |
| Frame larger than 64 KiB | `error too-large`, close |
| Known `type`, wrong fields, or sent at the wrong point in the sequence | `error bad-message`, close |
| Unknown `type`, otherwise well-formed | Ignored, socket stays open |

## Sequence

```
host                         router                        viewer
  |--- register ------------->|                               |
  |<-- registered {code} -----|                               |
  |                           |<------------- join {code} ----|
  |<-- peer-joined {turn} ----|---- joined {turn} ----------->|
  |--- offer ---------------->|---- offer ------------------->|
  |<-- answer ----------------|<--- answer -------------------|
  |--- candidate ... -------->|---- candidate ... ----------->|   (both directions,
  |<-- candidate ... ---------|<--- candidate ... ------------|    as gathered)
  |--- end-of-candidates ---->|---- end-of-candidates ------->|
  |<-- end-of-candidates -----|<--- end-of-candidates --------|
  |--- bye ------------------>|---- bye --------------------->|   (either side)
```

- **The host offers.** It is the side that will add media tracks in Phase 4.
- **Candidates are trickled.** A peer sends each candidate the moment `onicecandidate` fires
  and sends `end-of-candidates` only when gathering completes. This is required: SIPSorcery
  leaves relay candidates out of `localDescription.sdp` (ADR-001, gate findings). Peers must not
  wait a fixed delay and must not splice candidates into the SDP.
- **Pairing is not consent.** `peer-joined` means a viewer has the code. The host app still
  shows its approval modal before any session starts, exactly as on the TCP path; nothing on this
  channel can approve a session.

## Messages

### Pairing (to and from the router)

| `type` | Direction | Fields | Notes |
|---|---|---|---|
| `register` | host → router | none | First message on a host socket. |
| `registered` | router → host | `code`, `resumeToken` | `code` is 6 characters from the same alphabet as `/register`, unique across both routes, one-time use, valid for the code TTL (30 min). |
| `resume` | host → router | `resumeToken` | First message on a new host socket, instead of `register`. See *Reconnecting*. |
| `resumed` | router → host | `code` | Same code as before. |
| `join` | viewer → router | `code` | First message on a viewer socket. |
| `joined` | router → viewer | `turn` (optional) | The room is now paired. |
| `peer-joined` | router → host | `turn` (optional) | Sent at the same moment as `joined`. |
| `peer-left` | router → peer | none | The other socket closed or sent `bye`. The router then closes this socket. |
| `error` | router → peer | `code`, `message` | Always followed by close. `message` is for logs, not for users. |

TURN credentials are issued at **pairing** time, not at registration, so a host that waited
29 minutes for a viewer still gets credentials with a full lifetime.

```json
"turn": {
  "urls": ["turn:turn.example.org:3478?transport=udp",
           "turns:turn.example.org:443?transport=tcp"],
  "username": "1767225600:9f3c1a7e2b",
  "credential": "q8m4Zx1...="
}
```

`username` is `<expiry, unix seconds>:<random id>` and `credential` is
base64(HMAC-SHA1(secret, username)), coturn's `use-auth-secret` scheme. Each side gets its own
pair. `turn` is omitted when the router has no TURN secret configured; peers then have STUN and
host candidates only. There is no static-credential fallback.

### Relayed between the paired peers

The router checks that the room is paired, the fields are present and are strings or numbers as
listed, and the frame is within limits. It never reads or changes the content, and it forwards
the frame unaltered.

| `type` | Fields |
|---|---|
| `offer` | `sdp` |
| `answer` | `sdp` |
| `candidate` | `candidate`, `sdpMid` (string or null), `sdpMLineIndex` (integer or null) |
| `end-of-candidates` | none |
| `bye` | `reason` (optional string, for logs) |

Messages from one peer are delivered to the other in the order they were sent. Any relayed type
sent before pairing is `bad-message`.

### Error codes

| `code` | When |
|---|---|
| `bad-message` | See the table under *Unknown message types* |
| `unsupported-version` | `v` is not 1 |
| `too-large` | Frame over 64 KiB |
| `code-unknown` | `join` with a code that does not exist or has expired |
| `code-used` | `join` with a code that has already been joined |
| `resume-rejected` | `resume` with an unknown or expired token |
| `rate-limited` | Any limit below |
| `timeout` | No first message within 10 s of connecting |

## Reconnecting

v1 covers one case only: **a host waiting for a viewer** loses its socket (laptop sleeps, Wi-Fi
changes).

- When a registered, unpaired host socket closes, the router keeps the registration for 60 s.
- A new socket sending `resume {resumeToken}` within that time gets `resumed {code}`, with the
  same code, and the host keeps waiting.
- After 60 s the registration is deleted and its code becomes `code-unknown`.

Once paired, either socket closing ends the room. Reconnecting mid-session (ICE restart over a
new socket) is Phase 7, and adds message types rather than changing these.

`resumeToken` is 32 random bytes, hex-encoded, compared in constant time, and valid only for the
registration it was issued with.

## Limits

| Limit | Value |
|---|---|
| Frame size | 64 KiB (an SDP offer with one data channel is about 1–3 KiB) |
| Messages per socket | 100 per 10 s, then `rate-limited` |
| Open sockets per IP | 20 (offices share one public IP) |
| `register` and `resume` | The same per-IP limiter as `/register` (10 per minute) |
| `join` | The same per-IP limiter as `/connect` (10 per minute) |
| First message | Within 10 s of the upgrade |
| Unpaired registration | Code TTL (30 min), plus the 60 s resume window after a socket drop |

All of these are environment-configurable like the existing ones, and the defaults above are
what tests assert.

## Security posture

- **The code is the capability**, as it is today. Six characters from a 36-character alphabet
  (A–Z, 0–9) is about 2 × 10⁹ codes; the per-IP join limit and one-time use are what make guessing impractical.
- **The router is trusted with the SDP.** It could replace the DTLS fingerprint and sit in the
  middle. That is the same trust it already has over `sessionSecret` on the HTTP route, and it is
  why the router is self-hosted. End-to-end fingerprint verification (for example, a short code
  both users compare) is not in v1.
- **The router never logs** SDP, candidates, TURN credentials or resume tokens. It logs codes
  only when they are created and expired, as it does now.
- **`Origin` is not checked in v1.** The Phase 5 browser viewer adds an allow-list.
- **Every field is attacker-controlled.** The router validates types and sizes before acting
  and closes on anything it does not expect, apart from unknown `type`.

## Test vectors

Example messages, one file per type, live in `router-server/testdata/signaling/`. The Go tests
and the .NET `SignalingClient` tests (P3.6) both read them. Each side must parse every file, and
re-serializing it must produce the same JSON value. That shared test is the contract between the
two languages.
