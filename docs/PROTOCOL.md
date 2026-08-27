# PixelWizard Connect — Wire Protocol (v2)

This is the reference for the `NetworkMessage`/`TcpTransport` binary protocol used between
desktop peers (and, from Phase 6, the Flutter mobile client). It is meant to be complete
enough to implement a client from without reading the C#.

It does **not** cover the browser viewer's WebSocket protocol (`WebSocketHostServer.cs`) — that
is a separate, simpler, one-way tag-byte format with no version negotiation, untouched by any
of the v2 work described here. See the "Browser viewer" section at the end for its shape.

## Transport framing

Every message is sent as:

```
[4-byte outer length, little-endian, signed int32][frame bytes]
```

`outer length` is the byte length of everything that follows it (the frame). It is not
optional and not zero for any valid message — a `NetworkMessage` frame is always at least
13 bytes (type + timestamp + inner length). An outer length that is zero or negative means
stream sync is lost; the connection is unrecoverable and closes immediately
(`PixelWizard.Transport.FramingException`).

The frame itself is:

```
[1-byte MessageType][8-byte timestamp, little-endian long, DateTime.Ticks][4-byte inner data length, little-endian int32][data]
```

`data` is `MessageType`-specific; see the table below.

### Unknown message types

A frame with **valid framing and length** but a `MessageType` byte this build doesn't
recognize is **not** an error. The receiver reads and discards the payload and continues
processing the connection. This is what lets a v2 peer add a new message type without every
other peer needing to update in lockstep. It fires `TcpTransport.UnknownMessageTypeSkipped`,
distinct from `Error`/disconnect — the two failure modes must never be collapsed:

| Situation | Outcome |
|---|---|
| Bad/negative outer length | Unrecoverable — `FramingException`, disconnect |
| Malformed inner length (declares more than remains) | Unrecoverable — `InvalidDataException`, disconnect |
| Unrecognized `MessageType`, otherwise well-framed | Recoverable — payload discarded, session continues |

This tolerance applies **after** the Hello/Handshake negotiation below completes. Before
negotiation completes, both peers are strict: anything other than the exact expected next
message (`Hello`, then `Handshake`) disconnects immediately. Negotiation is the one place
where "unknown" isn't tolerated, because there is nothing yet to negotiate leniency about.

## Negotiation sequence

On connect, the viewer is the first speaker. The full sequence for a healthy connection:

```
viewer                          host
  |--------- Hello ------------->|   (protocol version, codecs, max streams, role)
  |<-------- HelloAck -----------|   (host's own Hello, symmetric shape)
  |--------- Handshake --------->|   (session secret)
  |<-------- HandshakeOk --------|
  |                               |
  |<===== ordinary traffic =====>|
```

If Hello negotiation fails, the host replies `HelloRejected` (reason + human-readable message)
and disconnects instead of `HelloAck`. If the session secret is wrong, the host replies
`HandshakeFailed` and disconnects instead of `HandshakeOk`. Anything other than the expected
next message at either step is treated as a protocol violation and disconnects immediately
with no reply.

### Hello / HelloAck payload (4 bytes, fixed-width)

```
[1 byte ProtocolVersion][1 byte Role][1 byte Codecs][1 byte MaxConcurrentStreams]
```

- **ProtocolVersion**: `2` for this revision (`ProtocolVersions.Current`). Compared for exact
  equality — see version-compatibility rules below.
- **Role**: `1` = `Full` (can receive and act on input messages), `2` = `ShareOnly` (has no
  input surface — e.g. a phone with no mouse/keyboard). A viewer that receives `ShareOnly` in
  `HelloAck` must not send input messages to that host; it is not an error to do so (they are
  simply meaningless and ignored), but a well-behaved peer suppresses them so as not to send
  into the void. Nothing in the current desktop codebase advertises `ShareOnly` — it always has
  `IInputInjector` available — but a viewer must handle receiving it, since a future mobile
  host will.
- **Codecs**: bit flags, `1` = `Jpeg`. `0` (`None`) is valid and means "no codec support to
  advertise" — it is not malformed, just guaranteed to fail negotiation (see below).
- **MaxConcurrentStreams**: a byte is enough headroom (256) for any realistic combination of
  screen/camera/overlay streams; this is not a hard architectural ceiling, just the field
  width chosen for this version.

There is no length-prefixed field in `Hello`, so there is no truncatable-length attack
surface here the way there is elsewhere — a short buffer throws `EndOfStreamException` from
`BinaryReader` with no extra bounds-checking code needed.

### HelloRejected payload

```
[1 byte HelloRejectReason][4-byte message length, little-endian int32][UTF-8 message bytes]
```

`HelloRejectReason`: `1` = `VersionMismatch`, `2` = `IncompatibleCapabilities` (no codec in
common). The message length is bounds-checked exactly like every other length-prefixed field
in this protocol (declared length must not exceed remaining buffer, and must not be negative).

### Version-compatibility rules

- Negotiation requires **exact** `ProtocolVersion` equality. There is no partial-compatibility
  table in Phase 1 — a v2 peer meeting a v3 peer (or vice versa) gets `HelloRejected` with
  `VersionMismatch` and a message naming both versions, then disconnects. What the user sees:
  a specific rejection reason surfaced through `Status`/`HostStatus`, not a bare dropped
  connection.
- **v1 peers (pre-Hello builds) cannot interoperate with v2 at all**, and this is by design,
  not an oversight. A v1 viewer's first message is `Handshake`, sent immediately on connect.
  A v2 host now requires `Hello` first and disconnects on anything else arriving before
  negotiation completes — so a v1 viewer meeting a v2 host gets "Bad hello" and a disconnect,
  never reaching the old `HandshakeFailed` path. No real v1 peers are deployed (this is a
  pre-1.0 solo prototype), so this was judged acceptable rather than something to preserve
  compatibility for. If that ever changes, bridging it would need a v1-shaped fallback path
  that isn't built here.

