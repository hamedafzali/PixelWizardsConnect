# Phase 4 Plan — WebRTC session transport and H.264

**Status: in progress.** On 2026-11-20 the owner said to go ahead without answering D1–D8
individually, so work proceeds on the proposed option for each (recommended (a) for D5). Any of
them can still be changed; a change after the sub-task that depends on it is a new commit, not a
rewrite. P4.1 is done.

ADR-001 scope: "SIPSorcery `ISessionTransport`; H.264 via `SIPSorceryMedia.FFmpeg`; keep
TCP+JPEG for LAN." Exit gate: "Session established between two different cellular networks."
Estimate: 4–6 weeks solo.

Phase 4 moves router-mediated sessions from direct TCP to WebRTC, using the signaling path
Phase 3 built, and adds H.264 video. Direct-IP LAN sessions stay on TCP+JPEG and do not change.

**Dependency on Phase 3.** The Phase 3 exit gate (P3.8) has not run yet. Phase 4 work up to
the first cross-network run (P4.5) does not need it, because CI pairs peers over loopback. The
Phase 4 gate needs the same deployed router and coturn, so P3.8 should run first. A P3.8
failure that changes the signaling or TURN design stops Phase 4 at that point.

---

## Where things stand

| Piece | Today |
|---|---|
| Transport | `ISessionTransport` (`src/PixelWizard.Core/Interfaces/ISessionTransport.cs`) is shaped for TCP: `ConnectAsync(host, port, useTls)` and `StartServerAsync(port, useTls)`, which completes when a viewer connects. The only real implementation is `TcpTransport`. |
| Sessions | `HostSession` takes an `ISessionTransport`. `ViewerSession` and `HostListener` take a `Func<ISessionTransport>`. The app hardcodes `new TcpTransport()` in `HostViewModel` and `ViewerViewModel`. |
| Wire format | One framed byte stream. Each message type has a static plane (`MessagePlane`): Media is `ScreenDelta`, `FullScreen` and `StreamFrame`, and everything else is Control. PROTOCOL.md already expects Media to move to a WebRTC track and Control to a reliable channel, with no change to the type table. |
| Negotiation | Hello/HelloAck (with `SupportedCodecs`; only `Jpeg = 1` exists), then Handshake/HandshakeOk carrying the session secret from `/connect`. |
| Consent | `HostSession` drops every send and ignores every inbound message until `Approve()`. `CaptureLoop` captures only while `IsApproved`. |
| Video | `IScreenCapture.Capture(forceFullFrame, jpegQuality)` returns JPEG-encoded 32×32-tile deltas. It is not a raw-frame interface. The Windows change detector uses System.Drawing; macOS and Linux share the Skia detector. The viewer decodes JPEG into a canvas on the UI thread. |
| Router path | `/register` and `/connect` swap a `host:port` and a session secret, and then the viewer opens direct TCP. Nothing traverses NAT. `SignalingClient` exists but the app does not use it. |
| FFmpeg | Only in the spike (`spikes/webrtc-desktop/dotnet-peer`: SIPSorcery 10.0.16, SIPSorceryMedia.FFmpeg 10.0.16, FFmpeg.AutoGen 9.0.1 override). Nothing in `src/`. |
| Packaging | Self-contained single-file publishes per OS into `dist/`. No native dependencies are staged except SkiaSharp through NuGet. The client project uses one `TargetFramework` per OS, never `TargetFrameworks`. |

### Findings this plan must honour (ADR-001, the T8 spike, Phase 3)

1. Trickle candidates from `onicecandidate`. Never rely on one final SDP. Never read
   `localDescription` after a fixed delay.
2. The H.264 offer needs the explicit fmtp
   `level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f`, or
   `setRemoteDescription` fails with `VideoIncompatible`.
3. Use a minimal LGPL FFmpeg build. This is a licensing requirement: standard builds link GPL
   x264/x265.
