# Packaging PixelWizard Connect

Scripts that produce **self-contained, single-file** publishes of the Avalonia
client for each platform. Output goes to `dist/` at the repo root by default.

> Code signing / notarization is **not** performed by these scripts (it requires
> certificates). Each script has a clearly marked `TODO` block with the exact
> commands. See [Code signing](#code-signing) below.

## Prerequisites

- **.NET 9 SDK** (`dotnet --version` ≥ 9.0) — required for every script.
- **macOS** host to build the `.app` bundle (uses the macOS runtime ID).
- **Windows** host with PowerShell for the Windows publish.
- **Inno Setup** (`iscc`) — only for building the optional Windows installer.
  Install from <https://jrsoftware.org/isinfo.php>.

## Scripts and their output

| Script | Run on | Produces |
| --- | --- | --- |
| `build-all.sh [out] [macos\|linux\|all]` | macOS/Linux | Dispatcher; calls the per-platform scripts. Does **not** run Windows packaging. |
| `macos/build-macos-app.sh [out] [arm64\|x64] [--no-ffmpeg]` | macOS | `dist/PixelWizard Connect.app` (default `arm64` → `osx-arm64`). |
| `windows/build-windows.ps1 [-OutDir out] [-NoFfmpeg]` | Windows | `dist\PixelWizardConnect-win-x64\` (single-file exe; icon via `<ApplicationIcon>`). |
| `windows/installer.iss` | Windows (`iscc`) | `dist\PixelWizardConnect-Setup-1.0.0.exe` — Start Menu + optional desktop shortcut. Run **after** `build-windows.ps1`. |
| `linux/build-linux.sh [out]` | Linux/macOS | `dist/PixelWizardConnect-linux-x64.tar.gz` containing the binary, `pixelwizard-connect.desktop`, the PNG icon, and `install.sh` (installs into `~/.local`). |

### Examples

```bash
# Everything buildable from a Unix shell (macOS .app + Linux tarball):
./packaging/build-all.sh

# macOS x64 (Intel) bundle into a custom dir:
./packaging/macos/build-macos-app.sh dist x64

# Linux tarball:
./packaging/linux/build-linux.sh
```

```powershell
# Windows publish, then installer:
.\packaging\windows\build-windows.ps1
iscc packaging\windows\installer.iss
```

The Linux tarball installs per-user:

```bash
tar xzf PixelWizardConnect-linux-x64.tar.gz
cd PixelWizardConnect-linux-x64
./install.sh    # copies binary → ~/.local/bin, icon → ~/.local/share/icons,
                # .desktop → ~/.local/share/applications
```

> An **AppImage** is a future option for Linux (build from the same publish
> output with `appimagetool`); not implemented to keep the toolchain minimal.

## Vendored FFmpeg (H.264)

The H.264 video track needs the project's LGPL FFmpeg build (`packaging/ffmpeg/`, pinned by
`packaging/ffmpeg/manifest.json`). The macOS and Windows scripts **require it by default** and stage
it as `ffmpeg/` next to the executable, which is where the app looks (`FfmpegBootstrap`:
`PIXELWIZARD_FFMPEG_DIR`, else `AppContext.BaseDirectory/ffmpeg`):

- macOS: `PixelWizard Connect.app/Contents/MacOS/ffmpeg/` (arm64 only; x64 needs `--no-ffmpeg`).
- Windows: `PixelWizardConnect-win-x64\ffmpeg\`; `installer.iss` ships it (`recursesubdirs`).
- Linux: no vendored FFmpeg. The tarball is JPEG-only (D5).

Where the zip comes from (`packaging/ffmpeg/stage-ffmpeg.sh` / `.ps1`), first match wins:

1. `PIXELWIZARD_FFMPEG_ZIP` — the zip, or a directory containing the asset named in the manifest.
2. The GitHub release tagged `manifest.releaseTag`, fetched from `PIXELWIZARD_FFMPEG_BASE_URL` or the
   clone's `origin`. Until that release is published this fails with an explicit message; it never
   skips silently. (To test before then, pass the zip of run 44884098400 through the variable.)

The zip's SHA-256 is compared with the manifest **before** it is unpacked; a mismatch aborts the build.
On macOS the script also checks every dylib: install names and references must be `@loader_path`/`@rpath`
relative or system paths, with no absolute `LC_RPATH`, so the bundle can be signed and moved. The shipped
zip already satisfies this, so nothing is rewritten and the existing ad-hoc signatures stay valid
(`codesign --deep --force -s -` and `codesign --verify --deep --strict` succeed on the built `.app`).

Opt out with `--no-ffmpeg` (shell) or `-NoFfmpeg` (PowerShell): the build warns and the app runs
JPEG-only.

```bash
PIXELWIZARD_FFMPEG_ZIP=/path/to/zips ./packaging/macos/build-macos-app.sh
./packaging/macos/build-macos-app.sh dist arm64 --no-ffmpeg
```

```powershell
$env:PIXELWIZARD_FFMPEG_ZIP = "C:\zips"; .\packaging\windows\build-windows.ps1
```

**Licence.** The staged `ffmpeg/licenses/` folder (LGPL 2.1 text, FFmpeg `LICENSE.md`, `NOTICE.txt`,
`BUILDINFO.txt` with the source URL and configure line, and on Windows the MIT notices of the NVENC and
AMF headers and winpthreads) travels inside every package. FFmpeg is shipped only as separate shared
libraries, so a user can replace them with their own build of the same FFmpeg major (same file names)
in that `ffmpeg/` folder, as LGPL requires; setting `PIXELWIZARD_FFMPEG_DIR` also overrides it.

**Check.** CI stages the artifact of the pinned `ffmpeg.yml` run with these scripts and runs
`tests/PixelWizard.FFmpeg.Tests` against the staged `ffmpeg/` directory on macOS and Windows. Locally:
`PIXELWIZARD_FFMPEG_DIR="<app>/Contents/MacOS/ffmpeg" dotnet test tests/PixelWizard.FFmpeg.Tests -c Release`.
A machine needs no FFmpeg installed: the libraries load from the app's own folder and import only OS
libraries.

## Icon assets

The scripts expect these assets (referenced, copied when present):

- `avalonia/PixelWizard.AvaloniaClient/Assets/app.ico` — Windows exe icon, wired
  via `<ApplicationIcon>` in the csproj.
- `packaging/macos/PixelWizard.icns` — macOS bundle icon.
- `packaging/windows/PixelWizard.ico` — Inno Setup `SetupIconFile`.
- `packaging/linux/pixelwizard-connect.png` — Linux icon.

## Code signing

Signing and notarization are **required for distribution** and are **manual
steps** that need certificates. They are intentionally left out of the scripts;
the exact commands live as commented `TODO` blocks in each script.

### macOS (needs an Apple Developer ID Application certificate)

```bash
codesign --deep --force --options runtime --timestamp \
    --sign "Developer ID Application: Your Name (TEAMID)" \
    "dist/PixelWizard Connect.app"

ditto -c -k --keepParent "dist/PixelWizard Connect.app" PixelWizardConnect.zip
xcrun notarytool submit PixelWizardConnect.zip \
    --apple-id "you@example.com" --team-id "TEAMID" \
    --password "app-specific-password" --wait
xcrun stapler staple "dist/PixelWizard Connect.app"
```

### Windows (needs a code-signing certificate)

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a `
    "dist\PixelWizardConnect-win-x64\PixelWizard.AvaloniaClient.exe"
```

Sign the exe **before** building the installer, then optionally sign the
installer `.exe` the same way.
