#!/usr/bin/env bash
# Builds the decode-only FFmpeg the engine plays video files and RTSP cameras with, from FFmpeg's and dav1d's pinned
# release sources: libavutil, libavcodec, libavformat and libswscale as shared libraries, configured without
# --enable-gpl, --enable-nonfree and --enable-version3 (their terms: LICENSES.md), with dav1d (AV1) linked
# statically into libavcodec, and the version and checksums pinned in pins.txt beside this script.
#
#   native/ffmpeg/build.sh --target <macos-arm64|windows-x64> --out <folder> [--source <tarball>] [--dav1d-source <tarball>]
#   native/ffmpeg/build.sh --target <macos-arm64|windows-x64> --verify <folder>
#
#   --target        macos-arm64 (on an Apple silicon Mac) or windows-x64 (on Linux with mingw-w64: the container
#                   windows-cross.Dockerfile beside this script, docs/2.0/packaging.md)
#   --out           a new or empty folder; it gets bundle/ (what ships: the four libraries and the two licence texts,
#                   nothing else), source/ (the two tarballs built from), config.mak (FFmpeg's recorded configuration,
#                   for reference: the checks read the libraries themselves) and work/
#   --source        FFmpeg's release tarball (ffmpeg-<version>.tar.xz), fetched elsewhere; else it is downloaded
#   --dav1d-source  dav1d's release tarball (dav1d-<version>.tar.xz), fetched elsewhere; else it is downloaded
#   --verify        only check a folder this script built (the checks below), then stop: exit 0 passes, 1 not, 2 can't check
#   --check-args    only check the arguments, then stop (exit 0 when they are accepted)
#
# Each tarball is refused unless its SHA-256 is the pinned one, before anything is unpacked; its licence text, unless its
# own SHA-256 is pinned too. Only the demuxers, parsers and decoders the spec's video containers and codecs need
# (spec/docs/smart-nodes.md, "Video files"; the mpeg4 decoder brings h263's), and the RTSP client with the tcp protocol
# for camera streams (spec/docs/providers.md, "rtsp"; FFmpeg's rtsp demuxer brings the http, rtp and udp protocols and the
# asf, mpegts and rm demuxers with it, but the engine allows FFmpeg only rtsp and tcp, and connects it only to its own
# loopback relay); no other protocol, no device, no filter, no hardware acceleration, no external library but dav1d, and
# no program. The engine loads the libraries by full path beside itself
# (InfoPanel.Core/Images/Video/FFmpeg.cs) and accepts only the majors it pins (avutil 61, avcodec 63, avformat 63,
# swscale 10), which FFmpeg 9.0 has.
#
# macos-arm64: lib{name}.{major}.dylib, each one regular file with the install name @rpath/lib{name}.{major}.dylib and the
# rpath @loader_path (the siblings resolve from the folder they're in: Contents/MacOS in the app), built for the app's
# minimum macOS (packaging/macos/Info.plist's LSMinimumSystemVersion), signed ad hoc (packaging signs them again).
# windows-x64: {name}-{major}.dll, PE32+ x86-64, importing only Windows' own DLLs and each other (libgcc linked statically,
# Windows' own threads). Checks, after a build and with --verify, on the files themselves (never a side file): bundle/
# holds exactly the six files; both licence texts are the pinned ones; each library's own record of its build, compiled
# into it (what FFmpeg's {name}_license() and {name}_configuration() answer): its licence line "lib{name} license: ..."
# names the licence whose SHA-256 pins.txt pins (ffmpeg_license_sha256), and its configure line has none of
# --enable-gpl, --enable-nonfree and --enable-version3 and has --enable-libdav1d, the same line in all four. macOS, each
# library: arm64 only, its install name, {name}_version exported, nothing linked but macOS's own libraries and its
# siblings, the rpath @loader_path alone, built for macOS (one LC_BUILD_VERSION, platform MACOS) with a minimum macOS
# (its minos) no newer than the app's, no function imported weakly, a valid signature. Windows, each DLL: PE32+ x86-64,
# its export directory's own name the file's (so its library and major), {name}_version exported, and its imports.
# Needs meson, ninja, pkg-config, make and curl; macOS's compiler (Xcode's command line tools); for windows-x64,
# mingw-w64, the host's C compiler and nasm.
# INFOPANEL_BUILD_JOBS (a number) caps the parallel compile jobs (the processor count by default).
set -euo pipefail

