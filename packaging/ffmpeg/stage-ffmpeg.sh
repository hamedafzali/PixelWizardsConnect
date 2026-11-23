#!/usr/bin/env bash
#
# stage-ffmpeg.sh — fetch, verify and unpack the vendored LGPL FFmpeg (P4.9).
#
# Usage:
#   stage-ffmpeg.sh <platform> <dest-dir>
#
#   platform   macos-arm64 | windows-x64  (a key of manifest.json "assets")
#   dest-dir   Directory to create; receives the shared libraries and licenses/.
#              The app finds it as <executable dir>/ffmpeg (FfmpegBootstrap).
#
# Where the zip comes from, first match wins:
#   1. $PIXELWIZARD_FFMPEG_ZIP  — a zip file, or a directory holding the asset named in manifest.json.
#   2. The GitHub release tagged manifest.releaseTag, downloaded with curl from
#      $PIXELWIZARD_FFMPEG_BASE_URL, else from this clone's "origin" remote (GitHub only).
#      That release may not exist yet; the script then fails. It never skips silently.
#
# The zip's SHA-256 must equal manifest.json assets.<platform>.sha256 BEFORE anything is unpacked.
# On macOS every dylib is also checked: install names and inter-library references must be
# @loader_path/@rpath-relative or system paths, so the bundle can be code-signed and moved.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MANIFEST="$SCRIPT_DIR/manifest.json"
PLATFORM="${1:-}"; DEST="${2:-}"
[ -n "$PLATFORM" ] && [ -n "$DEST" ] || { sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2; exit 2; }

command -v python3 >/dev/null || { echo "stage-ffmpeg: python3 is required to read manifest.json" >&2; exit 1; }
mf() { python3 -I -c 'import json,sys; m=json.load(open(sys.argv[1])); v=m
for k in sys.argv[2].split("."): v=v[k]
print(v)' "$MANIFEST" "$1"; }

ASSET="$(mf "assets.$PLATFORM.assetName")" || { echo "stage-ffmpeg: no asset '$PLATFORM' in manifest.json" >&2; exit 1; }
WANT_SHA="$(mf "assets.$PLATFORM.sha256")"
TAG="$(mf releaseTag)"

sha256_of() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
ZIP=""
if [ -n "${PIXELWIZARD_FFMPEG_ZIP:-}" ]; then
    if [ -d "$PIXELWIZARD_FFMPEG_ZIP" ]; then ZIP="$PIXELWIZARD_FFMPEG_ZIP/$ASSET"; else ZIP="$PIXELWIZARD_FFMPEG_ZIP"; fi
    [ -f "$ZIP" ] || { echo "stage-ffmpeg: PIXELWIZARD_FFMPEG_ZIP does not point at $ASSET: $ZIP" >&2; exit 1; }
    echo "stage-ffmpeg: using local zip $ZIP"
else
    BASE="${PIXELWIZARD_FFMPEG_BASE_URL:-}"
    if [ -z "$BASE" ]; then
        remote="$(git -C "$SCRIPT_DIR" remote get-url origin 2>/dev/null || true)"
        slug="$(printf '%s' "$remote" | sed -E 's#^(git@github\.com:|https://github\.com/)##; s#\.git$##')"
        case "$slug" in */*) BASE="https://github.com/$slug/releases/download/$TAG" ;; esac
    fi
    [ -n "$BASE" ] || { echo "stage-ffmpeg: cannot work out the release URL; set PIXELWIZARD_FFMPEG_ZIP or PIXELWIZARD_FFMPEG_BASE_URL" >&2; exit 1; }
    echo "stage-ffmpeg: downloading $BASE/$ASSET"
    ZIP="$WORK/$ASSET"
    if ! curl -fsSL --retry 2 -o "$ZIP" "$BASE/$ASSET"; then
        echo "stage-ffmpeg: ERROR: could not download $ASSET from release '$TAG'. The release may not be published yet." >&2
        echo "  Pass the zip with PIXELWIZARD_FFMPEG_ZIP=<file or dir>, or build without FFmpeg (JPEG-only) with the script's no-FFmpeg option." >&2
        exit 1
    fi
fi

GOT_SHA="$(sha256_of "$ZIP")"
if [ "$GOT_SHA" != "$WANT_SHA" ]; then
    echo "stage-ffmpeg: ERROR: SHA-256 mismatch for $ASSET" >&2
    echo "  expected $WANT_SHA (manifest.json)" >&2
    echo "  actual   $GOT_SHA" >&2
    exit 1
fi
echo "stage-ffmpeg: SHA-256 verified"

rm -rf "$DEST"; mkdir -p "$DEST"
unzip -q "$ZIP" -d "$DEST"

# Every library the manifest lists must be present; the licence notices must have come along.
for lib in $(python3 -I -c 'import json,sys; print(" ".join(json.load(open(sys.argv[1]))["assets"][sys.argv[2]]["libraries"]))' "$MANIFEST" "$PLATFORM"); do
    [ -f "$DEST/$lib" ] || { echo "stage-ffmpeg: ERROR: $lib missing from $ASSET" >&2; exit 1; }
done
[ -f "$DEST/licenses/COPYING.LGPLv2.1" ] || { echo "stage-ffmpeg: ERROR: licenses/COPYING.LGPLv2.1 missing from $ASSET" >&2; exit 1; }

case "$PLATFORM" in
    macos-*)
        # Signing-ready: nothing may reference an absolute, non-system path (a build directory would
        # break once the bundle is signed and moved). Verify only; the shipped zip is already clean.
        bad=0
        for f in "$DEST"/*.dylib; do
            while read -r ref; do
                case "$ref" in
                    @loader_path/*|@rpath/*|@executable_path/*|/usr/lib/*|/System/Library/*) ;;
                    *) echo "stage-ffmpeg: ERROR: $(basename "$f") references non-relative path: $ref" >&2; bad=1 ;;
                esac
            done < <({ otool -D "$f" | tail -n +2; otool -L "$f" | tail -n +2 | awk '{print $1}'; } )
            if otool -l "$f" | grep -A2 LC_RPATH | grep -E 'path /' | grep -vE 'path /(usr/lib|System)/' >/dev/null; then
                echo "stage-ffmpeg: ERROR: $(basename "$f") has an absolute LC_RPATH" >&2; bad=1
            fi
        done
        [ "$bad" = 0 ] || exit 1
        echo "stage-ffmpeg: dylib install names and references are @loader_path-relative or system"
        ;;
esac
echo "stage-ffmpeg: staged $PLATFORM into $DEST"
