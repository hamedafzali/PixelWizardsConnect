# Phase 3 Plan — Signaling and relay

**Status: draft for review.** Nothing past P3.0 has started.

ADR-001 scope: "Go router to persistent WebSocket signaling; SDP/ICE exchange; TURN credential
issuance; coturn deployment." Exit gate: "Two peers exchange ICE candidates across carriers."
Estimate: 2–3 weeks solo.

Phase 3 builds the path two WebRTC peers use to find each other. It does **not** carry a
session over WebRTC. That is Phase 4 (SIPSorcery `ISessionTransport`, H.264). The desktop app's
behaviour does not change in this phase.

---

## Where things stand

| Piece | Today |
|---|---|
| Router | `router-server/main.go`, Go 1.22, standard library only. HTTP `/register`, `/connect`, `/health`. In-memory `hosts`/`hostsByCode` maps under one mutex. 6-character one-time codes, 30-minute TTL, 10 requests/min per IP, `TRUSTED_PROXY_CIDRS` for `X-Forwarded-For`. The router generates `sessionSecret` and gives it to both sides. |
| Client side | `PixelWizard.Signaling` (P3.0, 47b98e0) holds `NetworkDiscovery` (UDP LAN discovery) and `RouterHttpClient` (implements `IRouterClient` in Core). |
| coturn | Spike only: `spikes/webrtc-desktop/coturn/turnserver.conf`, loopback-bound, static `lt-cred-mech` user. Nothing deployable. |
| WebRTC peer | Spike only: `spikes/webrtc-desktop/dotnet-peer`, SIPSorcery 10.0.16. |

### Spike findings this plan must honour (ADR-001, gate outcome)

1. SIPSorcery puts host candidates in `localDescription.sdp` but **not relay candidates**.
   Signaling must **trickle** candidates from `onicecandidate`. It must not rely on one final SDP.
2. Never read `localDescription` after a fixed delay. Wait for gathering to complete.
3. Do not carry the spike's SDP-splice workaround forward. Trickle makes it unnecessary.
4. Single-platform conclusions from the spike need checking on the other OS.

---

## Decisions for you before work starts

**D1 — The exit gate as written is too weak.** "Exchange ICE candidates across carriers" is met
once the router is reachable from two networks, because exchanging candidates only means
relaying JSON. Proposed gate:

> Two peers on two different mobile carriers, signaled only through the deployed router, reach
> ICE `connected` and pass a data-channel round trip. This happens twice:
> (a) with the normal transport policy, recording the selected candidate pair type;
> (b) with `iceTransportPolicy = relay`, with the allocation confirmed in coturn's log.

Run (b) proves TURN works even when direct traversal would have succeeded. It is the same check
the spike used ("confirmed at coturn, not inferred from ICE state").

**D2 — Which peers run the gate?** Recommended: a throwaway-grade console tool,
`tools/PixelWizard.IceProbe`, built on SIPSorcery plus the real `PixelWizard.Signaling` client.
It opens a data channel and nothing else, with no capture and no codecs. Phase 4 then reuses the
signaling client and the trickle handling, not the probe. The alternative, a browser page served
by the router, tests libwebrtc instead of SIPSorcery, which is the riskier side.

**D3 — A public host is needed.** The gate needs the router and coturn on a machine with a public
IP: a VPS, TLS via the existing Caddy setup, UDP 3478 and a relay port range open, and
TCP/TLS 443 for networks that block UDP. It also needs two devices on two different carriers
(for example, two phones tethering two laptops). I cannot provide these. Which host?

**D4 — The HTTP endpoints stay.** `/register` and `/connect` are left unchanged, and the app
keeps using them for TCP sessions. WebSocket signaling is added alongside them at `/signal`. The
HTTP route is retired in Phase 4, when router-mediated sessions move to WebRTC. Reason: the app
builds and runs at every boundary, and nothing in Phase 3 can carry a session yet.

**D5 — BACKLOG rows tagged Phase 3 that I propose not to do here:**

- *Router conveys the TCP certificate fingerprint out-of-band.* Superseded. After Phase 4,
  router-mediated sessions are WebRTC, and the DTLS fingerprint already travels in the SDP over
  the authenticated signaling channel. Direct-IP LAN TCP never goes through the router, so the
  row would protect a path that Phase 4 removes. Proposal: close it as superseded at Phase 4.
- *"Trust new certificate" re-pins plain TOFU.* This is a `TcpTransport` API change and has
  nothing to do with signaling. TCP stays for LAN, so the issue stays real. Proposal: re-tag it
  Phase 4, alongside the transport work.
- *Skia vs System.Drawing detector duplicate.* T5b recorded why the Windows one stays.
  Re-tag to Phase 4: the duplicate goes away when the Windows host feeds `FFmpegVideoEncoder`.

**D6 — The router's first dependency.** The router uses only the standard library today. The
WebSocket endpoint needs `github.com/coder/websocket` (formerly nhooyr.io/websocket:
context-aware, no transitive dependencies) or `gorilla/websocket`. Recommended: coder/websocket,
pinned to an exact version.

