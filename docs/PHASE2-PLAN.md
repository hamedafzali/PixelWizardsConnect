# Phase 2 Plan — Modularization

Phases 0 and 1 are on `main`. This is the execution plan for Phase 2
(`docs/ADR-001-architecture.md`): split `MainViewModel` and the rest of the
in-app code into the six-project shape, with `MainViewModel` reduced to a
thin per-mode adapter. Each task below (T1…T13) is scoped to land as one
commit or one short-lived branch, and is written to be handed to an
execution prompt without re-deriving the sequencing argument.

## Baseline correction (read this before using the ADR's numbers)

ADR-001 and STATUS_REPORT.md both cite `MainViewModel.cs` at 1,331 lines.
**It is 1,457 lines today** on `main` (Phase 1 added Hello/HelloAck/v1-detection
dispatch directly into `OnHostMessage`/`OnViewerMessage`, which is exactly the
code this phase needs to move). Two consequences:

- The "under 300 lines" exit gate in ADR-001 is restated below against the
  *current* file, not the stale baseline. Nothing about the target changes —
  the gap to close is just larger than documented.
- `MainViewModel` picked up 158 more lines of protocol-dispatch logic since
  the ADR was written, all of it wrapped in `Dispatcher.UIThread.Post`. There
  are **35 `Dispatcher.UIThread` call sites** in the file today, nearly all of
  them inside `OnHostMessage`/`OnViewerMessage`. This is the load-bearing fact
  for the characterization-test section below: the code this phase most needs
  to extract is also the code that grew the most since it was last measured.

This growth was the right trade, not a mistake: Phase 1's own hard rules
forbade restructuring `MainViewModel` during the protocol change, and mixing
the two would have made both unreviewable. But it is a real cost, not just a
number to correct — ADR-001's 4–6 week estimate for Phase 2 was made against
the smaller, 1,331-line target, before this growth happened. Any phase that
lands a protocol or feature change ahead of a scheduled refactor should
expect the same effect: the refactor's estimate is made against a moving
target, and the target moves in the direction that makes the refactor bigger,
not smaller, because new work lands in the file that already has the most
surface area to attach to. Budget Phase 2 against 1,457 lines, and expect
whatever change lands between now and T9 to add a few more.

## Target-shape corrections (say-so per the task brief)

ADR-001's table presents `PixelWizard.Transport.{Tcp,WebSocket}` and
`PixelWizard.Platform.*` as if the seams already exist. They don't:

- **No `PixelWizard.WebSocket` project exists.** `WebSocketHostServer` lives
  inside `PixelWizard.Transport` today, alongside `TcpTransport`. Splitting it
  out is a real extraction task (T7 below), not a rename.
- **No Mac platform project exists.** `MacScreenCapture`, `MacInputInjector`,
  `MacHostProvider`, `MacKeyMap` live in
  `avalonia/PixelWizard.AvaloniaClient/Platform/Mac/` — inside the client, not
  a sibling project. Windows and Linux platform code already lives in its own
  project (`PixelWizard.WindowsHost`, `PixelWizard.LinuxHost`); Mac is the odd
  one out and needs the same treatment (T11).
- **`PixelWizard.Session` as ADR-001 describes it doesn't exist at all.** This
  is the actual center of gravity of Phase 2 — `HostSession`/`ViewerSession`
  don't exist as objects today; `OnHostMessage`/`OnViewerMessage` dispatch
  live inline in `MainViewModel`. Everything else in the table is a
  comparatively mechanical file move; this is the one genuine design-and-build
  task, and it's why it's sequenced last among the extractions (T9), after
  everything that can be shaken loose independently already has been.

No other part of the target shape looks wrong. `PixelWizard.Protocol` and
`PixelWizard.Core`'s split (wire types vs. interfaces/domain models) is new
relative to today's single `PixelWizard.Core` project but is a clean,
independent extraction (T4) — nothing about it needs revision.

## Sequencing

```
T1  Characterization tests for MainViewModel dispatch (current shape)
T2  Extract PixelWizard.Protocol from PixelWizard.Core
T3  ReceiveLoop error-class split (backlog item 1)              [independent]
T4  Extract remaining PixelWizard.Core (interfaces + domain models)
T5  Extract PixelWizard.Media (capture loop, pacing, diffing, codecs)
T6  Extract PixelWizard.Transport.Tcp (rename/narrow existing Transport)
T7  Extract PixelWizard.Transport.WebSocket + Playwright browser smoke test
T8  WinForms removal + paired macOS CI assertion flip              [independent, same commit]
T9  PixelWizard.Session: HostSession/ViewerSession, zero Dispatcher refs
T10 Live end-to-end Hello-flow socket test (backlog item 4, unblocked by T9)
T11 Extract PixelWizard.Platform.Mac (parity with Windows/Linux hosts)
T12 Pin-mismatch recovery UI (backlog item 3)
T13 Split MainViewModel/XAML into thin per-mode views; delete the god object
```

