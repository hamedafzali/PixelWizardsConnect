# Phase 4 Plan — WebRTC session transport and H.264

**Status: implemented; exit gate (P4.11) pending.** The code and docs for P4.1–P4.10 and P4.12 are
done. The gate is a manual run on two machines and two carriers, and it has not been done, so Phase
4 is not closed. On 2026-11-20 the owner said to go ahead without answering D1–D8 individually, so
work proceeded on the proposed option for each (recommended (a) for D5). Any of them can still be
changed; a change after the sub-task that depends on it is a new commit, not a rewrite.

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
5. Feed the project's own raw capture (`IRawScreenCapture`) into an encoder we drive ourselves,
   not SIPSorcery's capture sources. The T8 spike's claim that `SIPSorceryMedia.FFmpeg`'s
   `FFmpegVideoEncoder` works does not hold for the OS encoders: it aborts on `frame->format` with
   `h264_videotoolbox`, and only appeared to work on a Mac with Homebrew FFmpeg because it silently
   picked the GPL `libx264`. libavcodec is therefore driven directly through FFmpeg.AutoGen
   (`PixelWizard.Media.H264`: `H264Encoder`, `H264Decoder`, `FfmpegLoader`); SIPSorcery only
   packetises and carries the H.264 track.
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
stay in the code and the HTTP routes retire in P4.10 (D7); both are removed there.

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

### P4.8 — H.264 track: host encode, viewer decode (done)
- Hello gains `SupportedCodecs.H264` (flag value 2, no extra bytes, no version bump: an older peer's
  0x01 reads as JPEG only). A peer advertises it only when FFmpeg loaded and (host) an encoder
  opens or (viewer) the decoder opens. H.264 is used only when both advertise it and the
  transport is WebRTC with a negotiated video m-line; otherwise the session stays on `media-jpeg`.
  TCP stays TCP and JPEG. The offer carries the 42e01f fmtp.
- `WebRtcTransport` (`IVideoTrackTransport`): an H.264 track at a 90 kHz clock, an API to send an
  Annex-B access unit, an event with reassembled access units, a viewer PLI and a host
  `KeyframeRequested`. The control and `media-jpeg` channels are untouched.
- Host: `H264HostPipeline` (capture, `H264Encoder`, track at about 15 fps) is built only inside
  `HostSession.Approve()`, so there is no capture, no encoder and nothing on the track before
  consent. The first frame is a keyframe, a PLI forces an IDR, and a periodic IDR comes from the
  2 s GOP. If the encoder cannot be created the session falls back to `media-jpeg` and logs why.
  The encoder is released on disconnect or deny. Encoder latency is not assumed away (VideoToolbox
  holds about 3 frames).
- Viewer: `H264ViewerReceiver` decodes on a background thread; frames are drawn onto the existing
  canvas on the UI thread only. Decode errors are dropped and a PLI is sent at most every 500 ms.
- FFmpeg is found through `PIXELWIZARD_FFMPEG_DIR` or `AppContext.BaseDirectory/ffmpeg`
  (`FfmpegBootstrap`); if neither loads, no H264 is advertised. Shipping it is P4.9.
- VideoToolbox is asked for `constrained_baseline`, so its SPS carries constraint_set1 as the
  42e01f fmtp says.
- Tests: a round trip over a paired `WebRtcTransport` and through the host and viewer sessions
  (decoded pixels close to the source), no video before `Approve()` and none after a deny,
  negotiation fallback to `media-jpeg`, PLI leading to a keyframe, Hello wire compatibility. The
  FFmpeg-gated cases run in CI via `PIXELWIZARD_FFMPEG_DIR` (the WebRTC assert count is 32).
- Not yet exercised here: Windows (`h264_mf`, and the Windows host and viewer paths) is covered by
  CI only; a real two-machine run is the P4.11 gate.

### P4.9 — Packaging
- Per-OS staging of the vendored FFmpeg into the single-file publishes. The macOS .app gets
  signing-ready library paths. Licence notices are included.
- The packaged app starts a session on a machine with no FFmpeg installed.