4. Vendor one specific tested FFmpeg build per OS. The macOS 8.x build was unobtainable during the
   spike, and Windows builds come from one volunteer's server.
5. Feed the project's own `IScreenCapture` implementations into `FFmpegVideoEncoder`, not
   SIPSorcery's capture sources.
6. Single-platform conclusions need checking on the other OS. Every CI check in this phase runs
   on both Windows and macOS.
7. The IceProbe announced its relay candidate twice (BACKLOG). The transport must tolerate a
   duplicate candidate, and P4.3 finds out where it comes from.

---

## Decisions for you before work starts

**D1 — Split establishment out of `ISessionTransport`.** A WebRTC connection is not "connect to
host:port". It is built from a `SignalingClient` that has already registered or joined.
Proposed: `ISessionTransport` keeps only the message surface (`IsConnected`, `MessageReceived`,
`SendMessageAsync`, `Disconnect`, and the events). Connecting moves to the code that creates the
transport: `TcpTransport` keeps its own `ConnectAsync` and `StartServerAsync`, and
`WebRtcTransport` gets an `EstablishAsync(signaling, turn, role)`. `HostSession` and
`ViewerSession` receive a transport that is connecting or connected. This is a refactor with no
behaviour change, done first and alone (P4.1). The alternative, making `WebRtcTransport`
implement `ConnectAsync(host, port, …)` by treating "host" as a room code, would hide the
difference behind arguments that mean something else.

**D2 — How the two planes map onto WebRTC.** Proposed:
- Control: one reliable, ordered data channel named `control`. It carries the existing framing
  unchanged.
- Media, first stage (P4.4): the existing JPEG `FullScreen`/`ScreenDelta` messages on a second
  reliable, ordered data channel named `media-jpeg`. This gives a working WebRTC session with no
  FFmpeg, so traversal and session behaviour can be tested before codecs are involved.
- Media, final stage (P4.6–P4.8): an H.264 video track. The `media-jpeg` channel remains the
  fallback when the two peers share no H.264 codec (D5).

The alternative, going straight to H.264, puts FFmpeg packaging on the critical path for the
first session that crosses NAT.

**D3 — Handshake over WebRTC.** `/signal` carries no session secret. The secret only protected
the TCP listener from anyone who could reach `host:port`. Over WebRTC the only peer that can
reach the host is the one that joined with the code, through a DTLS connection whose
fingerprint came over signaling. Decided: WebRTC sessions run the existing `Hello` and
`Handshake` unchanged, with an empty secret on both sides. That is exactly the direct-LAN path
(see the comment in `HostSession.HandleHandshake`), so the handshake code does not change and
`HandshakeVerified`, the consent modal and the viewer's approval-pending logic all stay as they
are. `/signal` carries no secret, and the consent modal is the gate. The router's existing
trust over the SDP is unchanged (SIGNALING.md, "The router is trusted with the SDP"). The
alternative, a host-generated secret sent through the router, adds no security, because the
router already sees everything that secret would protect.

**D4 — Consent ordering.** This is a rule, not an option; it is listed here so you can confirm
it. ICE and DTLS complete before the host has approved anything, which is unavoidable. Before
`Approve()`:
- no capture runs;
- the encoder is not created;
- no video frame is written to the track;
- nothing is sent on `media-jpeg`;
- inbound input, clipboard and chat are dropped, exactly as `HostSession` does today.

Escape and closing the window both deny, and nothing auto-approves. P4.4 and P4.6 each get
tests that assert zero media bytes leave before approval and that a denial closes the peer
connection.

**D5 — The H.264 encoder when no hardware encoder exists.** An LGPL build has no x264. Options:
- (a) Use the OS encoders that are always present: VideoToolbox on macOS, and Media Foundation
  (`h264_mf`) on Windows, which includes a software encoder. Fall back to JPEG over the
  `media-jpeg` channel only when encoder setup fails.
- (b) Ship Cisco's OpenH264 binary as a software fallback. This adds a separate download for the
  patent licence terms.

