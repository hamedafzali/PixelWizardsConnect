# ADR-001: Target architecture for AR-capable, internet-reachable remote support

- **Status**: Accepted, pending Phase 0 spike validation (see *Validation gate*)
- **Date**: 2026-08-26
- **Author**: solo maintainer
- **Supersedes**: implicit architecture described in `docs/ROADMAP.md`

---

## Context

PixelWizard Connect today is a working prototype: a Go relay/router for pairing plus a
single Avalonia (.NET 9) client acting as both host and viewer on Windows, macOS and Linux.
The audit in `STATUS_REPORT.md` established the relevant facts. The ones that drive this
decision:

- **No NAT traversal.** The router exchanges a plaintext `host:port` string. No STUN/TURN/ICE,
  no WebRTC. Connectivity depends on the host being directly reachable.
- **No real video codec.** Screen sharing is 32x32 tile-diffed JPEG over TCP. There is no
  inter-frame compression.
- **`MainViewModel.cs` is a 1,331-line god object** owning navigation, both transport roles,
  capture, protocol dispatch, input, rendering, chat, clipboard and settings.
- **Protocol has no versioning or capability negotiation.** Peers must upgrade in lockstep.
- **Protocol assumes one stream, one peer, one desktop-shaped framebuffer.** Frame messages
  carry no stream identity; `TcpTransport` accepts exactly one client then stops listening.
- **Platform capture and input code is clean.** `IScreenCapture` and `IInputInjector` have
  small, self-contained per-OS implementations. This is the one part already shaped for reuse.

### Goals

1. Remote support product; self-hostable; consent-first; no third-party cloud dependency.
2. Desktop screen sharing and remote control on **Windows and macOS**.
3. Native **iOS and Android** apps.
4. AR-style field assist: technician shares a live camera feed, remote expert annotates it.
5. Works over the **open internet**, on any network, including carrier-grade NAT.
6. Buildable and maintainable by **one developer**, AI-assisted, no deadline, quality-first.

### Forces

**AR assist is not an additive feature.** It breaks two assumptions the current design rests on:

- *Field users are on cellular.* Desktop support tolerates "LAN, VPN or port-forward" because
  the target is a machine in an office. A phone in a plant room is behind CGNAT with no
  port-forward, ever. NAT traversal moves from roadmap item to hard prerequisite.
- *Camera video destroys tile diffing.* Tile diffing works because screens are mostly static.
  A handheld camera changes every pixel every frame through sensor noise and micro-motion, so
  every tile flags changed every tick. The pipeline does not degrade for AR; it collapses.
  Camera video requires an inter-frame codec and a transport permitted to drop frames.

**Video codecs are where .NET hurts.** Hardware H.264 encode/decode has no native .NET story;
it means shipping FFmpeg binaries per platform. Other ecosystems get it from the OS or from
libwebrtc for free.

**WebRTC in .NET is one library.** SIPSorcery is genuinely good and genuinely pure C#, but it
is a small project reimplementing what Google maintains at scale. `flutter_webrtc` and browsers
wrap actual libwebrtc. The risk is asymmetric and concentrated entirely on the .NET side.

**Mobile is a new codebase regardless of language choice.** Avalonia has iOS/Android heads, but
camera capture, hardware encoding, torch, and orientation all require hand-written platform
bindings. Choosing Avalonia for mobile means doing the hardest version of the mobile work purely
to preserve language uniformity — and the shared C# that would preserve (`MainViewModel`) is
being deleted anyway.

---

## Decision

**Keep .NET for desktop. Add Flutter for mobile. WebRTC as the universal transport.
Extend the Go router into real signaling with a self-hosted TURN relay.**

```
+---------------------+         +----------------------+
|  Desktop agent      |         |  Field app           |
|  .NET 9 / Avalonia  |         |  Flutter (iOS/Andr)  |
|  Windows + macOS    |         |  camera + annotate   |
|  capture . inject   |         |  flutter_webrtc      |
|  SIPSorcery WebRTC  |         |  (real libwebrtc)    |
+----------+----------+         +----------+-----------+
           |                               |
           |   +-----------------------+   |
           +---+ router-server (Go)    +---+
               | WS signaling, SDP/ICE |
               | coturn (TURN relay)   |
               +-----------+-----------+
                           |
               +-----------+-----------+
               |  Expert viewer        |
               |  browser, zero-install|
               |  canvas annotations   |
               +-----------------------+
```

### Component decisions