**Done 2026-11-23.** `packaging/ffmpeg/stage-ffmpeg.sh` and `.ps1` take the zip from
`PIXELWIZARD_FFMPEG_ZIP` or the release in `manifest.json` (not published yet: the download fails
loudly), compare its SHA-256 with the manifest before unpacking, and stage `ffmpeg/` next to the
executable: `Contents/MacOS/ffmpeg` in the .app, beside the exe on Windows (`installer.iss` already
ships subfolders). FFmpeg is required by default; `--no-ffmpeg` / `-NoFfmpeg` builds JPEG-only. Linux is
JPEG-only. The macOS dylibs already use `@loader_path` names, so the script only verifies that no absolute
path or `LC_RPATH` remains (nothing is rewritten; ad-hoc codesign of the built .app verifies). The zip's
`licenses/` folder (LGPL text, notices, build info) ships inside the package and `packaging/README.md`
states the replace-the-libraries right. CI stages the pinned run's artifact with these scripts and runs the
FFmpeg tests against the staged directory. Verified locally on macOS only; the Windows scripts and the
CI step are unrun. Not verified: a launch of the packaged app on a clean machine (P4.11).

### P4.10 — Retire the HTTP session routes (D7)
- Remove `/register` and `/connect` from the router and their client code. Update README,
  SIGNALING.md and BACKLOG.

**Done 2026-11-23.** The router serves only `/signal` and `/health`; `/register`, `/connect`,
the registration store, their tests and `RouterHttpClient`/`IRouterClient` are gone (a test pins
the two routes to 404). `/signal` keeps its rooms, code generation (now unique among rooms) and
its two rate limiters, renamed `hostLimiter`/`joinLimiter`. `/health`'s `hosts` now counts open
`/signal` registrations. The URL derivation `SignalingClient` used moved to `RouterUrl`. The
BACKLOG fingerprint row is closed. **A deployed router must be redeployed** to drop the routes;
an old app build would stop working against it, but no app release exists.

### P4.11 — Exit-gate run (D8, needs you)
Record the results in `docs/PHASE4-GATE.md` with no personal details: carriers, OS, direction,
codec, selected pair, latency, and the coturn allocation line.

### P4.12 — Close
ADR phase table, BACKLOG, and the Phase 5 entry points: the browser viewer reuses `/signal`, the
`control` channel framing and the H.264 fmtp.

**Done 2026-11-23, with the gate pending.** The ADR phase table marks Phase 4 implemented with
P4.11 pending (the Phase 3 row keeps its own pending gate: [PHASE3-GATE.md](PHASE3-GATE.md) says "Not
yet run"). BACKLOG closes the Windows detector-suite row (the `windows-latest` job runs and asserts the
System.Drawing suite) and re-tags the two rows Phase 4 left open; it adds the Phase 4 follow-ups:
Developer ID signing and notarization, no Linux FFmpeg, the FFmpeg release not published, the router
redeploy, and Windows packaging only exercised through CI staging. The Phase 5 entry points are below.
Nothing here claims the gate passed.

#### Phase 5 entry points

What a browser viewer (Phase 5) can build on, and what it cannot. Every path below exists.

**Reuses as is**
- **`/signal`.** [SIGNALING.md](SIGNALING.md): "Transport", "Envelope", "Sequence", "Messages"
  (pairing and relayed types), "Reconnecting", "Limits", "Security posture". Router:
  `router-server/signal.go` (rooms, rate limiters, relay), `router-server/turn.go` (TURN credential
  issuance carried in `joined`). The text-frame JSON protocol is browser-native (`WebSocket`).
  A browser joins with `join {code}` and uses the `turn` block as `iceServers`. Reference client for
  the message order: `src/PixelWizard.Signaling/SignalingClient.cs`, types in
  `SignalingMessages.cs`. Needed router change: the `Origin` allow-list that SIGNALING.md reserves
  (`router-server/signal.go`, where `websocket.Accept` is called).
