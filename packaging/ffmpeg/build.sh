#!/usr/bin/env bash
# Builds the minimal LGPL FFmpeg shared libraries (P4.6, decision D6).
#   build.sh <macos-arm64|windows-x64> <ffmpeg-source-dir> <work-dir>
# Writes <work-dir>/prefix (installed libraries/headers) and <work-dir>/configure-args.txt (the
# exact configure arguments, minus --prefix). The Windows build runs inside an MSYS2 UCRT64 shell
# with the MinGW-w64 GCC; DEPS (optional) names a prefix holding the header-only nv-codec-headers
# and AMF headers.
set -euo pipefail

platform=${1:?platform}
src=${2:?ffmpeg source dir}
work=${3:?work dir}
mkdir -p "$work/obj"
work=$(cd "$work" && pwd)
src=$(cd "$src" && pwd)
prefix="$work/prefix"

# Nothing GPL or nonfree, nothing auto-detected from the build machine (no Homebrew/MSYS2 library
# can leak in), no programs, and only the pieces Phase 4 uses: the H.264 decoder and parser, plus
# the platform encoders below. avfilter and networking are not needed; swscale, swresample,
# avformat and avdevice are kept because SIPSorceryMedia.FFmpeg's loader calls into them.
common=(
  --disable-everything --disable-gpl --disable-nonfree --disable-autodetect
  --disable-programs --disable-doc --disable-debug
  --enable-shared --disable-static
  --disable-avfilter --disable-network
  --enable-decoder=h264 --enable-parser=h264
)

case "$platform" in
  macos-arm64)
    # @loader_path: the dylibs find each other next to themselves, wherever the app puts them.
    plat=(
      --install_name_dir=@loader_path
      --enable-pthreads
      --enable-videotoolbox --enable-encoder=h264_videotoolbox
    )
    ;;
  windows-x64)
    # h264_mf: Media Foundation, the OS encoder (software MFT always, hardware MFTs when present).
    # h264_nvenc / h264_amf: loaded from the GPU driver at run time (nvEncodeAPI64.dll / amfrt64.dll),
    # so nothing extra ships; the headers are MIT. h264_qsv is not built: it needs libvpl and its
    # dispatcher DLL shipped alongside.
    # d3d11va: FFmpeg 9.0.1 mfenc.c uses D3D11 types outside its CONFIG_D3D11VA guard, so h264_mf
    # does not compile without it (d3d11.dll is a Windows system DLL, loaded at run time).
    # w32threads, not winpthreads: avoids shipping libwinpthread-1.dll.
    plat=(
      --arch=x86_64 --target-os=mingw32
      --disable-pthreads --enable-w32threads
      --enable-mediafoundation --enable-d3d11va --enable-ffnvcodec --enable-nvenc --enable-amf
      --enable-encoder=h264_mf,h264_nvenc,h264_amf
      --extra-ldflags=-static-libgcc
    )
    ;;
  *) echo "unknown platform: $platform" >&2; exit 2 ;;
esac

args=("${common[@]}" "${plat[@]}")
printf '%s\n' "${args[*]}" > "$work/configure-args.txt"

extra=()
if [[ -n "${DEPS:-}" ]]; then
  extra+=("--extra-cflags=-I$DEPS/include")
  export PKG_CONFIG_PATH="$DEPS/lib/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
fi

cd "$work/obj"
"$src/configure" "--prefix=$prefix" "${args[@]}" ${extra[@]+"${extra[@]}"}

# Hard licence gate: the generated configuration must be plain LGPL.
for flag in GPL NONFREE VERSION3; do
  if ! grep -qE "^#define CONFIG_${flag} 0\$" config.h; then
    echo "licence gate failed: CONFIG_${flag} is not 0" >&2; exit 1
  fi
done

make -j"$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
make install
