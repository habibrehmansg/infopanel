# The Linux container native/ffmpeg/build.sh --target windows-x64 runs in: Ubuntu 24.04 for x86-64, pinned by its image
# index's digest, with mingw-w64 (x86-64), the host's C compiler (FFmpeg builds a few tools it runs), nasm (FFmpeg's and
# dav1d's x86 assembly), meson, ninja, pkgconf, make, curl and xz. Every one of them comes from Ubuntu's archive as it was
# at one moment (snapshot.ubuntu.com, the timestamp below), so a rebuild installs the same compilers, linkers and tools;
# meson is named by its version too, and a snapshot without that version stops the build. Only ca-certificates comes
# first from the archive as it is, to reach the snapshot over HTTPS (apt checks every index against Ubuntu's archive key
# either way). The snapshot service has Ubuntu's x86-64 archive, not the ports, so the image is x86-64 everywhere: the
# release workflow's runner runs it natively, a Mac with Apple silicon through Docker's emulation (slower, the same
# toolchain). The script is run from the mounted repository, never copied in (docs/2.0/packaging.md):
#
#   docker build -f native/ffmpeg/windows-cross.Dockerfile -t infopanel-ffmpeg-cross native/ffmpeg
#   docker run --rm -v "$PWD:/src:ro" -v "<out>:/out" infopanel-ffmpeg-cross \
#     /src/native/ffmpeg/build.sh --target windows-x64 --out /out
FROM --platform=linux/amd64 ubuntu:24.04@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && sed -i 's#http://[a-z]*\.ubuntu\.com/ubuntu/#https://snapshot.ubuntu.com/ubuntu/20261101T000000Z/#' /etc/apt/sources.list.d/ubuntu.sources \
 && ! grep -q '^URIs: http://' /etc/apt/sources.list.d/ubuntu.sources \
 && apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64 gcc libc6-dev nasm meson=1.3.2-1ubuntu1 ninja-build pkgconf make \
      curl xz-utils \
 && rm -rf /var/lib/apt/lists/*
