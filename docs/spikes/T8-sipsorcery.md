# T8 — SIPSorcery WebRTC Spike

Throwaway spike code lives in `spikes/webrtc-desktop/` (not referenced by the `.sln`, not
production code). This document is the deliverable.

## Verdict

**Yes, with real caveats.** SIPSorcery can carry H.264 both directions plus a data channel to a
real browser through a TURN relay, on macOS, using hardware (VideoToolbox) encode. It got there
only after finding and working around three separate defects, one of which is a genuine
SIPSorcery interop bug that will bite any real signaling implementation, not just this spike's
crude one. None of the problems were "SIPSorcery fundamentally can't do WebRTC" — they were
"SIPSorcery's SDP/ICE output doesn't match what a standard non-trickle exchange with Chrome
needs," which is fixable but is real, uncosted Phase 3/4 engineering work.

**Windows: half-answered.** A GitHub Actions `windows-latest` CI run (workflow_dispatch-only,
`.github/workflows/spike-t8-windows.yml`, not part of the gating build) answered the "does it work
at all" half: `FFmpeg.AutoGen` loads cleanly against a downloaded Windows FFmpeg build, the fmtp
workaround from finding #3 still produces an offer real Chrome accepts, and — a genuinely
surprising result — Intel Quick Sync (`h264_qsv`/`hevc_qsv` via `libvpl`) **is** compiled into the
readily-available Windows FFmpeg builds, in both the GPL and LGPL variants, unlike the VideoToolbox
situation on macOS which needed no separate codec library at all. The GPL/LGPL licensing tension
found on macOS (finding present in "Also checked" below) reproduces identically on Windows: the
LGPL-clean build drops `libx264`/`libx265` but keeps every hardware-vendor encoder path (QSV,
NVENC, AMF, Media Foundation). **What's still open is the actual cost**: `windows-latest` has no
GPU, so it cannot exercise Quick Sync — the CPU number that half needs is still unmeasured, and
remains pending real Windows hardware. See "Windows — partial" and the narrowed "Item 2" below for
the full breakdown and what wasn't answerable in CI (in particular, TURN/ICE connectivity end-to-end
was not established in this run — see below).

## What was built

- `spikes/webrtc-desktop/dotnet-peer/WebrtcSpike/Program.cs` — a .NET console app using
  SIPSorcery + `SIPSorceryMedia.FFmpeg`. Offers one H.264 video track and one data channel,
  `iceTransportPolicy = relay` only. Video source is a synthetic 1920x1080 BGR frame generator
  (see "What was substituted" below), encoded via `FFmpegVideoEncoder`, tried against
  VideoToolbox hardware first with a software fallback. Self-samples CPU (`Process.TotalProcessorTime`
  delta / wall-clock delta / core count) and RSS once per second.
- `spikes/webrtc-desktop/web-peer/index.html` — a vanilla `RTCPeerConnection` page, no framework.
  `canvas.captureStream(30)` as the video source, `iceTransportPolicy: 'relay'`, explicit
  `setCodecPreferences` to prefer H264.
- `spikes/webrtc-desktop/web-peer/drive.mjs` — a Playwright driver that loads `index.html` in a
  real browser and evaluates JS in-page. Stands in for a human opening a tab, since there is no
  human/GUI available in this environment.
- Signaling: crude file-drop (`offer.json`/`answer.json` in a shared directory, polled). This is
  the "copy-paste SDP through a text box" the spec explicitly allows, mechanized because there is
  no human to do the copy-pasting here.
- coturn as the TURN server, static long-term credentials (`spike`/`spikepass`, realm
  `spike.local`).

### What was substituted, and why

- **No screen capture.** `FFmpegScreenSource` needs an interactive macOS Screen Recording
  permission grant, which isn't obtainable in this headless/automated environment. Substituted a
  synthetic generated frame fed directly to `FFmpegVideoEncoder`. This exercises the real
  encode → RTP → transport path; it does not exercise SIPSorcery's screen-capture source code
  specifically, which is worth a spot-check before Phase 4 commits to it.
- **No real browser operator.** Playwright drives real Google Chrome (`channel: 'chrome'`),
  not Playwright's bundled Chromium — see the codec-availability finding below for why that
  distinction matters.
- **No photographed clock for glass-to-glass latency.** No camera or human is available. Frames
  carry an embedded millisecond timestamp instead (see Measurements).

## Relay traversal evidence