| Component | Choice | Rationale |
|---|---|---|
| Desktop agent | .NET 9 + Avalonia, retained | Existing capture and input code is the irreplaceable asset; P/Invoke is the right tool for input injection and future UAC/secure-desktop work |
| Mobile field app | Flutter | `flutter_webrtc` wraps real libwebrtc; camera, codecs and permissions are solved; keeps the ARKit/ARCore path open via platform channels |
| Expert viewer | Browser, evolved from the existing embedded viewer | Free hardware decode, free WebRTC, canvas annotations, zero install for someone joining a support call now. Not a third codebase — one page |
| Transport | WebRTC (SIPSorcery on desktop, libwebrtc elsewhere) | Only realistic path through CGNAT; DTLS-SRTP replaces the current always-trust TLS |
| Signaling | Existing Go router, extended to persistent WebSocket | Already written, already rate-limited, already self-hostable |
| Relay | Self-hosted coturn | Preserves the no-third-party-cloud stance; relays encrypted media it cannot read |
| Linux desktop | Demoted to community-support tier | Untested surface, not a stated goal |

### Annotation model

**Screen-space annotations with freeze-frame**, not world-anchored AR, for the first release.

World anchoring requires ARKit/ARCore sessions, pose streaming, 2D-tap-to-3D-ray hit testing and
per-frame reprojection. It degrades exactly where this product is used — brushed metal, glass,
uniform paint, poor light — and introduces depth ambiguity that needs heuristics users must undo.
Freeze-frame is what world-anchored products fall back to in practice anyway.

Three design constraints keep world anchoring an *additive* future change rather than a rewrite:

1. `IAnchorResolver` abstraction, with `ScreenSpaceResolver` as the only implementation today.
2. Annotation wire messages carry an **optional `Pose` field** (camera transform + intrinsics)
   from day one. Unused by the current resolver.
3. Annotations are stored as **vector primitives against a `FrameId`**, never rasterized into
   the video. Rasterizing is the decision that would make world anchoring impossible to retrofit.

---

## Alternatives considered

| Option | Effort | Codebases | Codec/WebRTC risk | Mobile quality | Verdict |
|---|---|---|---|---|---|
| **A.** All .NET/Avalonia incl. mobile | Low migration | 1 + Go | High — SIPSorcery *and* FFmpeg *and* hand-written camera bindings | Poor | Rejected: concentrates every hard problem on the weakest platform |
| **B.** .NET desktop + Flutter mobile | Medium | 2 + Go | Moderate — SIPSorcery on desktop only | Excellent | **Chosen** |
| **C.** Full Flutter, all platforms | High | 1 + Go | Low | Excellent | Rejected — but see below |
| **D.** Electron/Tauri desktop + Flutter | High | 2–3 + Go | Low | Excellent | Rejected: no advantage over C, more moving parts |

**Option C deserves fairness.** Of 6,224 lines, `MainViewModel` (1,331) is being rewritten
regardless, `MainWindow.axaml` (884) is rewritten for the new UX regardless, and the transport
layer is replaced by WebRTC regardless. The genuinely irreplaceable asset is roughly 800 lines
of platform capture and input code. "Rewrite everything" is smaller than it sounds.

C was rejected on **input injection and privileged desktop access**. Injecting keystrokes and
mouse events, and later handling UAC prompts and the Windows secure desktop, is precisely what
.NET plus P/Invoke does well and what Flutter would push through hand-written FFI on every
platform. That capability is on the critical path for desktop remote control — the product that
already exists and already works.

---

## Consequences

### Positive

- WebRTC replaces, rather than adds to, the security findings: DTLS-SRTP with fingerprint
  verification via signaling is strictly stronger than accepting any TLS certificate.
- Hardware codecs on mobile and browser come free; only the desktop needs FFmpeg.
- The browser expert client gives zero-install joining, the highest-leverage feature per unit
  of effort in the whole plan.
- Protocol v2 work is implementation-independent — it survives even if the spike fails and the
  architecture falls back to Option C.

### Negative / accepted costs

- Two client codebases and two toolchains to maintain solo.
- Dart is a new language (est. one weekend; the mobile *platform* was always the real cost).
- coturn is a second service to deploy, secure and monitor. TURN relaying carries media for
  roughly 10–20% of sessions, with a corresponding bandwidth cost.
- SIPSorcery is a single-library dependency on the desktop side — the principal technical risk.
- Linux desktop users lose first-class support.

### Validation gate

This ADR is **provisional until two throwaway spikes pass** in Phase 0. No production code.

1. **SIPSorcery ↔ browser**: H.264 video both directions plus a data channel, through a TURN
   relay, on Windows and macOS. Measure CPU and end-to-end latency.
2. **`flutter_webrtc` ↔ that same SIPSorcery peer**: camera video from a real phone.

Pass → Option B is confirmed; proceed to Phase 1.
Fail (interop problems or unacceptable CPU) → fall back to Option C **before** four phases are
built on top of the assumption. Phase 1's protocol work carries over either way.

Running this gate now rather than at Phase 4 is the difference between a two-week correction and
a six-month one.

## Gate outcome — 2026-08-27

