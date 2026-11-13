# Video: the live preview, video files and cameras

Two different things use video, and they need different FFmpeg: the live preview a client watches can come as H.264 video, which the engine encodes ([below](#the-live-preview-as-video-and-ffmpeg)); and a panel can show video files and RTSP camera streams, which the engine decodes with FFmpeg's libraries (HTTP MJPEG cameras need none) ([below](#video-files-and-cameras-ffmpegs-libraries)).

## The live preview as video, and ffmpeg

A client can ask for the live preview as H.264 video instead of PNG images. The engine encodes it with the computer's hardware encoder where it has one: VideoToolbox on macOS, and on Windows Media Foundation, the graphics chip's own encoder (AMD, Intel or NVIDIA), checked with a short test encode the first time a preview asks for video. Windows uses its graphics chip's encoder only: a PC without a working one keeps the preview as images. So may a PC whose graphics chip drives no display (no monitor and no Remote Desktop session, as on a headless PC that drives only USB panels): Windows then makes a software adapter the default, and the chip's encoder can refuse to start there. A monitor, an HDMI dummy plug or a Remote Desktop session brings video back. On macOS and Linux the engine falls back to ffmpeg, if ffmpeg is installed: the engine runs the ffmpeg it finds as a separate program and doesn't include one. Without either, the preview stays images. To use ffmpeg, install a build with libx264 (the usual packages have it):

- **macOS:** `brew install ffmpeg` ([Homebrew](https://brew.sh)).
- **Linux, Raspberry Pi OS:** `sudo apt install ffmpeg` (or your distribution's package).

The engine looks for ffmpeg once, the first time a preview asks for video: at the path given with `--ffmpeg <path>` if there is one, otherwise on the `PATH` the engine was started with, then in `/opt/homebrew/bin` and `/usr/local/bin`, where an app started from the Dock or Finder doesn't look. It checks that ffmpeg really encodes with a tiny test before using it, and logs which ffmpeg it found and its version. `--h264 software` uses ffmpeg even where there is a hardware encoder (on Windows, which has no software encoder, it leaves the preview as images; `--ffmpeg` is refused there), and `--h264 off` turns video off.

## Video files and cameras: FFmpeg's libraries

A panel plays video files ([video files](../../spec/docs/smart-nodes.md#video-files)) and shows RTSP camera streams ([camera streams](../../spec/docs/providers.md#rtsp-camera-streams)) with FFmpeg's libraries: libavutil 61, libavcodec 63, libavformat 63 and libswscale 10, which FFmpeg 9.0 has. This is not the ffmpeg program above: the engine loads the libraries itself, beside the engine first, else from the folder the `INFOPANEL_FFMPEG_DIR` environment variable names. When it finds them the session lists `media.video` (and the `rtsp` provider); without them it logs it once, and a video item isn't played but stays in the profile.

The Mac app carries them beside the engine ([packaging](packaging.md)); the Windows installer doesn't yet, and a build from source carries none. `native/ffmpeg/build.sh` builds them from FFmpeg's and dav1d's pinned release sources: a decode-only FFmpeg 9.0.2 with dav1d 1.5.4 for AV1 inside libavcodec, only the containers and codecs the formats name and the RTSP client, without `--enable-gpl`, `--enable-nonfree` and `--enable-version3` (its header says what it needs and what it checks). Put them beside the engine, or point `INFOPANEL_FFMPEG_DIR` at their folder (`<out>/bundle`).