usage() {
  sed -n '2,18p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

target=""
out=""
source_file=""
dav1d_file=""
verify=""
check_args=0
while [ $# -gt 0 ]; do
  case "$1" in
    --target) [ $# -ge 2 ] || usage; target="$2"; shift 2 ;;
    --out) [ $# -ge 2 ] || usage; out="$2"; shift 2 ;;
    --source) [ $# -ge 2 ] || usage; source_file="$2"; shift 2 ;;
    --dav1d-source) [ $# -ge 2 ] || usage; dav1d_file="$2"; shift 2 ;;
    --verify) [ $# -ge 2 ] || usage; verify="$2"; shift 2 ;;
    --check-args) check_args=1; shift ;;
    -h|--help) usage ;;
    *) echo "build.sh: unknown argument: $1" >&2; usage ;;
  esac
done
case "$target" in
  macos-arm64|windows-x64) ;;
  "") echo "build.sh: --target is needed: macos-arm64 or windows-x64" >&2; usage ;;
  *) echo "build.sh: --target must be macos-arm64 or windows-x64, not '$target'" >&2; exit 2 ;;
esac
if [ -n "$verify" ]; then
  [ -z "$out" ] && [ -z "$source_file" ] && [ -z "$dav1d_file" ] || { echo "build.sh: --verify takes no --out, --source or --dav1d-source" >&2; exit 2; }
  [ -d "$verify" ] || { echo "build.sh: --verify isn't a folder: $verify" >&2; exit 2; }
else
  [ -n "$out" ] || usage
  [ -z "$source_file" ] || [ -f "$source_file" ] || { echo "build.sh: --source isn't a file: $source_file" >&2; exit 2; }
  [ -z "$dav1d_file" ] || [ -f "$dav1d_file" ] || { echo "build.sh: --dav1d-source isn't a file: $dav1d_file" >&2; exit 2; }
fi
jobs="${INFOPANEL_BUILD_JOBS:-}"
[ -z "$jobs" ] || [[ "$jobs" =~ ^[1-9][0-9]{0,2}$ ]] || { echo "build.sh: INFOPANEL_BUILD_JOBS isn't a number of jobs: $jobs" >&2; exit 2; }

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pin() {
  local value
  value="$(sed -n "s/^$1=//p" "$here/pins.txt")"
  [ -n "$value" ] || { echo "build.sh: pins.txt has no $1" >&2; exit 2; }
  printf '%s' "$value"
}
version="$(pin ffmpeg_version)"
sha256="$(pin ffmpeg_source_sha256)"
copying_sha256="$(pin ffmpeg_copying_sha256)"
dav1d_version="$(pin dav1d_version)"
dav1d_sha256="$(pin dav1d_source_sha256)"
dav1d_copying_sha256="$(pin dav1d_copying_sha256)"
license_sha256="$(pin ffmpeg_license_sha256)"
for v in "$version" "$dav1d_version"; do
  [[ "$v" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "build.sh: a version in pins.txt isn't major.minor.micro: $v" >&2; exit 2; }
done
for h in "$sha256" "$copying_sha256" "$dav1d_sha256" "$dav1d_copying_sha256" "$license_sha256"; do
  [[ "$h" =~ ^[0-9a-f]{64}$ ]] || { echo "build.sh: a SHA-256 in pins.txt isn't 64 lower-case hex digits: $h" >&2; exit 2; }
done
[ "$check_args" -eq 0 ] || exit 0

# The libraries, as name:major, and the file each is on the target.
libraries=(avutil:61 avcodec:63 avformat:63 swscale:10)
file_name() {
  if [ "$target" = macos-arm64 ]; then printf 'lib%s.%s.dylib' "${1%%:*}" "${1#*:}"; else printf '%s-%s.dll' "${1%%:*}" "${1#*:}"; fi
}
siblings=()
for l in "${libraries[@]}"; do siblings+=("$(file_name "$l")"); done
cross=x86_64-w64-mingw32

case "$target" in
  macos-arm64)
    [ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ] || { echo "build.sh: --target macos-arm64 builds on an Apple silicon Mac only" >&2; exit 2; }
    tools=(cc make codesign otool lipo vtool nm plutil shasum strings)
    ;;
  windows-x64)
    [ "$(uname -s)" = Linux ] || { echo "build.sh: --target windows-x64 builds on Linux with mingw-w64 (native/ffmpeg/windows-cross.Dockerfile)" >&2; exit 2; }
    tools=("$cross-gcc" "$cross-objdump" "$cross-strings" nasm make sha256sum xz)
    ;;
