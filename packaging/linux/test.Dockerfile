# The Linux test image: builds and tests the engine on Linux, as an ordinary user (uid 1000).
# The source is never copied in: mount a copy of the repository without bin/ and obj/ at /src (a Linux build in a
# macOS or Windows checkout would overwrite its obj/project.assets.json), and a NuGet cache at /nuget.
#
#   docker build -f packaging/linux/test.Dockerfile -t infopanel-linux-test packaging/linux
#   docker run --rm --user 1000:1000 -v <copy>:/src -v <nuget cache>:/nuget infopanel-linux-test \
#     sh -c 'dotnet build InfoPanel.slnx && dotnet test InfoPanel.slnx --no-build --blame-hang-timeout 5m'
#
# The SDK image is pinned by digest (the multi-architecture index, so linux/arm64 and linux/amd64 both resolve); its
# SDK is global.json's version. Bump the digest on purpose, then regenerate the Linux goldens in the new image.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e

# What the engine needs from the system on Linux: fontconfig (Skia's font manager), one font family (the system fonts
# the engine lists beside its embedded ones; DejaVu, as on most desktops), libusb (USB panels). And D-Bus, only for the
# Now Playing check: a session bus a test's fake media player and the engine share (docs/agents/linux.md).
RUN apt-get update \
 && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core libusb-1.0-0 dbus \
 && rm -rf /var/lib/apt/lists/*

# An ordinary user at uid 1000 (root ignores file modes, so tests of private files would pass for the wrong reason).
# Ubuntu's image already has one; any other base gets its own.
RUN id -u 1000 >/dev/null 2>&1 || useradd --uid 1000 --user-group --create-home infopanel

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_PACKAGES=/nuget

USER 1000:1000
WORKDIR /src

# The SDK image sets ASPNETCORE_HTTP_PORTS for apps it would host. The test host hands it to the engine as a command-line
# setting (--HTTP_PORTS=…), which the engine refuses (it listens on 127.0.0.1 and its --port only), so every command
# runs without it. Docker can't unset a variable an image sets, and an empty value is still passed on.
ENTRYPOINT ["env", "-u", "ASPNETCORE_HTTP_PORTS"]
CMD ["dotnet", "--info"]
