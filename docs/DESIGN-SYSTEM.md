# Design System — PixelWizard Connect

Structural definition. This document is the authority for every UI decision across the
Avalonia desktop client, the browser expert viewer, and the Flutter mobile app.

Status: **draft, pre-implementation.** Values marked *provisional* are placeholders until
the Phase 4.5 design foundation task settles them.

---

## 1. Product principles

These are not general design advice. They are specific to a remote-support tool and each one
should be checkable against a screen.

**P1 — Assume the user is stressed.** Support happens when something is broken. Nobody opens
this product in a calm, exploratory mood. Every extra step and every ambiguous label lands
on someone already frustrated. Default to fewer choices, larger targets, plainer words.

**P2 — Two users, not one.** The *expert* is a repeat user who wants speed, density, and
keyboard shortcuts. The *field user or customer* may be non-technical, using it once, and
unsure whether they are being scammed. These are different interfaces, not one interface
with a toggle.

**P3 — Consent UI is a safety control, not decoration.** Remote-access scams are an
industry-wide epidemic. The consent dialog and the viewing badge are the last line of
defence. Never optimise them for reduced friction. Friction is the feature.

**P4 — Failure is the common state.** Connection problems will be the most-seen screen in the
product. A spinner and a shrug is the competitor baseline; beating it is cheap and
differentiating. Every failure state names what happened, what it means, and what to try.

**P5 — Follow convention, do not innovate.** Jakob's Law: people spend most of their time in
other products and expect yours to work like those. Match TeamViewer/AnyDesk pairing and
session-bar conventions. Novelty in this category is a cost, not a feature.

**P6 — Design for the field, not the desk.** One-handed, phone held up, direct sunlight,
gloves, noise, weak signal. Nothing about this can be evaluated sitting at a desk.

---

## 2. Token architecture

Three tiers. Only the middle tier is referenced by application code.

```
Tier 1  PRIMITIVE     radix.blue.9, radix.slate.11, space.4, font.size.3
                      Raw values. Never referenced directly by a component.
   |
Tier 2  SEMANTIC      color.action.primary, color.status.danger, space.inset.md
                      What code and specs reference. Platform-agnostic intent.
   |
Tier 3  COMPONENT     button.primary.background, badge.recording.fill
                      Only when a component needs a value that no semantic token expresses.
                      Use sparingly; a large tier 3 means tier 2 is wrong.
```

**Naming rule: semantic, never literal.** `color.action.primary`, not `blue500`. Literal names
break the moment dark mode arrives or mobile needs a lighter value for sunlight legibility.

### Source of truth

`design/tokens.json`, W3C design-token format. Committed to the repo, not exported from a
design tool — with one developer and no designer, the repo is the single source.

### Generation pipeline

