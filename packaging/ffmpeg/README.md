# Vendored FFmpeg (P4.6, decision D6)

The app's H.264 video track (Phase 4) needs FFmpeg. Standard builds link GPL x264/x265, so the
project builds its own minimal **LGPL** FFmpeg, as shared libraries, and vendors that.

| | |
|---|---|
| FFmpeg | 9.0.1 (the major `FFmpeg.AutoGen` 9.0.1 binds to: libavcodec 63, libavdevice 63) |
| Source | https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz, SHA-256 `cf38e0e2...cb237f635` (pinned in `.github/workflows/ffmpeg.yml` and `manifest.json`) |
| Platforms | macOS arm64, Windows x64. macOS x86_64 is not built; Linux uses the JPEG fallback (D5) |
| Licence | LGPL 2.1 or later; `--disable-gpl --disable-nonfree` |

## Provenance

Everything comes from `.github/workflows/ffmpeg.yml` (manual `workflow_dispatch`, never on push),
which downloads the source tarball, checks its SHA-256, runs `build.sh` (the configure line lives
there and nowhere else) and `package.sh`. The build fails if the generated `config.h` has
`CONFIG_GPL`, `CONFIG_NONFREE` or `CONFIG_VERSION3` other than 0, if a library imports anything
outside the OS and the shipped libraries, or if `manifest.json` records a different configure line.
The encoder probe (`tests/PixelWizard.FFmpeg.Tests`, enabled by `PIXELWIZARD_FFMPEG_DIR`) then
loads the packaged zip, lists the H.264 encoders, opens one and round-trips frames.

With `publish=true` the workflow uploads the zips, `SHA256SUMS`, the configure lines and the
licence notices to a release tagged `ffmpeg-<version>-pw<N>`. Packaging and CI download the zip
by the SHA-256 in `manifest.json` (placeholders marked `TO_BE_FILLED_AFTER_FIRST_WORKFLOW_RUN`
until the first published run). Builds are not bit-reproducible, so the hash of a published asset
is recorded, not recomputed.

## What is built

Only what Phase 4 uses: the H.264 decoder and parser, one or more H.264 encoders, and the
libraries `SIPSorceryMedia.FFmpeg` 10.0.16 loads at start-up (avcodec, avformat, avdevice, avutil,
swscale, swresample; it calls `avdevice_register_all` and `swr_*`). No filters, no network, no
programs, no other codecs, no auto-detected external library (`--disable-autodetect`).

| OS | H.264 encoders | Notes |
|---|---|---|
| macOS arm64 | `h264_videotoolbox` | VideoToolbox is part of the OS |
| Windows x64 | `h264_mf` (Media Foundation: software MFT always, hardware MFTs when present), `h264_nvenc`, `h264_amf` | NVENC and AMF load `nvEncodeAPI64.dll` / `amfrt64.dll` from the GPU driver at run time and are skipped when absent; their build-time headers (nv-codec-headers, AMF) are header-only and MIT. `h264_qsv` is **not** built: it needs libvpl and its dispatcher DLL shipped. |

Windows is built with MSYS2 UCRT64 and the MinGW-w64 GCC, using Windows threads, so the DLLs
import only Windows system DLLs (no MinGW runtime to ship).

## LGPL obligations and how they are met

- **Dynamic linking.** FFmpeg ships only as separate shared libraries, loaded at run time
  (`FFmpeg.AutoGen` P/Invoke). The app never links it statically.
- **Replaceable.** A user can substitute their own build of the same FFmpeg major (same sonames) in
  the same directory.
- **Notices.** Every package carries `licenses/` with `COPYING.LGPLv2.1`, FFmpeg's `LICENSE.md`,
  `NOTICE.txt`, `BUILDINFO.txt` (version, source URL, exact configure arguments) and, on Windows,
  the MIT notices of nv-codec-headers and the AMF headers. The app's installer/About page must ship
  this directory with the libraries (P4.9).
- **Source offer.** The complete corresponding source is the pinned tarball above (exact URL and
  SHA-256 in `manifest.json`); the build scripts are in this directory.
- **No GPL or nonfree component.** Enforced by the build and by `Build_is_lgpl_and_lists_its_h264_encoders`.
- **Patents.** H.264 is patent-encumbered independent of the copyright licence. The encoders used
  here are the operating system's and GPU vendors' own, which is the premise of D5(a).

## Local build (macOS)

    bash packaging/ffmpeg/build.sh macos-arm64 <ffmpeg-9.0.1-source-dir> <work-dir>
    bash packaging/ffmpeg/package.sh macos-arm64 <ffmpeg-9.0.1-source-dir> <work-dir> <out-dir> 9.0.1 1
    PIXELWIZARD_FFMPEG_DIR=<unzipped-dir> dotnet test tests/PixelWizard.FFmpeg.Tests

macOS libraries carry `@loader_path` install names, so the libraries find each other next to
themselves. They are ad-hoc signed by the linker only; Developer ID signing and notarization belong
to packaging (P4.9).
