#!/usr/bin/env bash
#
# build-all.sh — dispatcher for PixelWizard Connect packaging.
#
# Produces self-contained, single-file publishes for each supported platform by
# delegating to the per-platform scripts. Output lands in the chosen output dir
# (default: dist/ at the repo root).
#
# Usage:
#   ./packaging/build-all.sh [output-dir] [platform]
#
#   output-dir   Where artifacts are written (default: dist)
#   platform     One of: macos | linux | all (default: all)
#                Windows packaging uses PowerShell (build-windows.ps1) and is
#                NOT invoked from this bash dispatcher — run it on Windows.
#
# Examples:
#   ./packaging/build-all.sh
#   ./packaging/build-all.sh dist macos
#   ./packaging/build-all.sh /tmp/out linux
#
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

OUT_DIR="${1:-dist}"
PLATFORM="${2:-all}"

# Resolve OUT_DIR to an absolute path so the per-platform scripts agree on it.
mkdir -p "$REPO_ROOT/$OUT_DIR" 2>/dev/null || true
OUT_DIR_ABS="$(cd "$REPO_ROOT" && cd "$OUT_DIR" 2>/dev/null && pwd || echo "$REPO_ROOT/$OUT_DIR")"

usage() {
    sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

echo "PixelWizard Connect — packaging"
echo "  repo root : $REPO_ROOT"
echo "  output    : $OUT_DIR_ABS"
echo "  platform  : $PLATFORM"
echo

case "$PLATFORM" in
    macos)
        "$SCRIPT_DIR/macos/build-macos-app.sh" "$OUT_DIR_ABS"
        ;;
    linux)
        "$SCRIPT_DIR/linux/build-linux.sh" "$OUT_DIR_ABS"
        ;;
    all)
        "$SCRIPT_DIR/macos/build-macos-app.sh" "$OUT_DIR_ABS"
        "$SCRIPT_DIR/linux/build-linux.sh" "$OUT_DIR_ABS"
        echo
        echo "NOTE: Windows packaging is not run from bash."
        echo "      On Windows, run: packaging\\windows\\build-windows.ps1"
        ;;
    -h|--help|help)
        usage
        exit 0
        ;;
    *)
        echo "Unknown platform: $PLATFORM" >&2
        usage
        exit 1
        ;;
esac

echo
echo "Done. Artifacts in: $OUT_DIR_ABS"