[Style Dictionary](https://styledictionary.com) transforms one JSON source into three outputs:

| Target | Format | Output |
|---|---|---|
| Browser expert viewer | `css/variables` (built-in) | `web/tokens.css` — CSS custom properties |
| Flutter mobile | `flutter/class.dart` (built-in) | `lib/theme/tokens.g.dart` |
| Avalonia desktop | custom format (~30 lines of JS) | `Styles/Tokens.axaml` — `ResourceDictionary` |

`npm run build-tokens` regenerates all three. Generated files are committed so no build
step is required to compile, but CI verifies they match the source — a drift check, so a
hand-edited generated file fails the build.

Two of the three formats ship with Style Dictionary. Only Avalonia needs custom work, and it
is a short function emitting XML.

---

## 3. Color

### Base palette: Radix Colors

Do not hand-pick colors. Radix provides 12-step scales where each step has an assigned role
(backgrounds, hover, borders, overlays, text), APCA-based contrast targets, and matched dark
variants. Radix scales are not to be customised — brand colors are added as extra scales.

**Selected scales** *(provisional — settle in Phase 4.5)*:

| Role | Scale | Why |
|---|---|---|
| Accent | `indigo` | Trustworthy, non-alarming, distinct from status colors |
| Neutral | `slate` | Radix's recommended pairing for indigo |
| Danger | `red` | Disconnects, failures, denied consent |
| Warning | `amber` | Degraded connection, certificate mismatch |
| Success | `green` | Connected, consent granted |

### Semantic mapping

| Token | Light | Dark | Use |
|---|---|---|---|
| `color.bg.canvas` | `slate.1` | `slateDark.1` | App background |
| `color.bg.surface` | `slate.2` | `slateDark.2` | Cards, panels |
| `color.border.subtle` | `slate.6` | `slateDark.6` | Dividers |
| `color.border.strong` | `slate.8` | `slateDark.8` | Input borders |
| `color.text.primary` | `slate.12` | `slateDark.12` | Body text |
| `color.text.secondary` | `slate.11` | `slateDark.11` | Labels, hints |
| `color.action.primary` | `indigo.9` | `indigoDark.9` | Primary buttons |
| `color.action.primaryHover` | `indigo.10` | `indigoDark.10` | Hover |
| `color.status.danger` | `red.9` | `redDark.9` | Errors, disconnect |
| `color.status.warning` | `amber.9` | `amberDark.9` | Degraded states |
| `color.status.success` | `green.9` | `greenDark.9` | Connected |

### Reserved: the session-active color

The viewing badge and any "you are being viewed" indicator use a **dedicated, unmistakable
color used nowhere else in the product**. Reusing the accent or a status color here means the
single most safety-critical signal blends into ordinary UI. Candidate: `red.9` reserved
exclusively, with `color.status.danger` remapped to a different scale.

Decide in Phase 4.5. It is a security decision, not an aesthetic one.

---

## 4. Typography

System fonts on every platform. No bundled webfont: it costs download size, adds a licence to
track, and buys nothing users will notice.

| Platform | Stack |
|---|---|
| Avalonia | Inter (already bundled) → system fallback |
| Browser | `system-ui, -apple-system, Segoe UI, Roboto, sans-serif` |
| Flutter | Platform default (SF Pro / Roboto) |

**Scale** (1.25 ratio, rounded) *(provisional)*:

| Token | Size | Use |
|---|---|---|
| `font.size.1` | 12 | Captions, timestamps |
| `font.size.2` | 14 | Body, labels — the default |
| `font.size.3` | 16 | Emphasised body; **minimum for field-user-facing text** |
| `font.size.4` | 20 | Section headings |
| `font.size.5` | 25 | Screen titles |
| `font.size.6` | 31 | Connection code display |

**Rule:** any text a non-technical field user must read under stress is `font.size.3` or
larger. This includes the consent dialog, connection codes, and every error message.

---

## 5. Spacing, radius, motion

**Spacing** — 4px base: `space.1`=4, `2`=8, `3`=12, `4`=16, `5`=24, `6`=32, `7`=48, `8`=64.

**Radius** — `radius.sm`=4 (inputs), `radius.md`=8 (buttons, cards), `radius.lg`=16 (modals),
`radius.full`=9999 (pills, avatars).

**Touch targets** — minimum 44×44 (Apple HIG / WCAG 2.2). **Field app minimum 56×56**: gloves,
motion, sunlight.

**Motion** — `duration.fast`=150ms (hover, focus), `duration.base`=250ms (panels, modals),
`duration.slow`=400ms (screen transitions). Respect `prefers-reduced-motion`. Never animate
anything that delays a user under stress.

---

## 6. Component catalog

Specifications, implemented three times. A spec is complete when three implementations built
from it independently would match.

| Component | Desktop | Browser | Mobile | Notes |
|---|---|---|---|---|
| Button (primary/secondary/danger/ghost) | ✓ | ✓ | ✓ | States: default, hover, active, focus, disabled, pending |
| Text input | ✓ | ✓ | ✓ | Label, hint, error, disabled |
| Connection code display | ✓ | ✓ | — | Monospace, grouped, one-tap copy |
| Modal / dialog | ✓ | ✓ | ✓ | Escape = cancel; never Escape = confirm |
| **Consent dialog** | ✓ | — | ✓ | See §7. Own spec, not a modal variant |
| **Viewing badge** | ✓ | — | ✓ | Always-on-top, non-dismissable while active |
| Status pill | ✓ | ✓ | ✓ | Connected / connecting / degraded / failed |
| Session toolbar | ✓ | ✓ | ✓ | Follows TeamViewer/AnyDesk convention (P5) |
| Error panel | ✓ | ✓ | ✓ | What happened / what it means / what to do |
| Annotation toolbar | — | ✓ | ✓ | Phase 5 |
| Freeze-frame control | — | ✓ | ✓ | Phase 5 |
| Camera viewfinder | — | — | ✓ | Phase 6 |

**Every component spec states:** name, variants, states, min dimensions, tokens used,
keyboard behaviour, screen-reader label, and what it does at the smallest supported size.

---

## 7. Safety-critical UI

These have stricter rules than the rest of the product and change only with deliberate review.

### Consent dialog
- Shows the connecting endpoint and the timestamp
- **Deny is the default focus.** Enter must not grant consent
- Escape = Deny
- No "don't ask again" — unattended access does not exist and must not be simulated
- Text at `font.size.3` minimum, plain language, no jargon
- Never auto-dismiss, never time out into acceptance
- Never reduce friction here to improve conversion

### Viewing badge
- Visible whenever a viewer is connected, without exception
- Always-on-top, not dismissable, not minimisable while the session is active
- Uses the reserved session-active color (§3)
- States what is being shared and by whom

### Certificate mismatch
- Distinct from "host offline" — opposite user responses
- Explains that this may mean a legitimate reinstall *or* an interception attempt
- Offers "forget this host" as an explicit, deliberate action, never as a one-click dismissal

---

## 8. Accessibility floor

WCAG 2.2 AA, non-negotiable, EU-relevant for a commercial product.

- Contrast 4.5:1 body text, 3:1 large text and UI boundaries (Radix scales satisfy this by
  construction when steps are used in their intended roles)
- Full keyboard operability on desktop and web; visible focus ring everywhere
- Touch targets per §5
- Screen-reader labels on every interactive element
- Never color alone to convey state — pair with icon or text
- Respect `prefers-reduced-motion` and OS text-size settings

---

## 9. Phase placement

| Phase | UI work |
|---|---|
| **2** (current) | Structural only. Split the 884-line XAML into per-mode controls. **Consolidate hardcoded colors and spacing into one `ResourceDictionary` while splitting** — provisional values, but one file to swap later instead of six to hunt through. No visual redesign: you would be polishing screens the design phase may delete. |
| **4.5** *(new, ~2–3 wk)* | **Design foundation.** Competitive teardown (TeamViewer, AnyDesk, RustDesk, TeamViewer Pilot, Vuforia Chalk, Help Lightning). Settle the provisional values in this document. Build `tokens.json` + the Style Dictionary pipeline + the Avalonia custom format. Wireframe the four core flows: pair, consent, active session, connection failure. Deliberate review of §7 as a safety control. Write `docs/DESIGN-PRINCIPLES.md`. |
| **5** | Browser expert client is the first surface built on the system. Annotation toolbar and freeze-frame specs added to the catalog. |
| **6** | **Explicit mobile design pass.** Phone AR assist is not the desktop UI scaled down — different user, posture, and failure modes. Material 3 `ThemeData` fed *from* tokens, not bypassed. Apple HIG + Material 3 conventions for store review. |
| **7** | Field validation. Test with five non-technical users on real phones in real conditions (Nielsen: five users surface ~85% of usability problems). Sunlight, gloves, weak signal. |

Phase 4.5 sits after WebRTC works and before the browser expert client, because Phase 5 is
the first genuinely new UX surface and the foundation must exist before it, not after.

---

## 10. References

| Source | Use |
|---|---|
| *Refactoring UI* (Wathan & Schoger) | Written for developers without design training. Highest-leverage single read |
| [Radix Colors](https://www.radix-ui.com/colors) | The palette. Scale composition and semantic aliasing docs |
| [Style Dictionary](https://styledictionary.com) | Token pipeline. See the built-in Flutter example |
| NN/g 10 usability heuristics | The checklist to evaluate any screen against |
| [Laws of UX](https://lawsofux.com) | Jakob's, Fitts's, Hick's |
| Apple HIG / Material Design 3 | Platform conventions for Phase 6. Non-negotiable for store review |
| Apple HIG (AR) / Google ARCore design guidelines | Instructing phone movement, tracking-init states, graceful failure on featureless surfaces |
| WCAG 2.2 AA | Accessibility floor |
