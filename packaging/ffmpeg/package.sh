#!/usr/bin/env bash
# Packs the installed libraries, the licence notices and the build record into one zip.
#   package.sh <macos-arm64|windows-x64> <ffmpeg-source-dir> <work-dir> <out-dir> <version> <revision>
# Env: DEPS_LICENSES (optional) = directory of licence files for header-only dependencies.
set -euo pipefail

platform=${1:?platform}; src=${2:?src}; work=${3:?work}; out=${4:?out}; version=${5:?version}; rev=${6:?revision}
mkdir -p "$out"
out=$(cd "$out" && pwd); work=$(cd "$work" && pwd); src=$(cd "$src" && pwd)
prefix="$work/prefix"
asset="ffmpeg-${version}-pw${rev}-${platform}.zip"
stage="$work/stage"
rm -rf "$stage"; mkdir -p "$stage/licenses"

case "$platform" in
  macos-arm64)
    # Ship each library under its soname (libavcodec.63.dylib): that is the name the other
    # libraries' @loader_path install names, and FFmpeg.AutoGen, look for.
    for lib in avcodec avdevice avformat avutil swresample swscale; do
      soname=$(basename "$(ls "$prefix"/lib/lib${lib}.[0-9]*.dylib | grep -E "lib${lib}\.[0-9]+\.dylib$")")
      cp -L "$prefix/lib/$soname" "$stage/$soname"
    done
    # No dependency outside the OS and the shipped libraries.
    bad=$(otool -L "$stage"/*.dylib | grep -vE '^[^ 	]|@loader_path/|/usr/lib/libSystem|/System/Library/Frameworks/' || true)
    ;;
  windows-x64)
    cp "$prefix"/bin/*.dll "$stage/"
    # Only Windows system DLLs and our own libraries may be imported (no libgcc, winpthreads...).
    bad=$(for d in "$stage"/*.dll; do objdump -p "$d" | awk '/DLL Name:/ {print $3}'; done \
      | sort -u | grep -viE '^(av(codec|device|format|util)-[0-9]+|sw(resample|scale)-[0-9]+)\.dll$|^(api-ms-win-[a-z0-9-]+|kernel32|user32|advapi32|ole32|oleaut32|shell32|ws2_32|bcrypt|mfplat|mfuuid|mf|strmiids|ucrtbase|msvcrt|vcruntime[0-9]*|secur32|ncrypt|crypt32|gdi32|d3d11|dxgi|shlwapi|ntdll)\.dll$' || true)
    ;;
  *) echo "unknown platform: $platform" >&2; exit 2 ;;
esac
if [[ -n "${bad:-}" ]]; then
  echo "unexpected library dependencies:" >&2; echo "$bad" >&2; exit 1
fi

cp "$src/COPYING.LGPLv2.1" "$stage/licenses/COPYING.LGPLv2.1"
cp "$src/LICENSE.md" "$stage/licenses/FFMPEG-LICENSE.md"
if [[ -n "${DEPS_LICENSES:-}" && -d "$DEPS_LICENSES" ]]; then cp "$DEPS_LICENSES"/* "$stage/licenses/"; fi
tarball_url="https://ffmpeg.org/releases/ffmpeg-${version}.tar.xz"
cat > "$stage/licenses/NOTICE.txt" <<EON
This package contains FFmpeg ${version} (https://ffmpeg.org), built as shared libraries and licensed
under the GNU Lesser General Public License, version 2.1 or later (licenses/COPYING.LGPLv2.1).
It was configured with --disable-gpl and --disable-nonfree and links no GPL or nonfree component.
FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project.

The libraries are loaded dynamically by the application; to use a modified FFmpeg, replace them
with your own build of the same major version (ABI) in the same directory.

Complete corresponding source: ${tarball_url}
Exact configure arguments: licenses/BUILDINFO.txt
EON
{
  echo "ffmpeg ${version}  platform ${platform}  package revision pw${rev}"
  echo "source ${tarball_url}"
  echo "configure ./configure --prefix=<prefix> $(cat "$work/configure-args.txt")"
  if [[ -n "${DEPS_LICENSES:-}" ]]; then echo "header-only dependencies: see the other files in this directory"; fi
} > "$stage/licenses/BUILDINFO.txt"

rm -f "$out/$asset"
(cd "$stage" && zip -qr "$out/$asset" .)
cp "$work/configure-args.txt" "$out/configure-line-${platform}.txt"
(cd "$out" && { command -v sha256sum >/dev/null && sha256sum "$asset" || shasum -a 256 "$asset"; } > "SHA256SUMS-${platform}")
echo "built $asset"; cat "$out/SHA256SUMS-${platform}"
# The notices also ship next to the zip so a release page can show them without unpacking.
rm -rf "$out/licenses-${platform}"; cp -R "$stage/licenses" "$out/licenses-${platform}"
