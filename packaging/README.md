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
| `macos/build-macos-app.sh [out] [arm64\|x64]` | macOS | `dist/PixelWizard Connect.app` (default `arm64` → `osx-arm64`). |
| `windows/build-windows.ps1 [-OutDir out]` | Windows | `dist\PixelWizardConnect-win-x64\` (single-file exe; icon via `<ApplicationIcon>`). |
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