`iceTransportPolicy` was set to `relay` on both sides; both sides' ICE candidates in the
exchanged SDP are `typ relay`, and connectivity was verified **at the TURN server**, not inferred
from ICE state, per the spec's explicit requirement. coturn's own log, after a full successful
connect (`native-run/coturn-native.log`):

```
07:06:41.429 session 007000000000000005: incoming packet ALLOCATE processed, success
07:06:52.005 session 007000000000000006: incoming packet ALLOCATE processed, success
07:06:52.006 session 007000000000000006: peer 127.0.0.1 lifetime updated: 300
07:06:52.006 session 007000000000000006: incoming packet CREATE_PERMISSION processed, success
07:06:52.483 session 007000000000000005: incoming packet CREATE_PERMISSION processed, success
07:06:52.510 session 007000000000000006: incoming packet CHANNEL_BIND processed, success
07:06:58.703 session 007000000000000006: usage: rp=303, rb=201865, sp=1748, sb=1159408
07:06:58.704 session 007000000000000006: peer usage: rp=1745, rb=1152200, sp=300, sb=201128
07:07:04.689 session 007000000000000006: usage: rp=350, rb=251445, sp=1699, sb=1143835
07:07:10.391 session 007000000000000006: usage: rp=406, rb=351025, sp=1642, sb=1107514
```

`rb`/`sb` (relayed bytes received/sent) climb continuously and symmetrically across both
sessions — roughly 1.1–1.2MB relayed each direction over the ~20s sample window, ~1,600–1,750
packets each way. This is real media+data traffic passing *through* the TURN relay's allocated
ports, not a peer-to-peer shortcut. (Both peers run on `127.0.0.1` for this spike — see the
loopback-permission finding below for why that specific detail turned out to matter.)

## What failed, broke, or needed a workaround

This is the part that matters most for the Phase 4 estimate. Four separate, independent problems
had to be found and fixed before the connection worked at all. Three of them are specific to
running everything on one loopback machine and would not recur in production; **one is a real
SIPSorcery/browser interop defect that will recur.**

### 1. FFmpeg.AutoGen / native FFmpeg ABI mismatch (environment/packaging)

`SIPSorceryMedia.FFmpeg` 10.0.16 pins `FFmpeg.AutoGen` 8.1.0, which expects FFmpeg 8.x's exact
shared-library ABI (`libavdevice.62`). Homebrew and evermeet.cx currently only distribute FFmpeg
7.x or 9.x — **FFmpeg 8.x is not obtainable from either of the two normal macOS distribution
channels right now.** Installed FFmpeg was 9.x (`libavdevice.63`), causing
`DllNotFoundException: Unable to load DLL 'avdevice.62'`.

**Fix:** explicitly overrode the NuGet reference to `FFmpeg.AutoGen 9.0.1` to match. This resolved
the load error and the spike ran correctly against it, but SIPSorceryMedia.FFmpeg 10.0.16 was
*compiled* against FFmpeg.AutoGen 8.1.0's API surface — this override is a version-skew gamble
that happened to work, not a supported configuration. **Phase 4 cost:** the shipped app must pin
and bundle an exact, tested FFmpeg build rather than relying on whatever the OS/package manager
has, or this exact class of breakage recurs on every user machine with a different FFmpeg
installed.

### 2. Bundled Chromium has no H.264 codecs at all (test-harness, not SIPSorcery)

Playwright's default Chromium build ships with H.264 stripped (patent-encumbered codec, absent
from open-source Chromium builds). `RTCRtpSender.getCapabilities('video')` returned zero H264
entries, and negotiation failed outright. **This is a browser-distribution fact, not a SIPSorcery
defect** — confirmed by switching to `channel: 'chrome'` (real Google Chrome, already installed),
which exposed 7 real H264 profiles and fixed it immediately. Worth noting for any future
automated-testing setup around this feature: CI Chromium needs the same fix.

### 3. Offer missing `a=fmtp` line → `VideoIncompatible` (SIPSorcery usage gap)

The first working offer's H.264 line was `a=rtpmap:96 H264/90000` with no accompanying `a=fmtp:96`
at all. `pc.setRemoteDescription(answer)` returned `VideoIncompatible` even though the answer's
codec name and payload type matched exactly — SIPSorcery's H.264 compatibility check also
requires fmtp/profile-level-id agreement, and gave no fmtp to agree against.

**Fix:** explicitly construct the offered `VideoFormat` with
`"level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"`. This is a
one-line fix once diagnosed, but it means SIPSorcery's default `VideoFormat` construction for
H.264 is not browser-interop-ready out of the box — anyone doing this integration needs to know
to supply fmtp explicitly. Minor but real Phase 3 documentation/wrapper cost.