---

## Sub-tasks

Each line is one or more commits, and each sub-task is pushed, with CI counts reported, before the
next one starts. "Gate" is what must be true before moving on.

### P3.0 — Signaling home ✅ 47b98e0

`NetworkDiscovery` and `RouterHttpClient` moved from `Transport.Tcp` to the new
`PixelWizard.Signaling` project. CI green: Windows 337 core + 65 client, 13 System.Drawing passed;
macOS 337 + 65, 13 skipped.

### P3.1 — Fix the router race before adding concurrency

BACKLOG: `TestRegistrationRacingCleanup` failed once under `-race`. From reading the code, the
likely cause is a test problem, not a production one:

- `TestCleanupExpiredHosts_EventuallyRemovesExpiredCode` (main_test.go:512) starts
  `go cleanupExpiredHosts()` and never stops it.
- That goroutine runs every 5 ms for the rest of the test binary, reading `codeTTL` and
  `nowFunc`.
- Every later test's `resetGlobalState()` writes those globals without the mutex, and so does
  `TestRegistrationRacingCleanup`, which also starts a second goroutine that is never stopped.
- In production the globals are set once, before the goroutine starts.

This is **not confirmed**: there is no local Go or Docker in this session to reproduce it.

- Make `cleanupExpiredHosts` stoppable with a `context.Context`. `main` passes a background
  context; tests cancel theirs with `t.Cleanup`.
- Do not add locking to the config globals just to quiet the detector.
- Gate:
  - the go job runs `go test -race -count=50 ./...` once and it is green;
  - the leaked goroutine is confirmed as the cause by putting the leak back and seeing the race
    on CI;
  - the BACKLOG row is closed with the finding.

### P3.2 — Signaling protocol spec, before code

New `docs/SIGNALING.md`, versioned like `PROTOCOL.md`. It covers:

- **Transport:** WSS at `/signal`. JSON text frames carrying `{"v":1,"type":...}`. Unknown
  `type` values are ignored, never fatal, for the same reason as the Phase 1 envelope.
- **Roles and pairing:**
  - The host sends `register` and gets back `registered {code}`. TURN credentials go out at
    pairing time instead, in `joined` and `peer-joined` (refined in SIGNALING.md).
  - The viewer sends `join {code}` and gets back `joined {turn}`.
  - The host is told `peer-joined`.
  - The code stays one-time use, as it is today.
  - One host and one viewer per room. Rooms for more people (an expert plus a field worker)
    are Phase 5/6 and are kept out on purpose.
- **Relayed opaquely between the paired peers:** `offer`, `answer`, `candidate` (trickled),
  `end-of-candidates`, `bye`. The router checks size and type, never content.
- **Lifecycle:**
  - ping/pong keepalive;
  - a room ends when either socket closes, and the other side is told `peer-left`;
  - a host may re-register on a new socket within a grace window, using a resume token, so a
    network change does not lose the code (Phase 7 depends on this).
- **Limits:** maximum frame size, messages per second per socket, sockets per IP, and the
  existing per-IP rate limit on `register` and `join`. These are written down as numbers.
- **Errors:** a closed set of error codes (`code-unknown`, `code-used`, `rate-limited`,
  `too-large`, `bad-message`) sent as an `error` message before close.
- **Security notes:**
  - The router can read and alter SDP, so it is trusted to deliver the right DTLS fingerprint.
    That is the same trust it has today over `sessionSecret`, and it is stated plainly.
  - An `Origin` allow-list is reserved for the Phase 5 browser viewer.
- **Golden files:** example messages in `router-server/testdata/signaling/*.json`. Both the Go
  tests and the .NET tests read them, which is the contract between the two languages.

Gate: you review SIGNALING.md. No code until it is accepted.

### P3.3 — Router `/signal` endpoint

- `router-server/signal.go`. It shares the code space with the HTTP routes, so a code is unique
  across both. Its room state is separate.
- Go tests use `httptest.Server` with two real WebSocket clients and cover:
  - pairing;
  - relay in both directions;
  - message order is kept per direction;
  - unknown type ignored;
  - oversize frame rejected;
  - rate limits;
  - a code works only once;
  - `peer-left` on close;
  - resume within the window and rejection after it;
  - golden-file round trip;
  - a race test with many rooms at once under `-race`.
- HTTP handler tests are unchanged and must still pass with the same count.
- Gate: go job green with counts. The existing tests' count does not drop.

### P3.4 — TURN credential issuance

- coturn's REST-API scheme (`use-auth-secret`):
  - username = `<expiry-unix>:<random-id>`;
  - password = base64(HMAC-SHA1(secret, username)).
- The router is configured with `TURN_URLS`, `TURN_SECRET` and `TURN_CREDENTIAL_TTL`.
- Credentials go out in `peer-joined` and `joined` as `{urls, username, credential}`. They are
  issued per socket and never logged.
