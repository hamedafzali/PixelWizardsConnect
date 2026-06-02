# PixelWizard Connect Roadmap

## Product Direction

PixelWizard Connect should start as a focused Windows-to-Windows remote support MVP with a self-hosted Go router. The long-term architecture should keep the UI disposable so the client can later move to Avalonia or a web viewer without rewriting networking, session state, permissions, and protocol code.

The practical niche is not "another TeamViewer clone." The stronger angle is:

- self-hosted control over routing and relay infrastructure
- simple attended support with short-lived connection codes
- clear consent and audit trail
- AI-assisted troubleshooting summaries and next-step suggestions
- later: lightweight device inventory and support history for small teams

## Competitor Notes

TeamViewer and AnyDesk set the user expectation for fast remote control, file transfer, unattended access, address books, permission profiles, and enterprise security controls. TeamViewer is also moving AI into support workflows with AI session summaries, recommendations, and an in-session assistant. AnyDesk emphasizes file transfer, unattended access, two-factor authentication, permission profiles, and cross-platform use.

RustDesk is the closest strategic comparison because it is open source, self-hostable, and supports self-hosted rendezvous/relay servers. That validates the self-hosted server direction. It also shows the hard parts we must plan for: NAT traversal, relay fallback, encryption, setup simplicity, and clear documentation.

Microsoft Remote Help is more enterprise-governance focused. It is useful as a security model reference: organization login, role-based access, compliance warnings, and strong consent. It is less useful as a product model for independent/self-hosted use because it is tied to Microsoft tenant licensing.

MeshCentral is a useful reference for the longer-term admin console direction: web dashboard, agents, remote desktop, terminal, file transfer, scripting, inventory, and device management.

Sources:

- TeamViewer AI: https://www.teamviewer.com/apac/products/remote/features/ai/
- TeamViewer Session Insights: https://www.teamviewer.com/en-in/global/support/knowledge-base/teamviewer-remote/remote-control/generate-session-summaries-with-session-insights/
- AnyDesk features: https://anydesk.com/en/features
- AnyDesk access docs: https://support.anydesk.com/docs/access
- RustDesk: https://rustdesk.com/
- RustDesk docs: https://rustdesk.com/docs/en/
- Microsoft Intune Remote Help: https://learn.microsoft.com/en-us/intune/intune-service/fundamentals/remote-help-windows
- MeshCentral docs: https://docs.meshcentral.com/meshcentral/

## Phase 0: Current Prototype Status

Status: concept prototype.

What exists:

- WPF Windows client
- TCP client/server connection path
- simple screen capture and JPEG delta sending
- mouse/keyboard input messages
- Go HTTP router for connection-code lookup

Main issues:

- WPF client is Windows-only
- router flow currently does not solve internet/NAT connectivity
- router-client path has placeholder fallback behavior
- no authentication, TLS, approval prompt, or rate limiting
- screen streaming is not a real video pipeline
- `SignalingServer.cs` is placeholder-quality and should not be part of production path

## Phase 1: Windows MVP

Goal: prove that a user can host a Windows machine, share a short code, and another Windows machine can view/control it safely on a local network.

Keep:

- Go router server
- WPF client shell
- Windows APIs for capture and input

Build:

- Dockerfile and deployment notes for the Go router
- proper `/register`, `/connect`, `/health` behavior
- remove fake `localhost:8888` router fallback
- clear host approval prompt before remote control starts
- session disconnect and timeout behavior
- basic connection-code expiry
- basic shared secret or session token
- cleaner WPF UI for host/connect/status/session views

Do not build yet:

- unattended access
- file transfer
- browser client
- cross-platform host
- AI automation that controls the remote machine directly

## Phase 2: Architecture Cleanup

Goal: make WPF replaceable.

Create project boundaries:

- `PixelWizard.Core`: session state, models, permissions, protocol contracts
- `PixelWizard.Transport`: TCP/WebRTC/router clients
- `PixelWizard.WindowsHost`: screen capture and input injection
- `PixelWizard.WpfClient`: current desktop UI
- `router-server`: Go router/relay services

Add interfaces:

- `IScreenCapture`
- `IInputInjector`
- `IRouterClient`
- `ISessionTransport`
- `ISessionRecorder`
- `IAuditLog`

This phase is what prevents a future Avalonia or web client from becoming a full rewrite.

## Phase 3: Secure Self-Hosted Router

Goal: make the Alpine Docker server useful beyond LAN demos.

Build:

- Dockerfile for Alpine/container use
- env-based config for port, token secrets, code TTL, cleanup interval
- structured logs
- rate limits on registration and connection-code attempts
- TLS guidance through Caddy, Traefik, or nginx
- persistent audit logs
- optional relay service if direct connection fails

Decision point:

- keep simple TCP for LAN-only
- move to WebRTC for NAT traversal and low-latency media
- add TURN/relay fallback for difficult networks

Recommendation: plan for WebRTC, but do not block Phase 1 on it.

## Phase 4: Cross-Platform Client Decision

Goal: choose the next client shape based on real MVP feedback.

Options:

- Avalonia desktop client: best if we want Windows/Linux/macOS native desktop apps.
- Web viewer: best if the support person should connect from anywhere without installing a viewer.
- Native host agent plus web dashboard: best long-term support/RMM direction.

Recommendation:

- keep WPF during MVP
- move to Avalonia after core/session/transport boundaries exist
- keep Windows host first because capture/input are platform-specific anyway

## Phase 5: AI Support Layer

Goal: use AI to make support sessions more useful, not gimmicky.

Good AI uses:

- automatic session summary after disconnect
- issue timeline: connection started, app opened, error observed, actions taken
- suggested next troubleshooting steps from screen/OCR/session events
- command/runbook suggestions for common fixes
- searchable support history per device
- sensitive-data warning before screenshots or logs are stored
- "explain what changed" after a support session

Avoid early:

- AI taking remote control without explicit human confirmation
- always-on screen recording
- sending screenshots to cloud models by default
- vague chatbot UI without session context

MVP AI feature:

- local-first session notes: technician writes short notes, app generates a summary and next steps after the session.

Later AI feature:

- optional OCR/screenshot analysis with explicit consent, redaction, and audit logging.

## Phase 6: Features Competitors Teach Us To Consider

Core remote support:

- attended access with consent
- unattended access with strong security
- file transfer
- clipboard sync
- multi-monitor support
- session chat
- remote restart/reconnect
- quality controls for speed vs image quality
- session recording with consent

Admin/team features:

- address book/devices list
- roles and permissions
- audit logs
- device inventory
- connection history
- support notes
- branding/custom client

Security:

- TLS everywhere
- end-to-end session encryption
- short-lived pairing codes
- brute-force protection
- 2FA for admin console
- block/allow lists
- explicit permission profiles
- visible session indicator on host machine

## Immediate Next Tasks

1. Keep Go router and add Docker support.
2. Fix router integration so connection-code mode returns and uses the real host endpoint.
3. Improve WPF UI for the MVP.
4. Remove or quarantine placeholder `SignalingServer.cs`.
5. Split router client/networking out of `MainWindow.xaml.cs`.
6. Add a simple session approval flow.
7. Add a first session summary model for future AI notes.