Rationale for the order:

- **T1 before any extraction touches dispatch.** See the characterization
  section below — this is the one non-negotiable ordering constraint in the
  whole plan.
- **T2–T7 are the mechanical moves and are largely independent of each
  other and of T9.** `PixelWizard.Protocol`, `Core`, `Media`, and the two
  `Transport.*` projects are pure file moves plus namespace changes; the
  extraction-scores table in STATUS_REPORT §4 already rates their source
  components 3–5 out of 5 (safe to move today). They're sequenced T2→T7 by
  dependency only (Protocol has zero dependencies on other new projects, so
  nothing had to move before it; Core depends on Protocol's types; Media and
  Transport depend on Core's interfaces), not by risk. T3 and T8 are slotted in wherever convenient since they don't touch
  the extraction graph at all — shown at their natural backlog position, but
  either could move earlier or later without consequence.
- **T9 (Session) is deliberately last among the extractions**, not first.
  ADR-001's own dependency table would put Session in the middle, but every
  other extraction narrows what Session has to absorb: by the time T9 starts,
  `OnHostMessage`/`OnViewerMessage` only need to move protocol dispatch and
  connection lifecycle out of `MainViewModel` — the capture loop, the
  transport implementations, and the protocol types are already gone. Doing
  Session first would mean re-touching the same dispatch code twice.
- **T10 depends on T9** (needs drivable-without-UI session objects) — this is
  exactly the backlog row's own stated blocker, unchanged here.
- **T11 (Mac platform) and T12 (pin-mismatch UI) are independent of the
  Session work** and can run any time after T6 (T12 needs `TcpTransport` to
  already expose `ForgetPin` cleanly, which it does today) — placed near the
  end because they're UI-surface work, not restructuring, and there's no
  reason to interrupt the extraction sequence for them.
- **T13 (delete `MainViewModel`) is last by construction** — every prior task
  removes one more slice of it. It closes when nothing is left to remove.

## Intermediate milestone

Stopping anywhere in T2–T7 leaves the phase in its worst-looking honest
state: six new projects, a pile of moved files, and `MainViewModel` still
1,400+ lines and still the thing users would notice if it broke. That is real
progress — the extraction-scores table says these moves are safe precisely
because nothing about them is visible from outside — but it is not a
defensible pause point for a solo developer returning after a gap. There is
nothing to show for it except diffs, and "six projects, zero behavior change"
reads like a refactor that stalled, even when it didn't.

**The milestone is after T9 and T10.** At that point:

- `HostSession`/`ViewerSession` exist, dispatch has left `MainViewModel`
  entirely, and `Dispatcher.UIThread` no longer appears anywhere below the
  view-model layer — the one architectural property ADR-001 names explicitly
  ("zero `Dispatcher` references" in Session) is met.
- `MainViewModel`'s line count has dropped by whatever T9 removed, which is
  most of the 1,457 — a number worth reporting in T9's own report, not
  deferred to T13.
- T10's live socket test passes, which is external, checkable proof the new
  Session layer actually works end-to-end, not just that it compiles.

That is a shippable, explainable state even if T11–T13 never happen: the god
object's most dangerous logic is out, tested, and running. T11 (Mac parity)
and T12 (pin-mismatch UI) are independent surface work that can slot in
before or after this point without changing that. If the phase has to pause
anywhere, pause after T10, not after T7 — and if a gap is coming, do T3 and
T8 (both independent, both small, both already-known-good fixes) on the way
to T9 rather than after it, so there's a second, earlier proof of forward
motion before the big one lands.

Build-and-run gate: every task above ends with `dotnet build` succeeding for
all TFMs and the app launching in both host and viewer mode. T2–T9 are pure
moves plus adapter shims, so this is enforceable by construction — a task
that can't keep the app running is split until the parts that can are
separated from the part that can't (this is the same rule the risk section
below applies explicitly to T9 and T13).

## The characterization-test problem

STATUS_REPORT §4 rates `OnHostMessage`/`OnViewerMessage` dispatch at **1/5** —
the least safe-to-extract code in the repo — and it is also the largest
single extraction target (T9/T13). Phase 0 tested the protocol and the change
detectors; it built nothing for this code, because it's entangled with
`Dispatcher.UIThread` and can't be driven headlessly today.

**Approach: extract testability before extracting the code.** Concretely:

- **T1 writes characterization tests against the current shape**, not the
  target shape. It does this by adding a seam, not by finding a way to unit
  test `MainViewModel` as-is (that's not achievable without the seam — it's
  the entire problem). Concretely: `OnHostMessage`/`OnViewerMessage` are
  refactored *in place* (no project changes, no behavior changes) so that
  each `case` body calls a private pure method returning "what to do"
  (a state change + an optional UI action), and a single dispatcher-facing
  wrapper is the only thing that calls `Dispatcher.UIThread.Post`. This is
  the same pattern already established in Phase 1 for `HelloNegotiator.Evaluate`
  and `HelloCompatibility.LooksLikeV1Peer` — pure classification functions
  with a thin adapter around them — applied to the rest of dispatch instead of
  just the Hello path. T1 then writes tests against those pure methods,
  covering every `MessageType` case in both `OnHostMessage` and
  `OnViewerMessage`. This is real, if mechanical, work — expect it to be the
  second-largest task in the plan after T13 itself.
- **T2–T8 carry no regression risk from this problem** — they don't touch
  dispatch. Characterization coverage doesn't need to extend to them; they're
  covered by existing build/run verification and (for Media/Transport) the
  existing Phase-0 detector and protocol tests moving with the code.
- **T9 is where the extracted pure methods get a real home** (`HostSession`/
  `ViewerSession`), and it's covered by T1's tests directly — the pure methods
  don't change shape, only which class hosts them and how their outputs reach
  the UI (an event/callback instead of a direct `Dispatcher.Post`).
- **T13 is the highest-regression-risk task in the plan.** It's the one place
  where "the tests still pass" and "the feature still works" can diverge —
  moving XAML bindings and per-mode view model wiring isn't something T1's
  dispatch tests cover. It requires manual host+viewer smoke testing (LAN
  connect, screen share, input, clipboard, chat, disconnect/reconnect) before
  merge, in addition to the automated suite passing.

Tasks carrying real regression risk: **T9, T13**. Everything else is either
covered by existing tests moving with the code, or is new UI-only surface
(T12) verified by manual exercise of the one dialog it adds.

## Backlog items placed in sequence

| Backlog item | Task | Why here |
|---|---|---|
| `ReceiveLoop` conflates transport and handler errors | **T3** | Independent of the extraction graph; fixable today inside `TcpTransport` without waiting on Session. Doing it before T9 means `HostSession`/`ViewerSession` are built against a transport that already reports the right error class, instead of inheriting the ambiguity. |
| WinForms removal (`UseWindowsForms=true`) + paired macOS CI assertion flip | **T8** | Independent of Session/dispatch. Must land as one commit — the task brief's own constraint — because T4's CI has a `windows-latest` assertion counting `System.Drawing` tests and a paired `macos-latest` assertion confirming the suite is expectedly unrunnable there; removing `System.Drawing` without flipping both in the same commit means CI fails in a way that looks like a regression, not an intentional change. |
| Pin-mismatch recovery UI | **T12** | Needs the Session/UI boundary (a place to route `CertificatePinMismatchException` to a dialog instead of generic `TcpTransport.Error` handling) to exist cleanly — placed after T9 so the new dialog is wired through `Session`'s events, not bolted onto the old inline handler it would otherwise have to duplicate. |
| Live end-to-end Hello-flow socket test | **T10** | Directly blocked on `HostSession`/`ViewerSession` existing as drivable-without-UI objects — the backlog row's own stated precondition. Runs immediately after T9. |

## Risk