Recommended: (a). Linux is a community-support tier (ADR), so it gets the JPEG fallback unless
VAAPI happens to work. Windows Quick Sync, NVENC and AMF are used when FFmpeg finds them.
Measuring Windows hardware-encode CPU is still owed (ADR "Carried to Phase 4") and is part of
P4.6.

**D6 — Where the vendored FFmpeg comes from.** Proposed: a CI workflow, run on demand, builds a
minimal LGPL FFmpeg from a pinned source tag for Windows x64 and macOS arm64. It enables only the
H.264 decoder, the hardware encoders, swscale and the needed muxers, which also makes it much
smaller than the spike's 131 MB. It publishes the result as a GitHub release asset with a
committed SHA-256 manifest. Packaging and CI download it by checksum. The alternative, pinning a
third-party build (BtbN for Windows, a Homebrew bottle for macOS), conflicts with finding 4 and,
on macOS, links GPL and other libraries that are only ad-hoc signed and would fail notarization.

**D7 — Retiring `/register` and `/connect`.** Phase 3 D4 said these retire in Phase 4. Proposed:
the app switches router-mediated sessions to `/signal` in P4.5. The HTTP routes are removed from
the router in P4.10, after a released app no longer calls them, together with the BACKLOG
fingerprint row that Phase 3 D5 marked as superseded. Until then the router serves both.

**D8 — The exit gate, made concrete.** As written, the ADR gate is met by any connection. Proposed:

> Two desktops (one Windows, one macOS) on two different mobile carriers (laptops tethered to
> phones are fine). The macOS host shares its screen to the Windows viewer through the deployed
> router, and then the roles swap. Each direction must meet all of these:
> - the consent modal is shown and approved;
> - H.264 video plays;
> - mouse, keyboard and chat work;
> - the selected candidate pair is recorded;
> - glass-to-glass latency is recorded (a clock on screen, photographed against the viewer).
>
> The run is then repeated with relay forced, and the allocation must appear in coturn's log.

---

## Sub-tasks

Each sub-task is one or more commits. Every commit builds and runs. CI on Windows and macOS is
the verification.

### P4.0 — Plan accepted
Done 2026-11-20: proceeding on the proposed options for D1–D8 (see Status). D4 is a rule and
stands regardless.

### P4.1 — Split establishment out of `ISessionTransport` (D1)
- Move `ConnectAsync` and `StartServerAsync` off the interface. `HostListener` and
  `ViewerSession` call TCP establishment through a small factory, not through the interface.
- No behaviour change. All existing tests pass unchanged, except for type names in setup code.

### P4.2 — `WebRtcTransport`, control channel only
- New project `PixelWizard.Transport.WebRtc` (SIPSorcery 10.0.16, the same version as the probe).
- Moves in the peer setup the ADR listed under "Phase 4 entry points" (and the probe proved):
  - the ICE server list built from `TurnInfo`;
  - the ordered send queue (SDP, then candidates, then end-of-candidates);
  - the remote-candidate buffer;
  - sending end-of-candidates only when gathering completes;
  - reporting the nominated pair type.
- The `control` data channel carries the existing message framing.
- Tests:
  - two transports in one process, paired through a real router started by the test fixture,
    exchange Control messages in order;
  - a peer that drops raises `Disconnected` once;
  - a duplicate remote candidate is harmless.
- CI: on both OSes. This replaces the IceProbe CI step, and the probe stays as a diagnostic tool.

### P4.3 — Duplicate relay candidate
Find out whether the duplicate comes from SIPSorcery or from our queue, and fix it or document
it. This closes the BACKLOG row.

### P4.4 — JPEG over `media-jpeg` and the WebRTC session (D2, D3, D4)
- `HostSession` and `ViewerSession` run over `WebRtcTransport`, with Hello but no Handshake
  (D3).
- Media-plane messages go on `media-jpeg`, and Control-plane messages go on `control`, decided
  by `MessagePlane`.