- Tests:
  - a fixed-input vector checks the HMAC;
  - expiry is checked through the `nowFunc` seam;
  - the secret never appears in any response or log line.
- If `TURN_SECRET` is unset, the `turn` field is omitted, which gives STUN/host candidates only.
  It never falls back to a static credential.
- Gate: go job green. P3.5 confirms that coturn accepts the credentials.

### P3.5 — coturn deployment

- `router-server/coturn/turnserver.conf` is a production template:
  - `use-auth-secret`, `static-auth-secret` from the environment, `realm`;
  - `external-ip` / `relay-ip` as parameters, with a fixed `min-port`/`max-port` range;
  - `fingerprint`, `no-cli`, `no-multicast-peers`;
  - `denied-peer-ip` for loopback, link-local and RFC 1918 ranges, so the relay cannot be used
    to reach the server's own network;
  - TLS on 443/5349, reusing Caddy's certificate.
- A `coturn` service in `docker-compose.https.yml`, with a README section covering ports,
  firewall and secret rotation.
- Carried from spike finding #5: coturn's loopback and interface binding behaviour. The template
  comments say which address each setting must hold on a single-NIC VPS.
- CI: a new job on ubuntu-latest starts coturn in Docker with a test secret, then runs
  `turnutils_uclient`:
  - an allocation with router-issued credentials succeeds;
  - an allocation with expired credentials fails;
  - a relay to 10.0.0.0/8 is refused.
- Gate: that job is green, and its log shows all three results.

### P3.6 — .NET signaling client

- `PixelWizard.Signaling/SignalingClient.cs` over the standard library's `ClientWebSocket`, with
  no new package.
- Typed messages, reconnect with the resume token, and incoming candidates exposed as an async
  stream.
- No knowledge of SIPSorcery: it moves strings and candidates, not peer connections.
- Tests in `PixelWizard.Tests`:
  - against an in-process `HttpListener` WebSocket fake;
  - the golden files from P3.2 parse and re-serialize byte-for-byte equal.
- No change to `IRouterClient`; the app does not use this client yet.
- Gate: dotnet jobs green on both OSes with counts. Client test count unchanged.

### P3.7 — ICE probe

- `tools/PixelWizard.IceProbe`, a console app referencing SIPSorcery 10.0.16 (the spike's tested
  version) and `PixelWizard.Signaling`. Modes: `--host` and `--join <code>`.
- It applies the spike findings:
  - candidates trickled from `onicecandidate` the moment they arrive;
  - `end-of-candidates` sent only on gathering-complete;
  - no fixed delays, no SDP splicing.
- It prints:
  - the type of every gathered candidate;
  - the selected pair's type (host, srflx, relay);
  - the data-channel round-trip time.
- `--relay-only` sets `iceTransportPolicy = relay`.
- It is not in the app solution's shipped outputs. Whether it builds as part of the solution in
  CI is decided at implementation time; the default is yes.
- CI: on each dotnet OS, two probes run through a router started in the same job and pair over
  loopback. Relay is not tested here; P3.5 covers that.
- Gate: probe pairing green on Windows and macOS, so neither OS's conclusion stands alone
  (finding #4).

### P3.8 — Exit-gate run (needs D3)

- Deploy the router and coturn to the public host.
- Run the probes on two devices on two different carriers, (a) then (b), as defined in D1.
- Record in `docs/PHASE3-GATE.md`: carriers, OS, selected pair type, RTT, and the coturn log line
  showing the allocation.
- No user name, PC name or public IP of a personal device goes in the repo.
- If (a) selects relay on its own, record that; it is information about CGNAT, not a failure.

### P3.9 — Close

- Record the gate outcome in ADR-001's phase table.
- Apply the D5 decisions to BACKLOG.
- Update the router README for `/signal` and TURN.
- Leave the Phase 4 entry points written down: the signaling client API and how the probe's peer
  setup becomes the transport.

---

## What Phase 3 does not do

- No WebRTC session transport, no video, no FFmpeg. That is Phase 4.
- No change to the desktop app's UI or connection flow.
- No persistence or horizontal scaling for the router. One process, in memory, as today. A
  restart drops rooms; the resume token does not survive a restart, and that is acceptable.
- No browser viewer and no `Origin` enforcement beyond reserving it (Phase 5).
- No rooms with more than two peers.

## Risk

| Risk | Effect | Mitigation |
|---|---|---|
| D3 not available | Gate cannot run | P3.1–P3.7 do not need it; the gate run is last |
| Carrier blocks UDP | Run (a) fails | TURN over TLS on 443 in the P3.5 template; the probe reports which URL succeeded |
| SIPSorcery trickle behaviour differs from the spike | Probe never connects | Spike code is the reference; any new finding is reported, not worked around |
| Router dependency churn | Supply-chain surface | One dependency, exact version, `go.sum` committed |

## Separately: Phase 2 closure

Phase 2's code work is done (MainViewModel 224 lines; no `Dispatcher` code in Session). It is
recorded as closed after your T13.8 manual smoke test passes, including Mac-as-host screen
sharing and the pop-out window.