- **Control data channel.** Label `control` (`WebRtcTransport.ControlChannelLabel`, ordered,
  reliable), in `src/PixelWizard.Transport.WebRtc/WebRtcTransport.cs`. Message body:
  `NetworkMessage.Serialize()` (`src/PixelWizard.Protocol/NetworkMessage.cs`, layout in
  [PROTOCOL.md](PROTOCOL.md) "Transport framing": type, timestamp, inner length, data); type table
  and plane in `MessageType.cs` and `MessagePlane.cs`; Hello/HelloAck in `HelloMessages.cs`. Over a data
  channel the TCP 4-byte outer length is not used. Each data-channel message is a 1-byte header
  (0 = last chunk, 1 = more follow) plus at most 16 KiB of body, and the receiver concatenates until it
  sees 0 (`SendMessageAsync` and `OnChannelData`). **PROTOCOL.md does not document this chunk header**
  (D2 said "unchanged", which is true of the message body only); write it down before a second
  implementation relies on it.
- **H.264 offer.** The fmtp `level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f`
  is `WebRtcTransport.H264Fmtp` (payload type 96, 90 kHz). 42e01f is constrained baseline, level 3.1,
  a profile browsers are expected to decode. The encoder is asked for exactly that profile and the SPS matches
  (`src/PixelWizard.Media.H264/H264Encoder.cs`). Access units are Annex-B with SPS/PPS in-band on every
  keyframe (`H264Encoder.cs` clears the global-header flag, repeats SPS/PPS, and prepends them if
  missing; `AnnexB.cs`), which is what a browser's RTP H.264 depacketiser expects.
  The H.264 path has not been tried against a browser; check it in Chrome, Firefox and Safari.
- **Keyframe on demand.** The viewer sends RTCP PLI (`WebRtcTransport.RequestKeyframe`); the host
  turns a PLI or FIR into `IVideoTrackTransport.KeyframeRequested`
  (`src/PixelWizard.Core/Interfaces/IVideoTrackTransport.cs`), and `H264HostPipeline.cs` forces an IDR.
  A browser sends PLI by itself when its decoder loses a reference frame, so the host side needs no
  change; nothing needs to send PLI by hand.
- **Codec negotiation.** `Hello.Codecs` (`SupportedCodecs`, flag 2 = H264, in `HelloMessages.cs`). A
  browser viewer would send Hello on `control` and advertise H.264 as it already can decode it.
- **Consent.** `HostSession.Approve()` (`src/PixelWizard.Session/HostSession.cs`) and
  `ConsentGate.cs`; decision D4 above. The H.264 pipeline and the `media-jpeg` channel start only
  after `Approve()`, so a browser viewer gets the same gate with no host change. D3 (empty secret,
  the consent modal is the gate) applies to it too.
- **Existing browser viewer, for context.** `src/PixelWizard.Transport.WebSocket/WebSocketHostServer.cs`
  serves a separate JPEG-over-WebSocket viewer (PROTOCOL.md "Browser viewer"); its smoke test is
  `tests/WebViewerSmoke/drive.mjs`. It does not use WebRTC, `/signal` or `NetworkMessage`.

**A browser cannot reuse**
- **`SctpTuning.cs`.** It sets private fields of SIPSorcery's SCTP sender by reflection
  (`src/PixelWizard.Transport.WebRtc/SctpTuning.cs`); a browser's SCTP stack has its own pacing. The
  host's sender-side pacing, the 1 MiB high-water mark and the poll on `bufferedAmount` in
  `WebRtcTransport.cs` are SIPSorcery workarounds. A browser viewer may pass bytes to `RTCDataChannel`
  with its own `bufferedAmountLowThreshold`.
- **`media-jpeg`.** Stays the host's fallback for peers with no H.264. The host is the offerer and
  creates both `control` and `media-jpeg` before the offer (`EstablishAsync`), and `EstablishAsync`
  completes only when both are open. A browser answerer receives both through `ondatachannel`; it
  must let `media-jpeg` open (it can stay unused when H.264 is negotiated). Making the second channel
  optional would be a host change.
- **FFmpeg and `PixelWizard.Media.H264` decode.** A browser decodes natively; only the host-side
  encoder path is shared.
- **The `WebRtcTransport` class and `ISessionTransport`.** They are C#. A browser needs a JS
  implementation of the same sequence: trickle candidates from `onicecandidate`, no fixed delays,
  hold remote candidates until the remote description is set (the Phase 3 and 4 findings above).
- **TOFU/TLS, `TcpTransport`, UDP discovery.** LAN-only.

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