- Consent tests over the real transport: no media bytes before `Approve()`; deny and close
  both tear down the peer connection; a late approval after the viewer left changes nothing.
- `media-jpeg` never builds a frame queue: the capture loop awaits each send, so the
  transport's backpressure paces capture, and the media channel's high-water mark is kept small
  so queued frames add little latency. Frames are not dropped individually, because a
  `ScreenDelta` depends on the frame before it. Measured in P4.2: SIPSorcery's data
  channel reaches about 3.3 MB/s on loopback even with the SCTP tuning (0.3 MB/s without it), so
  5–15 fps of 50–200 KB JPEG frames is the ceiling, not the norm. This path is a fallback; H.264
  is the real video path.

**Done 2026-11-21.** `HostSession`/`ViewerSession` take `ISessionTransport` (`StartServerAsync` and
`ConnectAsync` throw `InvalidOperationException` on a non-TCP transport). `WebRtcTransport` has two
reliable ordered channels, `control` and `media-jpeg`, routed by `MessagePlane`; each has its own
reassembly buffer, send lock and high-water mark (media 256 KiB, control 1 MiB), both feed one
ordered receive loop, and `EstablishAsync` completes when both are open. SCTP tuning is applied once
per association. On loopback a Ping sent right after a 2 MiB frame arrives in about 100 ms, while
the frame takes about 500 ms. Session tests over the real transport (`SessionOverWebRtcTests`) show
zero media bytes before `Approve()`, input/clipboard/chat dropped, and that deny, a throwing decider
and a late approval send nothing.

### P4.5 — App wiring for router-mediated sessions (D7)
- Router mode in the host (register, then wait for the peer) and in the viewer (connect by
  code) uses `SignalingClient` plus `WebRtcTransport`. Direct-IP LAN mode is unchanged.
- Host resume within the router's resume window when the signaling socket drops before pairing.
- First cross-network check: the user runs the existing app with JPEG over WebRTC on two
  networks. This is information only, not the gate.

