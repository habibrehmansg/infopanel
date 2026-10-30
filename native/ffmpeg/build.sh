#!/usr/bin/env bash
# Builds the decode-only FFmpeg the engine plays videos with: libavutil, libavcodec, libavformat and libswscale as
# shared libraries, configured without --enable-gpl and --enable-nonfree (LICENSES.md), with only the demuxers, parsers and
# decoders the spec's video containers and codecs need (spec/docs/smart-nodes.md, "Video files"), no protocol, no
# device, no filter, no hardware acceleration and no program. The engine loads these by full path beside itself
# (InfoPanel.Core/Images/Video/FFmpeg.cs) and accepts only the major versions it pins (avutil 61, avcodec 63,
# avformat 63, swscale 10), which FFmpeg 9.0 has.
#
#   native/ffmpeg/build.sh <ffmpeg-9.0.2 source folder> <dav1d prefix> <output folder>
#
# The source is FFmpeg's release tarball (https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz), checked against the
# release's signature by whoever fetches it; dav1d is built and installed first, its prefix given here. The
# output folder gets lib/ with the shared libraries (macOS: @rpath install names; Linux: an $ORIGIN rpath; Windows:
# run under MSYS2 with a MinGW toolchain, DLLs in bin/), and the configuration FFmpeg recorded.
set -euo pipefail

VERSION="9.0.2"
SRC="${1:?the FFmpeg source folder}"
DAV1D="${2:?the dav1d install prefix}"
OUT="${3:?the output folder}"

if [ "$(cat "$SRC/RELEASE" 2>/dev/null)" != "$VERSION" ]; then
  echo "build.sh: $SRC is not FFmpeg $VERSION" >&2
  exit 2
fi

DEMUXERS="mov,matroska,avi"
DECODERS="h264,hevc,vp8,vp9,mpeg4,mjpeg,prores,libdav1d"
PARSERS="h264,hevc,vp8,vp9,mpeg4video,mjpeg,av1"
BSFS="vp9_superframe_split"

PLATFORM=()
case "$(uname -s)" in
  Darwin) PLATFORM=(--install-name-dir=@rpath) ;;
  Linux) PLATFORM=(--extra-ldflags='-Wl,-rpath,$$ORIGIN') ;;
esac

cd "$SRC"
PKG_CONFIG_PATH="$DAV1D/lib/pkgconfig:${PKG_CONFIG_PATH:-}" ./configure \
  --prefix="$OUT" \
  --enable-shared --disable-static --enable-pic \
  --disable-everything --disable-autodetect \
  --disable-programs --disable-doc --disable-network --disable-debug \
  --disable-avdevice --disable-avfilter --disable-swresample \
  --enable-avutil --enable-avcodec --enable-avformat --enable-swscale \
  --enable-demuxer="$DEMUXERS" \
  --enable-decoder="$DECODERS" \
  --enable-parser="$PARSERS" \
  --enable-bsf="$BSFS" \
  --enable-libdav1d \
  "${PLATFORM[@]}"
make -j"$(getconf _NPROCESSORS_ONLN)"
make install

# What a release ships: the configuration, for the notices, and the libraries' sizes.
mkdir -p "$OUT/share/ffmpeg"
cp ffbuild/config.mak "$OUT/share/ffmpeg/config.mak"
du -k "$OUT"/lib/* "$OUT"/bin/*.dll 2>/dev/null || true
