# Backlog — deferred items

Items intentionally deferred out of the task that surfaced them, tagged by the
`docs/ADR-001-architecture.md` phase they belong to.

| Item | Why deferred | Phase |
|---|---|---|
| `TcpTransport.ReceiveLoop` invokes `MessageReceived` synchronously, so its catch/break wraps every downstream handler — a rendering NRE is indistinguishable from a corrupt frame, and both kill the session. Transport errors and handler errors are different failure classes. | Needs the Session/transport boundary to exist | 2 |
| Viewer UI may not surface a sensible message on the deserialize-failure disconnect path | UX question, no owner yet | 5 |
| `ScreenChangeDetector` (System.Drawing) and `SkiaScreenChangeDetector` (SkiaSharp) are near-duplicate algorithms in two implementations, plus a third byte-identical copy of the Skia one (`PixelWizard.AvaloniaClient.Platform.Mac.SkiaScreenChangeDetector` vs `PixelWizard.LinuxHost.SkiaScreenChangeDetector`) | Consolidation is only safe once all are under an identical assertion suite (T2, `tests/PixelWizard.Tests/ScreenChangeDetector/`) | 3 |
| T2's `ScreenChangeDetectorAssertionSuite` has not been run against `PixelWizard.WindowsHost.ScreenChangeDetector` on real Windows — only cross-compiled and code-reviewed line-by-line against the Skia implementation. No disagreement has been found by inspection (block size, threshold, sampling step, and merge-margin constants all match), but this is unverified empirically. | Needs T4's `windows-latest` CI job to actually execute `SystemDrawingScreenChangeDetectorTests` | 3/4 |
