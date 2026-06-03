#!/usr/bin/env bash
#
# build-macos-app.sh — build a self-contained "PixelWizard Connect.app" bundle.
#
# Usage:
#   ./packaging/macos/build-macos-app.sh [output-dir] [arch]
#
#   output-dir   Where the .app is written (default: dist)
#   arch         arm64 | x64  (default: arm64) → osx-arm64 / osx-x64 RID
#
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

OUT_DIR="${1:-$REPO_ROOT/dist}"
ARCH="${2:-arm64}"

case "$ARCH" in
    arm64) RID="osx-arm64" ;;
    x64)   RID="osx-x64" ;;
    *) echo "Unknown arch: $ARCH (use arm64 or x64)" >&2; exit 1 ;;
esac

PROJECT="$REPO_ROOT/avalonia/PixelWizard.AvaloniaClient"
# Published binary name (AssemblyName is the default = project name).
BINARY_NAME="PixelWizard.AvaloniaClient"
# Name we expose inside the bundle (matched in Info.plist CFBundleExecutable).
BUNDLE_BINARY="PixelWizardConnect"
APP_NAME="PixelWizard Connect.app"
ICON_SRC="$SCRIPT_DIR/PixelWizard.icns"

STAGING="$(mktemp -d)"
APP_DIR="$OUT_DIR/$APP_NAME"

echo "Publishing $RID (self-contained, single-file)..."
dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained \
    -p:PublishSingleFile=true \
    -o "$STAGING"

echo "Assembling $APP_NAME ..."
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS"
mkdir -p "$APP_DIR/Contents/Resources"

# Copy every published file into MacOS/, then rename the entry binary.
cp -R "$STAGING/." "$APP_DIR/Contents/MacOS/"
if [ -f "$APP_DIR/Contents/MacOS/$BINARY_NAME" ]; then
    mv "$APP_DIR/Contents/MacOS/$BINARY_NAME" "$APP_DIR/Contents/MacOS/$BUNDLE_BINARY"
fi
chmod +x "$APP_DIR/Contents/MacOS/$BUNDLE_BINARY"

# Icon (CFBundleIconFile references "PixelWizard" → PixelWizard.icns).
if [ -f "$ICON_SRC" ]; then
    cp "$ICON_SRC" "$APP_DIR/Contents/Resources/PixelWizard.icns"
else
    echo "WARNING: icon not found at $ICON_SRC — bundle will use the default icon."
fi

cat > "$APP_DIR/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>PixelWizard Connect</string>
    <key>CFBundleDisplayName</key>
    <string>PixelWizard Connect</string>
    <key>CFBundleIdentifier</key>
    <string>com.pixelwizard.connect</string>
    <key>CFBundleExecutable</key>
    <string>PixelWizardConnect</string>
    <key>CFBundleIconFile</key>
    <string>PixelWizard</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSScreenCaptureUsageDescription</key>
    <string>PixelWizard Connect captures your screen so it can be shared with the remote viewer you authorize.</string>
    <key>NSAppleEventsUsageDescription</key>
    <string>PixelWizard Connect uses system events to deliver remote mouse and keyboard input when you allow a remote session.</string>
</dict>
</plist>
PLIST

rm -rf "$STAGING"

echo "Built: $APP_DIR"

# ─────────────────────────────────────────────────────────────────────────────
# TODO: CODE SIGNING + NOTARIZATION (required for distribution; needs an Apple
#       Developer ID Application certificate in your keychain). Not done here.
#
#   codesign --deep --force --options runtime --timestamp \
#       --sign "Developer ID Application: Your Name (TEAMID)" \
#       "$APP_DIR"
#
#   # Zip and submit for notarization:
#   ditto -c -k --keepParent "$APP_DIR" "PixelWizardConnect.zip"
#   xcrun notarytool submit "PixelWizardConnect.zip" \
#       --apple-id "you@example.com" --team-id "TEAMID" \
#       --password "app-specific-password" --wait
#
#   # Staple the ticket onto the app:
#   xcrun stapler staple "$APP_DIR"
# ─────────────────────────────────────────────────────────────────────────────