**Done 2026-11-21, except the cross-network check, which is the user's manual step (pending).**
Router mode now goes through `IRelayConnector` (`WebRtcRelayConnector` in the app): the host
registers on `/signal`, shows the code, waits for the peer, builds `HostSession(transport, hello, "")`
(empty secret, D3) and only then establishes the transport with the TURN info from `peer-joined`;
the viewer joins by code and does the same with `ViewerSession`. Consent is the direct path's
flow, so capture starts only after `Approve()`. The ws/wss URL is derived from the existing
"host:port" router setting (no new UI). Resume: if the socket drops before pairing, the
registration resumes with the same code (about 55 s of retries inside the router's 60 s window).
After a session ends or is refused the host re-registers and shows a new code, since codes are
one-time use (direct mode re-listens on the same port). LAN discovery is not announced in router
mode. The app no longer calls `/register` or `/connect`; `RouterHttpClient` and `IRouterClient`
stay in the code and the HTTP routes retire in P4.10 (D7).

### P4.6 — Vendored FFmpeg (D6)
- A build workflow, a release asset, a SHA-256 manifest, and the licence notices shipped with
  each package.
- Encoder probe on both OSes: which H.264 encoders the build exposes, and whether one opens.
  Windows hardware-encode CPU is measured on whatever runner hardware exists, and the real
  Quick Sync measurement is recorded when a machine is available.

### P4.7 — Raw-frame capture
- Add a raw BGRA frame capture to `IScreenCapture` for the Windows and macOS hosts, next to the
  JPEG-delta path, which TCP keeps using.
- Linux keeps JPEG only (D5).
- The duplicate-detector BACKLOG row is decided here. The H.264 path needs no change
  detection, so the Windows detector stays for TCP+JPEG unless a step removes it.

**Done 2026-11-21.** Implemented as a separate optional `IRawScreenCapture` (not a member of
`IScreenCapture`, so Linux and the JPEG callers are unchanged); `WindowsScreenCapture` and
`MacScreenCapture` implement both. `RawFrame` carries width, height, stride, BGRA pixels, a
monotonic timestamp and a display id (Windows monitor device name, macOS CGDirectDisplayID). The
pixel buffer is rented from `ArrayPool` and the consumer disposes each frame. Width and height are
cropped to even in the capture. Same OS grab as the JPEG path (GDI `CopyFromScreen`,
`CGDisplayCreateImage`), no cursor, logical display size on macOS. Not wired into `CaptureLoop` or
the session (P4.8). The duplicate-detector BACKLOG row is decided: the Windows detector stays.

### P4.8 — H.264 track: host encode, viewer decode
- Hello gains `SupportedCodecs.H264`. The offer carries the 42e01f fmtp. When the peers share
  no H.264, the session falls back to `media-jpeg`.
- Host: the raw frames go to `FFmpegVideoEncoder` and then the video track. This starts only
  after `Approve()` and starts with a keyframe.
- Viewer: an FFmpeg decoder decodes the H.264, and the frames are drawn onto the existing canvas
  on the UI thread only.
- Keyframes: a viewer's PLI triggers an IDR from the host, and a periodic IDR is the safety net.
- Tests: an in-process round trip of a synthetic frame on both OSes (decoded pixels close to the
  source), and no frame is written to the track before approval.

### P4.9 — Packaging
- Per-OS staging of the vendored FFmpeg into the single-file publishes. The macOS .app gets
  signing-ready library paths. Licence notices are included.
- The packaged app starts a session on a machine with no FFmpeg installed.

### P4.10 — Retire the HTTP session routes (D7)
- Remove `/register` and `/connect` from the router and their client code. Update README,
  SIGNALING.md and BACKLOG.

### P4.11 — Exit-gate run (D8, needs you)
Record the results in `docs/PHASE4-GATE.md` with no personal details: carriers, OS, direction,
codec, selected pair, latency, and the coturn allocation line.

### P4.12 — Close
ADR phase table, BACKLOG, and the Phase 5 entry points: the browser viewer reuses `/signal`, the
`control` channel framing and the H.264 fmtp.

---

## What Phase 4 does not do

- No browser viewer over WebRTC and no annotations. That is Phase 5.
- No mobile app.
- No change to the direct-IP LAN path: TCP+JPEG, TOFU pinning and discovery stay as they are.
  The "Trust new certificate" re-pin row stays open for that path.
- No audio.
- No horizontal scaling or persistence for the router.
- No end-to-end fingerprint verification beyond the router trust SIGNALING.md describes.

## Risk

| Risk | Effect | Mitigation |
|---|---|---|
| SIPSorcery behaves differently on Windows | Transport works on one OS only | Every transport test runs on both CI OSes from P4.2 on |
| Minimal LGPL FFmpeg will not build or load on one OS | No H.264 there | The JPEG over `media-jpeg` path from P4.4 is a complete fallback; the gate records which codec was used |
| Media Foundation H.264 quality or CPU is poor | Degraded video on Windows without hardware encode | Lower resolution or frame rate (ADR); measured in P4.6 |
| Media flows before consent | Safety failure | D4 tests at P4.4 and P4.8, asserting zero media bytes before `Approve()` |
| Carrier blocks UDP | No direct path | TURN over TLS on 443 from the Phase 3 coturn template |
| Phase 3 gate fails | Signaling or TURN redesign | P3.8 runs before P4.5's first cross-network check |
| SIPSorcery data channel throughput (P4.2: 0.3 MB/s stock, about 3.3 MB/s with the SCTP sender tuning, loopback) | `media-jpeg` is slow; control traffic is unaffected | `SctpTuning` sets three private SctpDataSender fields by reflection and a test fails CI if an upgrade renames them; after a loss SIPSorcery falls back to its own slow start; backpressure paces `media-jpeg` capture; H.264 on an RTP track does not use SCTP |