### 4. SIPSorcery does not merge relay/TURN candidates into `localDescription.sdp` — host candidates only (real SIPSorcery defect)

This is the load-bearing finding of the spike. After fix #3, codec negotiation succeeded
(`setRemoteDescription result: OK`) but ICE never progressed past `checking` → `failed` on the
.NET side, and stayed at `new` forever on the Chrome side. Root cause, confirmed by inspecting
the raw SDP text on disk: **`offer.json` had zero `a=candidate` lines**, even though
`pc.onicecandidate` had already fired and `Task.Delay(2500)` was given for gathering to finish
before `pc.localDescription.sdp.ToString()` was serialized to disk. SIPSorcery fires the trickle
event but does not merge those candidates into `localDescription.sdp` — unlike Chrome, which does
embed them once gathering completes even when the caller is using a "wait then send the whole
blob" pattern rather than true per-candidate trickling.

**Consequence:** confirmed by a later macOS re-test (see the update below) to be a genuine,
candidate-type-dependent behavior, not a timing artifact: SIPSorcery merges gathered **host**
candidates into `localDescription.sdp` on its own, but never merges **relay (TURN)** candidates,
no matter how long gathering is given to finish. For a relay-based exchange like this one, that
means **SIPSorcery requires real per-candidate trickle ICE signaling and does not support the
"gather everything, then send one final SDP blob" pattern** that browsers support natively and
that the spec's own crude file-drop signaling instruction models. Any Phase 3 signaling design
that assumes a single final offer/answer exchange will silently produce an unusable SIPSorcery
offer with no relay candidates at all, and nothing about the failure (`VideoIncompatible`-free,
ICE just sits at `new`/`checking`→`failed`) points directly at "the offer has no candidates."
This cost the most diagnosis time in the whole spike.

**What this spike did instead (throwaway only, do not carry forward):** manually collected
`onicecandidate` events into a list and spliced `a=candidate` lines into the SDP text before
writing it out — and specifically, spliced them under the **first** `m=` section (video,
`mid:0`), not appended at the end of the file, since a first attempt that appended at the end
silently landed the candidate under the last `m=` section (`m=application`, the data channel)
instead, because this offer is BUNDLEd and Chrome expects the candidate on the bundle's first
(tagged) m-line. This splice-after-gathering approach is a hack that happens to work for a
same-process file-drop spike; it does not scale to a real signaling channel (WebSocket/relay
server) where the far side needs candidates as they arrive, not after an arbitrary gather-timeout.

**Phase 4 direction:** implement genuine trickle ICE in the real signaling layer — send each
`onicecandidate` event as its own signaling message the moment it fires, on both sides. SIPSorcery
already emits these events correctly; the fix is entirely on the signaling-transport side. This is
also the *faster* choice, not just the more correct one: real trickle lets ICE checks start before
gathering finishes, improving time-to-first-frame versus waiting for a full gather. The manual
splice workaround built for this spike should **not** be carried into Phase 3 — it was a
same-process expedient for a throwaway spike, not a design worth replicating.

**Update — macOS re-test with the fixed delay removed (resolved):** the Windows CI addendum (see
"Windows — partial" below) showed SIPSorcery embedding a single gathered *host* candidate into
`localDescription.sdp` on its own, the opposite of what's described above, and raised a real
question: was the original macOS result above a fixed-delay artifact (2500ms not being long enough
for TURN allocation), or a genuine difference tied to candidate type? This was re-tested on macOS
against the same relay/TURN setup, with two changes: (1) the fixed `Task.Delay(2500)` was replaced
with a real wait on `onicegatheringstatechange` reaching `RTCIceGatheringState.complete`, and (2)
the manual splice was disabled entirely so nothing could mask the native SDP.

Result: **not a harness bug.** ICE gathering completed in **30ms** — nowhere close to the 2500ms
the original fixed delay allowed — and the offer still had **zero `a=candidate` lines**, even
though `onicecandidate` had already fired with a `typ relay` candidate. So this is **genuine
SIPSorcery behavior, and it is not timing-dependent either**: the variable isn't how long you wait,
it's **candidate type**. SIPSorcery merges gathered **host** candidates into `localDescription.sdp`
on its own (per the Windows addendum) but does **not** merge **relay** (TURN) candidates, no matter
how long gathering is given to finish. The "never merges" framing in the finding above is therefore
wrong as a blanket statement — replace it with: *SIPSorcery does not merge relay/TURN candidates
into `localDescription.sdp`; host candidates are merged natively.*