esac
[ -n "$verify" ] || tools+=(meson ninja pkg-config curl tar)
for tool in "${tools[@]}"; do
  command -v "$tool" >/dev/null || { echo "build.sh: $tool is needed and isn't on PATH" >&2; exit 2; }
done
# The SHA-256 of a file, or of standard input without one.
sha() {
  if [ "$target" = macos-arm64 ]; then shasum -a 256 "$@" | cut -d' ' -f1; else sha256sum "$@" | cut -d' ' -f1; fi
}
strings_of() {
  if [ "$target" = macos-arm64 ]; then strings -a "$1"; else "$cross-strings" -a "$1"; fi
}

if [ "$target" = macos-arm64 ]; then
  plist="$here/../../packaging/macos/Info.plist"
  min_macos="$(plutil -extract LSMinimumSystemVersion raw -o - "$plist")" || { echo "build.sh: plutil can't read LSMinimumSystemVersion from $plist" >&2; exit 2; }
  [[ "$min_macos" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] || { echo "build.sh: $plist's LSMinimumSystemVersion isn't a version: $min_macos" >&2; exit 2; }
fi

# Succeeds when version $1 is newer than version $2 (dotted numbers, a missing part being 0), fails when it isn't; a
# version that isn't dotted numbers stops the script with exit 2 (native/libusb/build.sh's, which says why it's written so).
newer_than() {
  local v i
  local -a a b
  for v in "$1" "$2"; do
    [[ "$v" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] || { echo "build.sh: not a version (dotted numbers): '$v'" >&2; exit 2; }
  done
  a=(); b=()
  v="$1."; while [ -n "$v" ]; do a+=("${v%%.*}"); v="${v#*.}"; done
  v="$2."; while [ -n "$v" ]; do b+=("${v%%.*}"); v="${v#*.}"; done
  for i in 0 1 2; do
    if (( 10#${a[i]:-0} > 10#${b[i]:-0} )); then return 0; fi
    if (( 10#${a[i]:-0} < 10#${b[i]:-0} )); then return 1; fi
  done
  return 1
}

# Every inspection runs on its own, its output and exit status taken before anything is searched in it: a tool that fails
# (or prints nothing) stops with exit 2, "can't check", never reads as a pass. A check that fails returns 1.
cant() { echo "build.sh: $1 failed on $2 (or printed nothing): can't check it: refused" >&2; exit 2; }

verify_macos_library() {
  local lib="$1" name="$2" short="$3" archs names deps foreign rpaths build platform minos syms weak
  archs="$(lipo -archs "$lib")" || cant lipo "$lib"
  [ -n "$archs" ] || cant lipo "$lib"
  [ "$archs" = arm64 ] || { echo "build.sh: $lib isn't arm64 only" >&2; return 1; }
  names="$(otool -D "$lib")" || cant otool "$lib"
  [ -n "$names" ] || cant otool "$lib"
  [ "$(printf '%s\n' "$names" | tail -n 1)" = "@rpath/$name" ] || { echo "build.sh: $lib's install name isn't @rpath/$name" >&2; return 1; }
  deps="$(otool -L "$lib")" || cant otool "$lib"
  [ -n "$deps" ] || cant otool "$lib"
  foreign="$(printf '%s\n' "$deps" | tail -n +3 | awk -v sibs="${siblings[*]}" '
    BEGIN { n = split(sibs, s, " "); for (i = 1; i <= n; i++) ok["@rpath/" s[i]] = 1 }
    $1 ~ /^(\/usr\/lib\/|\/System\/Library\/)/ { next }
    ($1 in ok) { next }
    { print }')" || cant awk "$lib"
  if [ -n "$foreign" ]; then
    printf '%s\n' "$foreign"
    echo "build.sh: $lib links something that isn't macOS's own or a sibling (above)" >&2
    return 1
  fi
  rpaths="$(otool -l "$lib")" || cant otool "$lib"
  [ -n "$rpaths" ] || cant otool "$lib"
  rpaths="$(printf '%s\n' "$rpaths" | awk '$1 == "cmd" && $2 == "LC_RPATH" { r = 1; next } r && $1 == "path" { print $2; r = 0 }')" || cant awk "$lib"
  [ "$rpaths" = "@loader_path" ] || { echo "build.sh: $lib's rpaths aren't @loader_path alone: ${rpaths:-none}" >&2; return 1; }
  build="$(vtool -show-build "$lib")" || cant vtool "$lib"
  [ -n "$build" ] || cant vtool "$lib"
  # One build version load command (an older LC_VERSION_MIN_* or a second platform counts too), naming macOS: an iOS or
  # simulator library of the same architecture would pass every other check and fail to load.
  platform="$(printf '%s\n' "$build" | awk '$1 == "cmd" { n++; c = $2 } $1 == "platform" { p = $2 }
    END { if (n == 1 && c == "LC_BUILD_VERSION") print p }')" || cant awk "$lib"
  [ "$platform" = MACOS ] || { echo "build.sh: $lib isn't built for macOS alone (LC_BUILD_VERSION's platform: ${platform:-not one LC_BUILD_VERSION}): refused" >&2; return 1; }
  minos="$(printf '%s\n' "$build" | awk '$1 == "minos" { print $2 }')" || cant awk "$lib"
  [[ "$minos" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] || { echo "build.sh: $lib names no minimum macOS (LC_BUILD_VERSION's minos)" >&2; return 1; }
  if newer_than "$minos" "$min_macos"; then
    echo "build.sh: $lib needs macOS $minos, newer than the app's minimum $min_macos (LSMinimumSystemVersion): refused" >&2
    return 1
  fi
  syms="$(nm -m "$lib")" || cant nm "$lib"
  [ -n "$syms" ] || cant nm "$lib"
  printf '%s\n' "$syms" | awk -v s="_${short}_version" '$NF == s && /\(__TEXT,__text\) external/ { f = 1 } END { exit !f }' || {
    echo "build.sh: $lib doesn't export ${short}_version: it isn't lib$short" >&2
    return 1
  }
  weak="$(printf '%s\n' "$syms" | awk '/\(undefined\) weak external/')" || cant awk "$lib"
  if [ -n "$weak" ]; then
    printf '%s\n' "$weak"
    echo "build.sh: $lib imports something newer than macOS $min_macos (weakly, above): refused" >&2
    return 1
  fi
  codesign --verify --strict "$lib" || { echo "build.sh: $lib's signature isn't valid (or codesign failed)" >&2; return 1; }
  echo "$lib: arm64, @rpath/$name, exports ${short}_version, rpath @loader_path, platform $platform, macOS $minos or later (the app's minimum: $min_macos)"
}

# Windows' own DLLs a library may import (lower case), besides its siblings.
windows_dlls="kernel32.dll msvcrt.dll bcrypt.dll user32.dll advapi32.dll ws2_32.dll ole32.dll shell32.dll secur32.dll"
verify_windows_library() {
  local lib="$1" name="$2" short="$3" header dump own imports foreign
  header="$("$cross-objdump" -f "$lib")" || cant objdump "$lib"
  [ -n "$header" ] || cant objdump "$lib"
  printf '%s\n' "$header" | grep -q 'file format pei-x86-64' || { echo "build.sh: $lib isn't PE32+ x86-64" >&2; return 1; }
  dump="$("$cross-objdump" -p "$lib")" || cant objdump "$lib"
  [ -n "$dump" ] || cant objdump "$lib"
  # The name the DLL was linked as, in its export directory, which renaming the file doesn't change: it says which
  # library and which major this is.
  own="$(printf '%s\n' "$dump" | awk '/^The Export Tables/ { e = 1; next } e && $1 == "Name" && NF == 3 && !done { print $3; done = 1 }')" || cant awk "$lib"
  [ "$own" = "$name" ] || { echo "build.sh: $lib was linked as ${own:-nothing (no export directory)}, not $name: refused" >&2; return 1; }
  printf '%s\n' "$dump" | awk -v s="${short}_version" '/^\[Ordinal\/Name Pointer\] Table/ { t = 1; next } t && NF == 0 { t = 0 }
    t && $1 ~ /^\[/ && $NF == s { f = 1 } END { exit !f }' || {
    echo "build.sh: $lib doesn't export ${short}_version: it isn't $short" >&2
    return 1
  }
  imports="$(printf '%s\n' "$dump" | awk '$1 == "DLL" && $2 == "Name:" { print $3 }')" || cant awk "$lib"
  [ -n "$imports" ] || { echo "build.sh: $lib imports nothing: can't check it: refused" >&2; exit 2; }
  foreign="$(printf '%s\n' "$imports" | awk -v own="$windows_dlls" -v sibs="${siblings[*]}" '
    BEGIN { n = split(own " " sibs, s, " "); for (i = 1; i <= n; i++) ok[tolower(s[i])] = 1 }
    !(tolower($1) in ok) { print }')" || cant awk "$lib"
  if [ -n "$foreign" ]; then
    printf '%s\n' "$foreign"
    echo "build.sh: $lib imports a DLL that isn't Windows' own or a sibling (above)" >&2
    return 1
  fi
  echo "$lib: PE32+ x86-64, linked as $own, exports ${short}_version, imports $(printf '%s\n' "$imports" | tr '\n' ' ' | sed 's/ $//')"
}

# What a library says of its own build, compiled into it (its {name}_license() and {name}_configuration()): the licence
# line, whose licence's SHA-256 must be the pinned one, and the configure line, kept in $record_configure for the
# comparison of all four.
record_configure=""
verify_build_record() {
  local lib="$1" short="$2" text licence count flag
  text="$(strings_of "$lib")" || cant strings "$lib"
  [ -n "$text" ] || cant strings "$lib"
  # Each string read from its marker to its end (a string before it whose array has no terminating zero runs into it),
  # and counted once however often the library holds it: one licence and one configure line, never two.
  count="$(printf '%s\n' "$text" | awk -v p="lib$short license: " 'i = index($0, p) { s = substr($0, i); if (!seen[s]++) n++ } END { print n + 0 }')" || cant awk "$lib"
  [ "$count" -eq 1 ] || { echo "build.sh: $lib holds $count licence lines \"lib$short license: ...\", not one: refused" >&2; return 1; }
  licence="$(printf '%s\n' "$text" | awk -v p="lib$short license: " 'i = index($0, p) { if (!done) print substr($0, i + length(p)); done = 1 }')" || cant awk "$lib"
  [ "$(printf '%s' "$licence" | sha)" = "$license_sha256" ] || {
    echo "build.sh: $lib was built under \"$licence\", not the licence pins.txt pins (LICENSES.md): refused" >&2
    return 1
  }
  # The configure line is read whole, from its first "--" (a flag before --prefix= is part of it; a table running into its
  # start has no "--"), so the comparison of the four and the flag checks see all of it.
  count="$(printf '%s\n' "$text" | awk 'index($0, "--prefix=") { s = substr($0, index($0, "--")); if (!seen[s]++) n++ } END { print n + 0 }')" || cant awk "$lib"
  [ "$count" -eq 1 ] || { echo "build.sh: $lib holds $count configure lines (\"--prefix=...\"), not one: refused" >&2; return 1; }
  record_configure="$(printf '%s\n' "$text" | awk 'index($0, "--prefix=") { if (!done) print substr($0, index($0, "--")); done = 1 }')" || cant awk "$lib"
  for flag in --enable-gpl --enable-nonfree --enable-version3; do
    case " $record_configure " in
      *" $flag "*) echo "build.sh: $lib was configured with $flag: refused" >&2; return 1 ;;
    esac
  done
  case " $record_configure " in
    *" --enable-libdav1d "*) ;;
    *) echo "build.sh: $lib wasn't configured with --enable-libdav1d (AV1): refused" >&2; return 1 ;;
  esac
  echo "$lib: lib$short, its licence the pinned one, configured without --enable-gpl, --enable-nonfree and --enable-version3, with dav1d"
}

verify_output() {
  local dir="$1" bundle="$1/bundle" entry name short l failed=0 count=0 configure="" first=""
  [ -d "$bundle" ] || { echo "build.sh: $dir has no bundle/ folder" >&2; return 1; }
  for entry in "$bundle"/* "$bundle"/.[!.]*; do
    [ -e "$entry" ] || [ -L "$entry" ] || continue
    name="$(basename "$entry")"
    count=$((count + 1))
    [ -f "$entry" ] && [ ! -L "$entry" ] || { echo "build.sh: $entry isn't a regular file" >&2; failed=1; continue; }
    case " ${siblings[*]} ffmpeg-COPYING.txt dav1d-COPYING.txt " in
      *" $name "*) ;;
      *) echo "build.sh: $bundle holds $name, which doesn't ship" >&2; failed=1 ;;
    esac
  done
  [ "$count" -eq 6 ] || { echo "build.sh: $bundle holds $count entries, not the four libraries and the two licence texts" >&2; failed=1; }
  [ -f "$bundle/ffmpeg-COPYING.txt" ] && [ "$(sha "$bundle/ffmpeg-COPYING.txt")" = "$copying_sha256" ] || { echo "build.sh: $bundle/ffmpeg-COPYING.txt isn't the pinned licence text" >&2; failed=1; }
  [ -f "$bundle/dav1d-COPYING.txt" ] && [ "$(sha "$bundle/dav1d-COPYING.txt")" = "$dav1d_copying_sha256" ] || { echo "build.sh: $bundle/dav1d-COPYING.txt isn't the pinned licence text" >&2; failed=1; }
  for l in "${libraries[@]}"; do
    name="$(file_name "$l")"
    short="${l%%:*}"
    if [ ! -f "$bundle/$name" ]; then
      echo "build.sh: $bundle has no $name" >&2; failed=1; continue
    elif [ "$target" = macos-arm64 ]; then
      verify_macos_library "$bundle/$name" "$name" "$short" || failed=1
    else
      verify_windows_library "$bundle/$name" "$name" "$short" || failed=1
    fi
    record_configure=""
    verify_build_record "$bundle/$name" "$short" || failed=1
    # The four were built by one configure: a library from another build (another licence, other components) differs.
    if [ -n "$record_configure" ]; then
      if [ -z "$first" ]; then
        first="$name"; configure="$record_configure"
      elif [ "$record_configure" != "$configure" ]; then
        echo "build.sh: $bundle/$name was configured otherwise than $first: refused" >&2; failed=1
      fi
    fi
  done
  [ "$failed" -eq 0 ] || { echo "build.sh: $dir doesn't pass (above)" >&2; return 1; }
  echo "$dir: passes ($target: ${siblings[*]}, the two licence texts; each library under the pinned licence, from one configure without --enable-gpl, --enable-nonfree and --enable-version3; dav1d inside)"
}

if [ -n "$verify" ]; then
  verify_output "$verify"
  exit 0
fi

mkdir -p "$out"
out="$(cd "$out" && pwd)"
if [ -n "$(ls -A "$out")" ]; then
  echo "build.sh: --out must be a new or empty folder: $out" >&2
  exit 2
fi
[ -n "$jobs" ] || jobs="$(getconf _NPROCESSORS_ONLN)"
work="$out/work"
mkdir "$work" "$out/source" "$out/bundle"

# Fetches (or copies) a tarball into source/ and refuses it unless its SHA-256 is the pinned one, before it is unpacked.
fetch() {
  local given="$1" url="$2" pinned="$3" file="$out/source/$(basename "$2")" actual
  if [ -n "$given" ]; then
    cp "$given" "$file"
  else
    curl --fail --location --silent --show-error --proto '=https' "$url" -o "$file"
  fi
  actual="$(sha "$file")"
  if [ "$actual" != "$pinned" ]; then
    echo "build.sh: $file's SHA-256 is $actual, not the pinned $pinned: refused" >&2
    exit 1
  fi
  echo "$file: SHA-256 $actual (pinned)"
}
fetch "$source_file" "https://ffmpeg.org/releases/ffmpeg-$version.tar.xz" "$sha256"
fetch "$dav1d_file" "https://downloads.videolan.org/pub/videolan/dav1d/$dav1d_version/dav1d-$dav1d_version.tar.xz" "$dav1d_sha256"
tar -xJf "$out/source/ffmpeg-$version.tar.xz" -C "$work"
tar -xJf "$out/source/dav1d-$dav1d_version.tar.xz" -C "$work"
src="$work/ffmpeg-$version"
dav1d_src="$work/dav1d-$dav1d_version"
[ "$(cat "$src/RELEASE")" = "$version" ] || { echo "build.sh: the tarball's ffmpeg-$version/RELEASE isn't $version" >&2; exit 1; }
[ -f "$dav1d_src/meson.build" ] || { echo "build.sh: the tarball has no dav1d-$dav1d_version/meson.build" >&2; exit 1; }
# FFmpeg's source holds a licence text for each licence it can be built under: the one this build is under is the
# COPYING.* whose SHA-256 is pinned (LICENSES.md names it).
ffmpeg_copying=""
for f in "$src"/COPYING.*; do
  if [ -f "$f" ] && [ "$(sha "$f")" = "$copying_sha256" ]; then ffmpeg_copying="$f"; fi
done
[ -n "$ffmpeg_copying" ] || { echo "build.sh: no ffmpeg-$version/COPYING.* is the pinned licence text: refused" >&2; exit 1; }
[ "$(sha "$dav1d_src/COPYING")" = "$dav1d_copying_sha256" ] || { echo "build.sh: dav1d-$dav1d_version/COPYING isn't the pinned licence text: refused" >&2; exit 1; }

# dav1d, static, for libavcodec alone: its library and headers into work/dav1d, its tools, tests and examples off.
dav1d_prefix="$work/dav1d"
meson_args=(--prefix="$dav1d_prefix" --libdir=lib --buildtype=release --default-library=static
  -Denable_tools=false -Denable_tests=false -Denable_examples=false -Denable_docs=false)
if [ "$target" = macos-arm64 ]; then
  export MACOSX_DEPLOYMENT_TARGET="$min_macos"
  CFLAGS="-arch arm64 -mmacosx-version-min=$min_macos" LDFLAGS="-arch arm64 -mmacosx-version-min=$min_macos" \
    meson setup "${meson_args[@]}" "$work/dav1d-build" "$dav1d_src"
else
  cat > "$work/meson-cross.txt" <<EOF
[binaries]
c = '$cross-gcc'
ar = '$cross-ar'
strip = '$cross-strip'
windres = '$cross-windres'

[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF
  meson setup "${meson_args[@]}" --cross-file "$work/meson-cross.txt" "$work/dav1d-build" "$dav1d_src"
fi
ninja -C "$work/dav1d-build" -j "$jobs"
ninja -C "$work/dav1d-build" install

# FFmpeg: everything off, then only what the engine uses on. Only dav1d's pkg-config file is seen (PKG_CONFIG_LIBDIR), so
# no other dav1d (Homebrew's, the system's) can be linked; --static takes its private libraries, linking it in.
components=(
  --enable-demuxer=mov,matroska,avi,rtsp
  --enable-protocol=tcp
  --enable-decoder=h264,hevc,vp8,vp9,mpeg4,mjpeg,prores,libdav1d
  --enable-parser=h264,hevc,vp8,vp9,mpeg4video,mjpeg,av1
  --enable-bsf=vp9_superframe_split
)
if [ "$target" = macos-arm64 ]; then
  platform=(--enable-pthreads --install-name-dir=@rpath
    --extra-cflags="-mmacosx-version-min=$min_macos"
    --extra-ldflags="-mmacosx-version-min=$min_macos"
    --extra-ldsoflags="-Wl,-rpath,@loader_path")
else
  platform=(--enable-cross-compile --target-os=mingw32 --arch=x86_64 --cross-prefix="$cross-"
    --enable-w32threads --extra-ldflags=-static-libgcc)
fi
cd "$src"
PKG_CONFIG_LIBDIR="$dav1d_prefix/lib/pkgconfig" PKG_CONFIG_PATH="" ./configure \
  --prefix="$work/ffmpeg" \
  --pkg-config=pkg-config --pkg-config-flags=--static \
  --enable-shared --disable-static --enable-pic \
  --disable-everything --disable-autodetect \
  --disable-programs --disable-doc --disable-debug \
  --disable-avdevice --disable-avfilter --disable-swresample \
  --enable-avutil --enable-avcodec --enable-avformat --enable-swscale \
  "${components[@]}" \
  --enable-libdav1d \
  "${platform[@]}"
make -j"$jobs"
make install
cp ffbuild/config.mak "$out/config.mak"

# What ships: one regular file a library under the name the engine loads (make install's are links to a versioned file
# on macOS), and the two licence texts.
for l in "${libraries[@]}"; do
  name="$(file_name "$l")"
  if [ "$target" = macos-arm64 ]; then
    cp -L "$work/ffmpeg/lib/$name" "$out/bundle/$name"
    codesign --force --sign - --timestamp=none "$out/bundle/$name"
  else
    cp -L "$work/ffmpeg/bin/$name" "$out/bundle/$name"
  fi
done
cp "$ffmpeg_copying" "$out/bundle/ffmpeg-COPYING.txt"
cp "$dav1d_src/COPYING" "$out/bundle/dav1d-COPYING.txt"

verify_output "$out"
du -k "$out"/bundle/*
echo "Built $out/bundle (FFmpeg $version with dav1d $dav1d_version, $target) from $out/source"
