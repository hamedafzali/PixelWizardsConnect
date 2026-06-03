#!/usr/bin/env bash
#
# build-linux.sh — build a self-contained linux-x64 publish and package it as a
# distributable tarball with a .desktop entry, icon, and an install.sh.
#
# Usage:
#   ./packaging/linux/build-linux.sh [output-dir]
#
#   output-dir   Where the tarball is written (default: dist)
#
# Future option: an AppImage could be produced from the same publish output
# (e.g. with appimagetool) — not implemented here to keep the toolchain minimal.
#
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

OUT_DIR="${1:-$REPO_ROOT/dist}"

PROJECT="$REPO_ROOT/avalonia/PixelWizard.AvaloniaClient"
RID="linux-x64"
# Published binary name (default AssemblyName = project name).
BINARY_NAME="PixelWizard.AvaloniaClient"
ICON_SRC="$SCRIPT_DIR/pixelwizard-connect.png"

STAGING="$(mktemp -d)"
PKG_ROOT="$(mktemp -d)"
PKG_NAME="PixelWizardConnect-linux-x64"
PKG_DIR="$PKG_ROOT/$PKG_NAME"
TARBALL="$OUT_DIR/$PKG_NAME.tar.gz"

echo "Publishing $RID (self-contained, single-file)..."
dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained \
    -p:PublishSingleFile=true \
    -o "$STAGING"

echo "Assembling package..."
mkdir -p "$PKG_DIR"
cp -R "$STAGING/." "$PKG_DIR/"
chmod +x "$PKG_DIR/$BINARY_NAME" 2>/dev/null || true

# Icon
if [ -f "$ICON_SRC" ]; then
    cp "$ICON_SRC" "$PKG_DIR/pixelwizard-connect.png"
else
    echo "WARNING: icon not found at $ICON_SRC — install.sh will skip the icon."
fi

# .desktop entry. Exec is resolved to the installed binary path by install.sh.
cat > "$PKG_DIR/pixelwizard-connect.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=PixelWizard Connect
Comment=Cross-platform remote desktop: host or view another machine
Exec=$BINARY_NAME
Icon=pixelwizard-connect
Terminal=false
Categories=Network;RemoteAccess;
EOF

# install.sh — per-user install into ~/.local.
cat > "$PKG_DIR/install.sh" <<EOF
#!/usr/bin/env bash
set -e

BIN_NAME="$BINARY_NAME"
SRC_DIR="\$(cd "\$(dirname "\${BASH_SOURCE[0]}")" && pwd)"

BIN_DIR="\$HOME/.local/bin"
ICON_DIR="\$HOME/.local/share/icons"
APP_DIR="\$HOME/.local/share/applications"

mkdir -p "\$BIN_DIR" "\$ICON_DIR" "\$APP_DIR"

echo "Installing binary to \$BIN_DIR/\$BIN_NAME"
cp "\$SRC_DIR/\$BIN_NAME" "\$BIN_DIR/\$BIN_NAME"
chmod +x "\$BIN_DIR/\$BIN_NAME"

if [ -f "\$SRC_DIR/pixelwizard-connect.png" ]; then
    echo "Installing icon to \$ICON_DIR/pixelwizard-connect.png"
    cp "\$SRC_DIR/pixelwizard-connect.png" "\$ICON_DIR/pixelwizard-connect.png"
fi

echo "Installing desktop entry to \$APP_DIR/pixelwizard-connect.desktop"
# Point Exec at the absolute installed binary path.
sed "s|^Exec=.*|Exec=\$BIN_DIR/\$BIN_NAME|" \
    "\$SRC_DIR/pixelwizard-connect.desktop" > "\$APP_DIR/pixelwizard-connect.desktop"

echo "Done. Make sure \$BIN_DIR is on your PATH."
EOF
chmod +x "$PKG_DIR/install.sh"

mkdir -p "$OUT_DIR"
echo "Creating tarball $TARBALL ..."
tar -C "$PKG_ROOT" -czf "$TARBALL" "$PKG_NAME"

rm -rf "$STAGING" "$PKG_ROOT"

echo "Built: $TARBALL"