| Task | Failure mode / early warning | Backout |
|---|---|---|
| **T9 — Session extraction** | Largest behavioral move in the plan. Early warning: a smoke-test session that connects but silently drops input events, or a handshake that succeeds locally but not across the LAN (dispatch timing subtly changed by moving off direct `Dispatcher.Post`). If T1's characterization tests all pass but manual smoke testing shows different behavior, stop — that's a sign the pure-function extraction in T1 missed a side effect. | Revert the single T9 commit/branch; `MainViewModel` still has the pre-T9 inline dispatch since T1 didn't change its behavior, only its internal shape. If T9 can't be reverted cleanly because later tasks already depend on `Session` existing, that dependency is the signal T9 was not actually split finely enough — split it into "introduce `Session` classes with the old dispatch still inline in `MainViewModel` calling into them" and "delete the inline copies" as two separate commits before proceeding further. |
| **T13 — Delete MainViewModel / split per-mode VMs** | Only task with real UI/XAML risk and the weakest automated coverage (dispatch tests don't cover bindings). Early warning: any regression a human wouldn't notice from a test run — layout shift, a control that no longer updates, a command that silently no-ops because its binding path changed during the split. | Land per-mode view model as an addition first (new `HostModeViewModel`/`ViewerModeViewModel` classes, `MainViewModel` still exists and still works), verify manually, *then* remove `MainViewModel` in a separate commit. Never combine "add the replacement" and "delete the original" in one commit for this task specifically — it's the one place in the plan where that's not just tidiness, it's the actual backout mechanism. |
| **T7 — Extract PixelWizard.Transport.WebSocket** | Lowest apparent risk (STATUS_REPORT rates `WebSocketHostServer` extraction 3/5, the lowest of the mechanical moves) but the browser-viewer protocol is unversioned and, until this task adds coverage, untested by anything in this repo (`docs/PROTOCOL.md`'s own words: "no browser JS changes were needed in any Phase 1 commit" — meaning also no test caught one if it had been needed). Early warning: browser viewer connects but frames don't render, with the .NET side reporting nothing wrong. | T7's scope includes a minimal Playwright smoke test (headless Chromium, following the pattern already proven in `spikes/webrtc-desktop/web-peer/drive.mjs`): load the viewer page, connect to a `PixelWizard.WindowsHost`/`LinuxHost` test instance over the extracted `WebSocketHostServer`, assert at least one frame renders. This is real added scope (~an afternoon), justified because the browser viewer stops being a demo and becomes the Phase 5 "expert client" — it should not enter that phase with zero automated coverage. If the smoke test can't be made to pass quickly, that itself is the early-warning signal; revert the single-project split and investigate before retrying, since nothing downstream depends on `WebSocketHostServer` having its own project yet. |

## What Phase 2 does not do

- **No WebRTC.** `StreamFrame`/multi-stream plumbing from Phase 1 stays inert;
  T5 (Media extraction) must not be used as an opportunity to start wiring a
  second transport for it. That's Phase 4.
- **No annotation/overlay features.** `StreamKind.Overlay` exists as an enum
  value only; nothing in Phase 2 gives it a producer or consumer. Phase 5.
- **No Flutter/mobile client work**, including no speculative "make Session
  portable to non-.NET" abstraction. `PixelWizard.Session`'s only consumer
  through Phase 2 is `AvaloniaClient`; designing for a hypothetical second
  consumer now is exactly the kind of premature generality this plan should
  avoid. Phase 6.
- **No TOFU-pinning redesign.** The backlog's router-mediated fingerprint
  exchange (item 5) is real but is router-protocol work, not Session/UI work
  — it's out of scope here even though T12 touches the same
  `CertificatePinMismatchException` surface. T12 adds the recovery dialog for
  an already-detected mismatch; it does not change how or when pinning
  happens.
- **No `ScreenChangeDetector` consolidation.** Backlog item 3 (near-duplicate
  detector implementations) is explicitly gated on T4's Windows CI assertion
  actually executing the Windows suite — tempting to fold into T5 (Media
  extraction) since it touches the same files, but it's a Phase 3 item by the
  backlog's own tag and should stay there; T5 moves the detectors as-is,
  duplication included.
- **The 884-line XAML split** named in ADR-001's Phase 2 row is folded into
  T13 rather than given its own task — splitting the views has no independent
  meaning separate from splitting the view models they bind to. **T13 also
  consolidates every hardcoded color and spacing literal currently scattered
  across that XAML into a single `ResourceDictionary`**, using provisional
  values — not a redesign, and no visual value changes while splitting. The
  point is one file for Phase 4.5 to swap values in, instead of six files to
  hunt through. Do not build the token pipeline or touch `docs/DESIGN-SYSTEM.md`'s
  provisional values as part of this — that's Phase 4.5's job, not this task's.

## Roadmap update: Phase 4.5 — Design foundation (new)

`docs/DESIGN-SYSTEM.md` (added alongside this plan) inserts a new phase into
the roadmap between Phase 4 and Phase 5:

| Phase | Scope | Duration |
|---|---|---|
| **4.5 — Design foundation** *(new)* | Competitive teardown (TeamViewer, AnyDesk, RustDesk, TeamViewer Pilot, Vuforia Chalk, Help Lightning). Settle the provisional values in `docs/DESIGN-SYSTEM.md`. Build `design/tokens.json` and the Style Dictionary pipeline (CSS, Dart, and the custom Avalonia `ResourceDictionary` format). Wireframe the four core flows: pair, consent, active session, connection failure. Deliberate review of the safety-critical UI section (consent dialog, viewing badge, certificate mismatch). Write `docs/DESIGN-PRINCIPLES.md`. | ~2–3 wk |

It sits there, not earlier and not folded into Phase 2, because Phase 5's
browser expert client is the first genuinely new UX surface in the roadmap —
the foundation needs to exist before that surface is built, not be
retrofitted onto it afterward. Phase 2 does no more toward this than T13's
`ResourceDictionary` consolidation above: structural tidying that makes
Phase 4.5's eventual token swap a one-file change, nothing that anticipates
what the tokens will actually be.