## Message types

| Type | Value | Plane | Payload |
|---|---|---|---|
| `RegisterHost` | 1 | Control | router-specific |
| `HostRegistered` | 2 | Control | router-specific |
| `ConnectToHost` | 3 | Control | router-specific |
| `HostConnected` | 4 | Control | router-specific |
| `ConnectionFailed` | 5 | Control | router-specific |
| `Disconnect` | 6 | Control | (none) |
| `ScreenDelta` | 10 | **Media** | `ScreenDelta` (region + length-prefixed image) |
| `FullScreen` | 11 | **Media** | raw JPEG bytes directly as `Data` |
| `ScreenSize` | 12 | Control | (dimensions) |
| `MouseMove` | 20 | Control | `MouseMoveMessage` |
| `MouseClick` | 21 | Control | `MouseClickMessage` |
| `KeyPress` | 22 | Control | `KeyMessage` |
| `KeyRelease` | 23 | Control | `KeyMessage` |
| `MouseButtonDown` | 24 | Control | `MouseClickMessage` |
| `MouseButtonUp` | 25 | Control | `MouseClickMessage` |
| `Ping` | 30 | Control | echoed back as `Pong` |
| `Pong` | 31 | Control | 8-byte timestamp |
| `QualityPreset` | 32 | Control | 4-byte int index |
| `Handshake` | 40 | Control | UTF-8 session secret |
| `HandshakeOk` | 41 | Control | (none) |
| `HandshakeFailed` | 42 | Control | (none) |
| `ClipboardText` | 50 | Control | UTF-8 text |
| `ChatMessage` | 60 | Control | UTF-8 text |
| `Hello` | 70 | Control | `HelloMessage` (see above) |
| `HelloAck` | 71 | Control | `HelloMessage` (see above) |
| `HelloRejected` | 72 | Control | `HelloRejectedMessage` (see above) |
| `StreamFrame` | 80 | **Media** | `StreamFrameMessage` (see below) |

"Plane" is a static property of the type (`MessageTypeExtensions.GetPlane`), not runtime
state. In Phase 1, Control and Media both travel over the same TCP connection — Phase 4 is
expected to route Media types onto a WebRTC track while Control stays on a reliable channel,
without any change to this table.

### StreamFrame payload

```
[1 byte StreamId][1 byte StreamKind][4-byte SequenceNumber, little-endian uint32]
[8-byte CaptureTimestampTicks, little-endian int64]
[4-byte X][4-byte Y][4-byte Width][4-byte Height]  (all little-endian int32)
[4-byte ImageData length, little-endian int32][ImageData]
```

`StreamKind`: `1` = `Screen`, `2` = `Camera`, `3` = `Overlay`. This labels what the pixels are
for a viewer to composite/display with; it is not identity — `StreamId` is. Multiple streams
of the same `StreamKind` are legal (not expected today, but not forbidden).

Unlike v1's dual shape (`FullScreen` carries raw bytes, `ScreenDelta` wraps a region),
`StreamFrame` always carries explicit region fields — a full-frame update sets
`X=0, Y=0, Width/Height=actual dimensions` rather than needing a second message shape.

Field-width reasoning:

- **StreamId** (byte): up to 256 concurrent streams per peer, vastly more than any
  screen+camera+overlay combination needs. Not a design ceiling, just a generous width.
- **SequenceNumber** (uint32), scoped to one stream's lifetime — a reconnect or stream
  restart resets it to 0, it does not persist across a `StreamId`'s re-use across sessions.
  At a generous 60fps it wraps only after roughly 2.3 years of continuous, uninterrupted
  capture; a 16-bit counter would wrap in a little over an hour at a modest 15fps, which a
  real support session could plausibly exceed. The extra two bytes over a `ushort` are
  negligible next to JPEG-sized payloads. Comparisons should use wraparound-safe arithmetic
  (`PixelWizard.Core.Protocol.SequenceNumbers.Gap`) as defense in depth even though the wrap
  case is not expected to be reached in practice.
- **CaptureTimestampTicks** (int64): the same `DateTime.Ticks` epoch (`0001-01-01` UTC, 100ns
  resolution) `NetworkMessage.Timestamp` already uses, so the wire protocol has one timestamp
  convention rather than two. The extra bytes versus a 4-byte Unix-seconds/ms alternative are
  negligible next to frame payload size, and this avoids an epoch-conversion bug class.

Nothing in Phase 1 produces a real second stream — the exit criterion is that two concurrent
streams are *representable*, not that anything wires one up. That wiring is Phase 4's WebRTC
track work.

## Security posture

Every field above is attacker-controlled. The rule applied throughout: **no length-prefixed
read may silently truncate; every declared length is bounds-checked against what actually
remains in the buffer; malformed input throws a typed exception rather than misparsing.**
This applies to `NetworkMessage`, `ScreenDelta`, `StreamFrameMessage`, and
`HelloRejectedMessage` — the four types with a length-prefixed field. `HelloMessage` is
fixed-width and has no such field; a short buffer there throws `EndOfStreamException` from
`BinaryReader` with no additional code required.

## Browser viewer (separate protocol, not versioned)

`WebSocketHostServer.cs` speaks an entirely different, one-way, hand-tagged byte protocol to
the in-browser JS viewer, with no relation to `NetworkMessage`/`Hello`/`StreamFrame`:

| Tag byte | Meaning |
|---|---|
| `0x01` | Full JPEG frame follows |
| `0x02` | Delta + region follows |
| `0x10` | Client ping |

None of the Phase 1 changes above affect this format — no browser JS changes were needed in
any Phase 1 commit.
