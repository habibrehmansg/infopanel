#!/usr/bin/env bash
# Makes the video test fixtures in InfoPanel.Core.Tests/Fixtures/video/. Run only on purpose (the files are committed and
# the tests compare decoded colours with them): it needs a full FFmpeg build with the encoders below (libx264, libx265,
# libvpx, libsvtav1), which the engine itself never uses. Development used FFmpeg 9.0.2.
#
#   native/ffmpeg/make-fixtures.sh [ffmpeg]
#
# Every clip is 64 x 48, 10 frames at 10 fps (1 s), and frame i is one solid colour:
#   red = 25 i, green = 250 - 25 i, blue = 40 for even i and 210 for odd i,
# so the colour a test reads back says which frame was shown (VideoFixtures in the tests holds the same table).
set -euo pipefail

FFMPEG="${1:-ffmpeg}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/../../InfoPanel.Core.Tests/Fixtures/video"
mkdir -p "$OUT"

SOURCE=(-f lavfi -i "color=c=black:s=64x48:r=10:d=1")
COLOURS="format=rgb24,geq=r=N*25:g=250-N*25:b=40+170*(N-2*floor(N/2))"
GLOBAL=(-hide_banner -loglevel error -y)
OUTPUT=(-map_metadata -1 -fflags +bitexact -flags:v +bitexact)

make() {
  local name="$1"; shift
  "$FFMPEG" "${GLOBAL[@]}" "${SOURCE[@]}" "${OUTPUT[@]}" -vf "$COLOURS" "$@" "$OUT/$name"
  echo "$name $(wc -c < "$OUT/$name" | tr -d ' ') bytes"
}

# One per container and codec the engine decodes. The mp4 keeps its moov after the media data (the muxer's default),
# so the probe has to skip mdat to find it; the mov has it first.
make clip-h264.mp4   -c:v libx264 -pix_fmt yuv420p -crf 8 -g 10 -bf 0 -x264-params log-level=error
make clip-h264.mov   -c:v libx264 -pix_fmt yuv420p -crf 8 -g 5 -bf 0 -x264-params log-level=error -movflags +faststart
make clip-hevc.mkv   -c:v libx265 -pix_fmt yuv420p -crf 8 -g 10 -bf 0 -x265-params log-level=error
make clip-vp8.webm   -c:v libvpx -pix_fmt yuv420p -crf 4 -b:v 1M -g 10
make clip-vp9.webm   -c:v libvpx-vp9 -pix_fmt yuv420p -crf 8 -b:v 0 -g 10
make clip-av1.webm   -c:v libsvtav1 -pix_fmt yuv420p -crf 10 -g 10 -svtav1-params log-level=0
make clip-mpeg4.avi  -c:v mpeg4 -q:v 2 -g 10 -bf 0
make clip-mjpeg.avi  -c:v mjpeg -q:v 2 -pix_fmt yuvj420p
make clip-prores.mov -c:v prores_ks -profile:v 0

# Containers the probe reads holding a codec outside the list: refused as media.codec, never decoded.
make outside-mpeg2.mkv -c:v mpeg2video -q:v 2
make outside-qtrle.mov -c:v qtrle
make outside-huffyuv.avi -c:v huffyuv

# A PNG with a video's name: the probe reads what the bytes are, not the name.
"$FFMPEG" "${GLOBAL[@]}" "${SOURCE[@]}" "${OUTPUT[@]}" -vf "$COLOURS" -frames:v 1 -f image2 -update 1 -c:v png "$OUT/png-named.mp4"
echo "png-named.mp4 $(wc -c < "$OUT/png-named.mp4" | tr -d ' ') bytes"