The end-to-end check confirms the practical consequence: with the splice disabled, the .NET side's
ICE state went `checking` → `failed`, and Chrome (driven via `drive.mjs`, unmodified `index.html`)
never left `ice: new` / `conn: new` for the full run — it received an offer with no candidates and
had nothing to check against. No connection, no workaround. The manual splice this spike built is
still required for any relay-based (TURN) blob-exchange signaling; it is not required for
loopback/host-only signaling.

**Phase 3 impact:** remove the "SIPSorcery requires trickle ICE for all candidate types" framing —
it doesn't; host candidates are fine without it. What actually needs trickle ICE (or an
equivalent post-gathering append step) is specifically **relay/TURN candidates**, which is exactly
the case a real deployment cares about (LAN-only host candidates won't traverse NAT). The durable
lesson from this whole investigation stands regardless of the candidate-type finding above: **never
read `localDescription` after a fixed delay — wait for the real gathering-complete signal (or do
true per-candidate trickle) before treating an offer/answer as final.** A fixed delay is what made
this finding look inconclusive in the first place; it should never appear in Phase 3 signaling code.

### 5. coturn default-denies loopback/cross-interface peers (environment, not SIPSorcery)

Even after fix #4, connections still failed with `CREATE_PERMISSION processed, error 403:
Forbidden IP` in coturn's log. Two stacked causes, both artifacts of testing everything on one
machine:

- coturn refuses `CreatePermission` for loopback peer addresses by default (anti-SSRF guard) —
  needs `--allow-loopback-peers`.
- The spike's coturn config still had `listening-ip=0.0.0.0`/`relay-ip=0.0.0.0` left over from an
  earlier Docker-based attempt, so coturn bound relay sockets across *every* local interface,
  including a live iPhone-tethering interface (`172.20.10.2`). An allocation that landed on that
  non-loopback interface then correctly refused to relay to a `127.0.0.1` peer, regardless of the
  loopback-peers flag — a legitimate cross-interface security check, just tripped by leftover
  config. Fixed by pinning `listening-ip`/`relay-ip` to `127.0.0.1` explicitly.

This is purely a same-machine testing artifact and will not recur once client and TURN server are
on different real hosts, but it consumed real diagnosis time and is worth flagging: **local
same-machine spikes of TURN relay behavior need care in coturn's interface binding, or failures
that look like ICE/codec bugs are actually just coturn's security defaults.**

### Also tried and ruled out

- Docker Desktop's coturn (the original setup) never got a connection through at all — its
  gVisor-based virtual network stack rewrites the apparent source IP of loopback-destined traffic
  (seen in coturn's logs as `172.66.147.243` instead of `127.0.0.1`), and TURN allocations made it
  through the control channel but relayed zero bytes. Rather than debug Docker Desktop's network
  virtualization further, switched to native Homebrew coturn, which is what actually got the
  spike working. **Not a finding about SIPSorcery or Phase 4** — just a note that Docker-hosted
  local TURN testing on macOS is unreliable for this kind of thing.
- Manually patching the emitted relay candidates' `raddr`/`rport` (both SIPSorcery's and Chrome's
  own relay candidates report `raddr 0.0.0.0 rport 0`, which looked suspicious) had no effect on
  the outcome — ruled out as the cause of the ICE-stuck-at-`new` symptom before finding the real
  cause (finding #4).

## Measurements

All measurements are macOS only (Apple Silicon, this machine). **Windows: not tested, no
Windows machine was available in this environment — do not extrapolate these numbers to
Windows.**

| Metric | Result | How measured |
|---|---|---|
| CPU during 1080p encode (macOS) | ~5.5–8% of total (8-core) process CPU, i.e. roughly 0.5 of one core, **at an achieved encode framerate of ~24.3fps** (not the requested 30fps — see below) | `Process.TotalProcessorTime` delta / wall-clock delta / `Environment.ProcessorCount`, sampled every 1s in-process |
| Achieved encode framerate | **~24.3fps**, against a requested ~30fps (33ms frame-generation delay). 1461 frames encoded over 60.0s wall-clock in a dedicated run. The CPU number above is *conditioned on this framerate* — it is not a 30fps number. The gap is most likely encode-loop overhead (synthetic frame generation + `EncodeVideo` call + `SendVideo` all serialized in one loop, no pipelining) rather than a VideoToolbox throughput ceiling, but this spike didn't isolate which | `frameNo` count / `Stopwatch` elapsed at run completion, sender side |
| Encoder used | Hardware — VideoToolbox (`AV_HWDEVICE_TYPE_VIDEOTOOLBOX`) constructed and used successfully; `usingHardware=True` logged every run | Constructor success + low CPU number consistent with hardware offload (a software x264 encode of 1080p would cost far more than one core at ~30fps) |
| Memory over session | Flat, ~105–160MB RSS, no sustained upward trend observed in a 20s connected run | `Process.WorkingSet64`, sampled every 1s; full 10-minute run below |
| Connection setup time | ~0.5s from second peer's TURN `ALLOCATE` to `CHANNEL_BIND` success (data-plane ready); ~11s wall-clock from first peer's `ALLOCATE` to second peer's, but that gap is dominated by Playwright/Chrome cold-launch overhead in this test harness, not by SIPSorcery/ICE/DTLS negotiation itself | coturn log timestamps, both peers' `ALLOCATE`/`CREATE_PERMISSION`/`CHANNEL_BIND` lines |
| Data channel under load | Reliable, continuous — 500ms-interval messages both directions, zero drops observed, for the full duration of every successful connected run (20s and 10-minute runs) | Message sequence numbers logged on both sides; no gaps found |
| Latency (encode→relay→jitter-buffer→decode) | **Avg jitter-buffer delay: ~5.6ms. Avg decode time: ~1.6ms. Avg RTT (candidate pair): ~0.7ms.** Measured via the browser's `getStats()` rather than pixel-readback of a decoded frame (see note below). **Excludes capture and on-screen display/render time** — this is decode-pipeline latency only, from the point RTP packets arrive to the point a frame is handed to the video element. **Also excludes real network transit** — both peers and the TURN relay run on `127.0.0.1` on one machine, so the ~0.7ms RTT reflects loopback + TURN relay hop, not a real network path; a WAN deployment's RTT and jitter-buffer depth will differ substantially. 60 one-second samples over a 60s run, 1436 frames decoded (`inbound-rtp.framesDecoded` delta), consistent with the ~24.3fps achieved encode rate above. |
| 10-minute stability run | See below | `runSeconds=600`, full CPU/mem/data-channel/ICE-state log kept for the entire run |

### 10-minute stability run

`runSeconds=600`, same relay-only/loopback setup as above, full CPU/mem/data-channel/ICE-state
log kept for the entire run.

- **Errors/disconnects/freezes/degradation: zero.** Grepping both peers' full logs for
  error/failed/disconnect/closed/freeze (excluding the routine per-second sample lines) returned
  no matches on either side.
- **Connection state: stayed `connected` throughout.** Spot-checked via periodic web-driver
  samples (e.g. `{"ice":"connected","conn":"connected"}` at t=490s and t=495s) with no observed
  drop at any sampled point across the full 600s.
- **CPU:** avg 6.2%, min 3.4%, max 8.5% of total (8-core-normalized) process CPU, n=599 one-second
  samples. Consistent with the shorter run's ~5.5–8% figure — no upward drift over 10 minutes.
- **Memory:** avg 105.6MB RSS, min 85MB, max 150MB, n=599 samples. Flat over the full run — no
  sustained upward trend, i.e. no leak observed at this timescale.
- **Data channel:** 1196 messages delivered in each direction (500ms send interval on both sides),
  matching counts on both peers — no drops across the full 10 minutes.
- **Relay traffic at the TURN server, full duration:** coturn's own log
  (`native-run/coturn-native.log`) shows both sessions' periodic usage reports continuing at a
  steady ~6s cadence from the first `ALLOCATE` at `07:08:17` through the last usage report at
  `07:18:18` — i.e. the relay carried traffic continuously for the entire ~10-minute window, not
  just at the start. No `Forbidden IP`, `error`, or permission-denial lines appear anywhere in that
  window. This corroborates the "no disconnects" finding above from the relay's own vantage point,
  not just the two peers' self-reported state.

## Windows — partial

**Scope: only "does it work at all" — DLL loading, version skew, whether findings #3/#4 reproduce,
negotiation, packaging size/licensing. None of this needs a GPU.** Run via a manually-triggered,
`workflow_dispatch`-only GitHub Actions workflow (`.github/workflows/spike-t8-windows.yml`) on
`windows-latest`, reusing `spikes/webrtc-desktop/` as-is except for two small cross-platform changes
to `Program.cs`: the FFmpeg lib path became an env var (`SPIKE_FFMPEG_LIB_PATH`, still defaulting to
the original Homebrew path on macOS), and the VideoToolbox hardware-encoder attempt is skipped
outright on non-macOS hosts rather than attempted-then-caught, since `windows-latest` has no GPU at
all. `ci.yml` was not touched.

**`windows-latest` has no hardware encoder and falls back to software x264 — any CPU number this
run produced would be meaningless as an answer to "what does hardware encode cost," and none was
reported for that purpose.** That question is still fully open; see the narrowed pending section
below.

- **FFmpeg.AutoGen loading and version:** Loaded cleanly. Two Windows FFmpeg builds were downloaded
  from BtbN/FFmpeg-Builds' `latest` release (`gpl-shared` and `lgpl-shared`, both
  `N-126277-ga8c7afa7d7-20260826`, `libavcodec 63.8.101`) — a clearly GPL/LGPL-labeled source,
  unlike gyan.dev's builds, chosen specifically so the licensing question below has an unambiguous
  answer. Windows does **not** appear to have macOS's "FFmpeg 8.x is unobtainable" problem (finding
  #1) — BtbN publishes current builds routinely; the constraint on macOS was Homebrew-specific.
- **Encoders enumerable:** As expected, only software encoders actually ran (`libx264` in the GPL
  build; `h264_mf`/Media Foundation, `h264_qsv`, `h264_nvenc`, `h264_amf` all present in both
  variants but **not exercised** — no GPU on this runner to back them). The surprising result: `ffmpeg
  -hwaccels` and `-encoders` show `qsv` (Intel Quick Sync via `libvpl`/oneVPL) compiled into **both**
  the GPL and LGPL Windows builds out of the box, alongside `d3d11va`/`d3d12va`/`nvenc`/`amf`/`vaapi`.
  This means the Phase 4 "does the minimal custom build need Quick Sync compiled in specially"
  question is likely already answered **no** — it's already there in a stock BtbN build — pending
  confirmation that the custom minimal build (LGPL-only, codecs stripped) also carries `--enable-libvpl`
  by default rather than needing to add it explicitly.
- **Finding #3 reproduction: fixed, not broken.** The explicit fmtp construction
  (`level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f`) that fixes finding #3
  on macOS was carried over unchanged in `Program.cs`, and the resulting Windows-built offer was
  accepted by real Chrome (headless `channel: 'chrome'` via Playwright, same as macOS): Chrome
  answered with a matching H.264 fmtp line and `ontrack video` fired.
- **Finding #4: result in, and it complicates the original claim rather than confirming it.** The
  first Windows run used relay-only policy with no TURN reachable (see below) — zero candidates
  gathered, zero to test. A follow-up run (`SPIKE_ICE_TRANSPORT_POLICY=all`, no relay-only
  restriction, no TURN server needed — host candidates gather with no TURN server involved, which
  the original relay-only test on this platform wrongly conflated with the merge question) removed
  that ambiguity: `onicecandidate` fired with a single host candidate
  (`10.1.0.153:56444 typ host`), and the written offer **does** contain that candidate —
  `a=candidate` appears **twice**, once positioned natively right after `a=setup:actpass` (SDP's
  conventional candidate position, before `a=mid:0`), and a second identical copy appended by this
  spike's own manual-splice workaround, which fires unconditionally and had no way to know
  SIPSorcery had already embedded it. The result is a malformed offer with a duplicate candidate
  line in the same `m=` section.

  This means finding #4's original framing — "SIPSorcery's `localDescription` never gains gathered
  candidates" — does not hold as a blanket claim. It held on macOS with TURN-relay candidates; it
  did not hold here, where a single host candidate was embedded natively. The leading hypothesis at
  the time this Windows run was captured was **timing** — that the macOS read happened after a fixed
  2500ms delay rather than a real gathering-complete wait, and that TURN allocation might simply
  take longer than that. **This was re-tested on macOS directly (see finding #4 below) and ruled
  out**: gathering completed in 30ms with the fixed delay removed and the manual splice fully
  disabled, and the offer still had zero candidates. The real variable is **candidate type, not
  timing** — SIPSorcery merges host candidates into `localDescription.sdp` on its own but does not
  merge relay/TURN candidates, regardless of how long gathering is given. **Action for Phase 3:**
  don't manually splice candidates unconditionally; check whether candidates are already present
  before splicing (host candidates will already be there; relay candidates won't be), and — as a
  general signaling-layer rule independent of this finding — always wait for ICE-gathering-complete
  before reading `localDescription`, never a fixed delay, since a fixed delay is exactly what made
  this finding look ambiguous in the first place.
- **TURN/ICE connectivity: not established, and here's exactly why.** The spike's coturn setup is a
  Docker container (`spikes/webrtc-desktop/coturn/docker-compose.yml`), and `docker compose up`
  failed immediately: `no matching manifest for windows(10.0.26100)/amd64` — coturn has no Windows
  container image, and GitHub's `windows-latest` runner's Docker only runs Windows containers, not
  Linux ones. With `iceTransportPolicy: 'relay'` hardcoded on both sides and no TURN server reachable,
  ICE never left the `new` state for either peer during the 30-second observation window. This is a
  CI-environment limitation, not a Windows-the-OS limitation — a real Windows desktop build wouldn't
  run coturn locally anyway — but it means **end-to-end connection establishment was not exercised
  on Windows in this run**, only SDP-level offer/answer negotiation (which does not require ICE).
- **DLL bundling and licensing:** GPL build: 7 DLLs, ~164MB total (`avcodec-63.dll` alone is
  ~94MB). LGPL build: same 7 DLLs, ~131MB total (`avcodec-63.dll` ~68MB — smaller by exactly the
  `libx264`/`libx265` removal). Confirmed via `-buildconf`: the LGPL variant is built with
  `--disable-libx264 --disable-libx265` (alongside `--disable-avisynth`, `--disable-libdavs2`, and a
  few other GPL-only pieces) while otherwise carrying the same feature set, including every
  hardware-vendor encoder path. **The GPL-x264/x265 licensing problem found on macOS (see "Also
  checked" below) is not macOS-specific — it reproduces identically on Windows**, confirming the
  LGPL-minimal-build requirement is a genuine cross-platform Phase 4 requirement, not something tied
  to Homebrew's packaging choices specifically. Each build ships a single top-level `LICENSE.txt`,
  not one per DLL — its exact text wasn't captured in this run's artifacts, but BtbN's variant
  naming (`gpl-shared` vs `lgpl-shared`) is itself the authoritative signal for which license regime
  applies to a given download, which is why that build source was chosen over gyan.dev's.
- **What could not run headless in CI, stated plainly:** coturn (no Windows/Linux-container path on
  this runner, as above). Everything else — dotnet build, FFmpeg download/inspection/enumeration,
  the dotnet peer itself, and headless Chrome via Playwright — ran without workaround.
- Full logs and raw findings (`ffmpeg -version`/`-buildconf`/`-encoders`/`-hwaccels` output, DLL
  listings, offer/answer SDP, Playwright driver log) are in the `t8-windows-partial-findings`
  artifact on run `33045044593`.

## Item 2 — Windows: pending (hardware-encode cost only)

**Everything about "does it work at all" is now answered above. What remains open is narrower:
what does real Quick Sync hardware encode actually cost on Windows.** No GPU-backed run has
happened yet; the CPU/memory numbers below still need real Intel Quick Sync (or NVENC/AMF) hardware,
not a cloud VM — a VM with no hardware encoder would fall back to software x264 and answer the
wrong question, exactly as `windows-latest` did above.

- Which hardware encoder is actually reachable through `SIPSorceryMedia.FFmpeg` on real hardware:
  QSV, NVENC, AMF, or D3D11VA — naming the one used and the GPU it ran on. Given the CI finding above
  that `libvpl`/QSV is already compiled into the stock Windows FFmpeg build, this is now mostly a
  question of whether `SIPSorceryMedia.FFmpeg`'s encoder construction actually reaches it correctly,
  not whether the codec library exists.
- CPU during 1080p encode at a stated achieved framerate (not just the requested one — see the
  macOS framerate note above for why that distinction matters).
- Memory over a 10-minute run.
- Real end-to-end connection setup time and data channel behavior through an actual reachable TURN
  server (coturn on Windows itself, or a Linux box acting as the TURN server for a Windows dotnet
  peer) — the Docker/Linux-container gap found in CI above means this still needs to be solved for a
  real Windows run, e.g. by running coturn on a separate Linux host/VM rather than via
  Docker-on-Windows.

**Until this is filled in, the verdict at the top of this document should be read as: SDP-level
negotiation and packaging/licensing confirmed on Windows via CI, hardware-encode cost and full
end-to-end connectivity still open.**

## Also checked

- **Hardware vs software H.264, macOS:** Hardware (VideoToolbox) is reachable through
  `SIPSorceryMedia.FFmpeg`'s `FFmpegVideoEncoder(opts, AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX)`
  constructor and was used successfully in every run (`usingHardware=True`), no fallback to
  software ever triggered on this machine.
- **Codec negotiation:** Did not agree cleanly out of the box — needed the explicit fmtp fix
  (finding #3). Once fixed, negotiation itself was clean and fast.
- **FFmpeg binary/packaging cost:** The five core libav*/libsw* dylibs actually loaded total
  **~16MB** (`libavcodec` alone is 9.3MB). But this Homebrew build is dynamically linked against
  roughly a dozen more third-party codec libraries not needed for this use case — libvpx, dav1d,
  libmp3lame, libopus, SvtAv1Enc, libx264, libx265, liblzma — none of which H.264-only screen
  relay needs. Shipping the Homebrew build as-is means bundling and code-signing all of them (each
  currently only ad-hoc signed, not Developer-ID signed, which will not pass Gatekeeper/notarization
  as distributed). **Phase 4 should budget for building a minimal custom FFmpeg** (H.264 encode/decode +
  VideoToolbox only, codecs the product doesn't use stripped out) rather than shipping a full
  Homebrew-equivalent build — real size and signing-pipeline work, not a checkbox.
- **FFmpeg licensing: this is a legal requirement, not only a size optimization.** Homebrew's
  ffmpeg build is compiled `--enable-gpl` and links `libx264`/`libx265`, both GPL-licensed. Linking
  GPL code into a closed-source commercial product either obligates GPL compliance (source
  disclosure) for the whole linked binary or is a license violation — this is not something a
  smaller custom build merely improves, it is something a **minimal LGPL-only build is required
  to fix**: `--disable-gpl`, hardware encode via VideoToolbox (macOS) / whatever Windows finds
  viable (see Item 2), and GPL codecs (x264, x265) stripped entirely, keeping only LGPL-compatible
  codecs and the hardware encoder paths the product actually uses. This should be a hard
  requirement in the Phase 4 FFmpeg build spec, not a stretch goal.
- **macOS signing/entitlement friction found:** none beyond the packaging point above — no Screen
  Recording permission was exercised (synthetic frames were substituted, see above), so that
  specific friction point is **untested**, not confirmed clean. It should be spot-checked before
  Phase 4 relies on it. **Note:** Phase 4 should feed the product's existing `IScreenCapture`
  implementations into `FFmpegVideoEncoder` directly, rather than using SIPSorcery's own capture
  sources (e.g. `FFmpegScreenSource`, which is what this spike avoided by substituting synthetic
  frames). The product's `IScreenCapture` code already exercises the macOS Screen Recording
  permission flow in production, so reusing it removes this untested-permission concern rather
  than requiring a fresh spot-check of SIPSorcery's own capture path.

## What would change the Phase 4 estimate in ADR-001

1. **Budget explicit engineering time to implement real trickle ICE in the signaling layer**
   (finding #4). A real deployment relies on NAT traversal, which means relay/TURN candidates —
   and SIPSorcery does not merge those into `localDescription.sdp` no matter how long gathering is
   given (confirmed genuine, not a timing artifact; host candidates alone are fine without this, but
   host-only won't traverse NAT in the field). So a "gather then send one blob" design will not work
   for the case that matters, and this is not optional Phase 3 scope. This is not a one-line fix like
   the fmtp issue: it means sending each `onicecandidate` event as its own signaling message on both
   sides, plus a test that would have caught this (an assertion that ICE actually reaches
   `connected`, not just that an SDP exchange completed) so it doesn't regress silently. The upside:
   real trickle is also faster to first frame than waiting for full gathering, so this isn't pure
   added cost.
2. **Budget for a custom minimal FFmpeg build and its per-platform signing pipeline**, not "bundle
   whatever Homebrew has." The gap between "works on my dev machine with brew" and "ships signed
   and notarized" is real, unbudgeted work.
3. **Pin FFmpeg.AutoGen / native FFmpeg versions explicitly and test that pin**, rather than
   relying on whatever's installed — the 8.x-is-unavailable problem found here will hit every
   fresh dev/build machine the same way until it's pinned and vendored.
4. **The core hypothesis holds:** hardware H.264 via VideoToolbox works, CPU cost while encoding
   is low (~0.5 core), and the connection is genuinely reliable once established — nothing found
   here suggests SIPSorcery can't carry the product's real workload. The risk this spike surfaces
   is integration/packaging friction, not a fundamental capability gap.
5. **Windows is a real open question.** Everything above is macOS-only. Given the packaging and
   native-library findings here, Windows should get its own short spike pass before Phase 4
   estimates are finalized — FFmpeg binary distribution and hardware encoder access
   (`AV_HWDEVICE_TYPE_D3D11VA`/`DXVA2` equivalents) are plausibly a different set of problems
   entirely on that platform.