**Option B confirmed.** Evidence: `docs/spikes/T8-sipsorcery.md`.

| Risk the gate tested | Result |
|---|---|
| SIPSorcery browser interop | Passed — H.264 both directions plus data channel, real Chrome |
| TURN relay traversal | Passed — confirmed at coturn, not inferred from ICE state |
| Hardware encode reachable | Passed — VideoToolbox macOS (~0.5 core); Quick Sync already in stock BtbN Windows builds |
| Stability | Passed — 600s, memory flat 85–150MB, 1196/1196 data-channel messages each way |
| Packaging and licensing | Understood, not blocking — minimal LGPL FFmpeg build required |

### Carried to Phase 4, not gate items

- **Windows hardware-encode CPU.** Needs a Quick Sync machine. A poor result is a
  degraded-mode problem (lower resolution or framerate without a hardware encoder), not an
  architecture problem — Flutter via libwebrtc faces the identical constraint, so it cannot
  favour Option C.
- **T9 mobile interop.** Blocked on device access. Low risk: `flutter_webrtc` wraps real
  libwebrtc against a now-proven desktop peer.

### Findings that change Phase 3 and Phase 4

- **SIPSorcery does not merge relay/TURN candidates into `localDescription.sdp`**, though it
  does merge host candidates. Signaling must trickle relay candidates from `onicecandidate`
  rather than sending a final SDP blob. Host-only paths unaffected. Candidate for an
  upstream issue; if fixed, the workaround disappears.
- **Never read `localDescription` after a fixed delay** — wait for gathering-complete.
- **Any splice-style workaround must check for candidates already present**, or it produces
  duplicate-candidate SDP.
- **Minimal LGPL FFmpeg build is a licensing requirement**, not a size optimization —
  standard distributions link GPL x264/x265.
- **Vendor a specific tested FFmpeg build.** macOS 8.x was unobtainable and needed a
  version-skew override; Windows depends on BtbN, one volunteer's build server.
- **Feed the existing `IScreenCapture` implementations into `FFmpegVideoEncoder`** rather
  than SIPSorcery's own capture sources — keeps platform code the project already owns.

### Process note

Finding #4 was reported three times with three different explanations before resolving
correctly, and only because Windows contradicted macOS. Treat single-platform conclusions
in the spike report with corresponding caution.

---

## Implementation phases

| Phase | Work | Exit gate | Est. solo effort |
|---|---|---|---|
| **0** | CI on Windows/macOS; golden-file protocol tests; detector fixture tests; cert pinning; trusted-proxy CIDR for `X-Forwarded-For`; document the direct-connect consent model. Then the two spikes. | Green CI on both OSes; both spikes pass | 2–3 wk |
| **1** | Protocol v2: skip-unknown envelope, `StreamId`/`StreamKind`, `Hello`/capabilities incl. peer role, control plane split from media plane | Unknown message type does not kill a session | 2 wk |
| **2** | Modularize: `Protocol`/`Core`/`Session`/`Media`/`Transport.*`/`Platform.*`; delete `MainViewModel`; DI; split the 884-line XAML | `MainViewModel` under 300 lines; zero `Dispatcher` references in `Session` | 4–6 wk |
| **3** | Go router to persistent WebSocket signaling; SDP/ICE exchange; TURN credential issuance; coturn deployment | Two peers exchange ICE candidates across carriers | 2–3 wk |
| **4** | SIPSorcery `ISessionTransport`; H.264 via `SIPSorceryMedia.FFmpeg`; keep TCP+JPEG for LAN | Session established between two different cellular networks | 4–6 wk |
| **5** | WebRTC browser expert viewer; annotation layer (vector primitives, `IAnchorResolver`, optional `Pose`, freeze-frame). Ship on desktop screen share first | Expert draws on a live desktop session from a browser | 4 wk |
| **6** | Flutter mobile: viewer role first, then camera host; store builds | Phone joins a desktop session, then shares its camera | 6–8 wk |
| **7** | Field polish: bidirectional freeze-frame, laser pointer, torch, zoom, reconnect on network change, poor-network behaviour | Usable on real cellular in a real plant room | 3–4 wk |

Phase 5 shipping annotations on **desktop screen share first** is deliberate: "draw on my screen"
is independently valuable, exercises the entire new protocol before mobile exists, and remains
the permanent fallback whenever camera or tracking conditions are poor.

---

## Revisit triggers

- Either Phase 0 spike fails → reopen the Option B vs C decision.
- Real usage shows screen-space annotation is insufficient → reopen world anchoring, which the
  `IAnchorResolver` and `Pose` design deliberately keeps cheap.
- SIPSorcery becomes unmaintained → evaluate migrating the desktop agent to Flutter (Option C).
- A second developer joins → the two-codebase cost drops sharply; revisit Linux support tier.
