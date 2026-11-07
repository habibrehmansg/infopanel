#!/usr/bin/env bash
# Builds the libusb-1.0 the macOS app ships (USB panels, docs/2.0/panels.md): libusb-1.0.dylib for arm64, from libusb's
# pinned release source, as one regular file whose install name is @rpath/libusb-1.0.dylib (the app's layout, like
# FFmpeg's libraries: packaging/macos/build.sh moves it to Contents/MacOS, where the engine loads it by full path from
# beside itself, InfoPanel.Core/Panels/Usb/LibUsb.cs). It links only macOS's own IOKit, CoreFoundation, Security and
# libSystem. The version and the checksums are pinned in pins.txt beside this script.
#
#   native/libusb/build.sh --out <folder> [--source <libusb-<version>.tar.bz2>]
#   native/libusb/build.sh --verify <libusb-1.0.dylib>
#
#   --out         a new or empty folder; it gets libusb-1.0.dylib and libusb-COPYING.txt (libusb's licence text)
#   --source      the release's tarball, when it was fetched elsewhere; else it is downloaded from
#                 https://github.com/libusb/libusb/releases/download/v<version>/libusb-<version>.tar.bz2
#   --verify      only check a library this script built (the checks below), then stop: exit 0 passes, 1 not, 2 can't check
#   --check-args  only check the arguments, then stop (exit 0 when they are accepted)
#
# The tarball is refused unless its SHA-256 is the pinned one, before anything is unpacked; its COPYING, unless its own
# SHA-256 is pinned too. The library is built for the app's minimum macOS (MACOSX_DEPLOYMENT_TARGET, for the compile and
# the link, is packaging/macos/Info.plist's LSMinimumSystemVersion), not the building Mac's. Checks, after a build and
# with --verify: arm64 only, the install name, nothing linked but macOS's own libraries, a minimum macOS (its
# LC_BUILD_VERSION's minos) no newer than the app's, no function imported weakly (one newer than that minimum), a valid
# signature. Needs macOS's compiler (Xcode's command line
# tools), make and curl; the release tarball carries its own configure. The library is signed ad hoc (arm64 refuses
# unsigned code); packaging signs it again with the rest of the app.
set -euo pipefail

usage() {
  sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

out=""
source_file=""
verify=""
check_args=0
while [ $# -gt 0 ]; do
  case "$1" in
    --out) [ $# -ge 2 ] || usage; out="$2"; shift 2 ;;
    --source) [ $# -ge 2 ] || usage; source_file="$2"; shift 2 ;;
    --verify) [ $# -ge 2 ] || usage; verify="$2"; shift 2 ;;
    --check-args) check_args=1; shift ;;
    -h|--help) usage ;;
    *) echo "build.sh: unknown argument: $1" >&2; usage ;;
  esac
done
if [ -n "$verify" ]; then
  [ -z "$out" ] && [ -z "$source_file" ] || { echo "build.sh: --verify takes no --out or --source" >&2; exit 2; }
  [ -f "$verify" ] || { echo "build.sh: --verify isn't a file: $verify" >&2; exit 2; }
else
  [ -n "$out" ] || usage
  [ -z "$source_file" ] || [ -f "$source_file" ] || { echo "build.sh: --source isn't a file: $source_file" >&2; exit 2; }
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
plist="$here/../../packaging/macos/Info.plist"
pin() {
  local value
  value="$(sed -n "s/^$1=//p" "$here/pins.txt")"
  [ -n "$value" ] || { echo "build.sh: pins.txt has no $1" >&2; exit 2; }
  printf '%s' "$value"
}
version="$(pin version)"
sha256="$(pin source_sha256)"
copying_sha256="$(pin source_copying_sha256)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "build.sh: pins.txt's version isn't major.minor.micro: $version" >&2; exit 2; }
[[ "$sha256" =~ ^[0-9a-f]{64}$ ]] || { echo "build.sh: pins.txt's source_sha256 isn't 64 lower-case hex digits" >&2; exit 2; }
[[ "$copying_sha256" =~ ^[0-9a-f]{64}$ ]] || { echo "build.sh: pins.txt's source_copying_sha256 isn't 64 lower-case hex digits" >&2; exit 2; }
[ "$check_args" -eq 0 ] || exit 0
if [ "$(uname -s)" != "Darwin" ]; then
  echo "build.sh: macOS only (Linux uses the system's libusb, Windows takes the release's DLL)" >&2
  exit 2
fi

# The app's minimum macOS, which the library is built for and checked against.
min_macos="$(plutil -extract LSMinimumSystemVersion raw -o - "$plist")" || { echo "build.sh: plutil can't read LSMinimumSystemVersion from $plist" >&2; exit 2; }
[[ "$min_macos" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] || { echo "build.sh: $plist's LSMinimumSystemVersion isn't a version: $min_macos" >&2; exit 2; }

# Succeeds when version $1 is newer than version $2 (dotted numbers, a missing part being 0), fails when it isn't. A
# version that isn't dotted numbers (empty or non-numeric parts included) stops the script with exit 2: that is not "not
# newer". The parts are split by parameter expansion: here-strings need temporary files in macOS's bash 3.2, and a
# folder they can't be made in must not turn every part into 0.
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

# The checks a library this script built passes. Every inspection runs on its own, its output and exit status taken
# before anything is searched in it: a tool that fails (or prints nothing) stops with exit 2, "can't check", never
# reads as a pass. A check that fails returns 1.
verify_library() {
  local lib="$1" archs names deps foreign build minos syms weak
  cant() { echo "build.sh: $1 failed on $lib (or printed nothing): can't check it: refused" >&2; exit 2; }
  archs="$(lipo -archs "$lib")" || cant lipo
  [ -n "$archs" ] || cant lipo
  [ "$archs" = "arm64" ] || { echo "build.sh: $lib isn't arm64 only" >&2; return 1; }
  names="$(otool -D "$lib")" || cant otool
  [ -n "$names" ] || cant otool
  [ "$(printf '%s\n' "$names" | tail -n 1)" = "@rpath/libusb-1.0.dylib" ] || { echo "build.sh: $lib's install name isn't @rpath/libusb-1.0.dylib" >&2; return 1; }
  deps="$(otool -L "$lib")" || cant otool
  [ -n "$deps" ] || cant otool
  foreign="$(printf '%s\n' "$deps" | tail -n +3 | awk '$0 !~ /^[ \t]+(\/usr\/lib\/|\/System\/Library\/)/')" || cant awk
  if [ -n "$foreign" ]; then
    printf '%s\n' "$foreign"
    echo "build.sh: $lib links something that isn't macOS's own (above)" >&2
    return 1
  fi
  build="$(vtool -show-build "$lib")" || cant vtool
  [ -n "$build" ] || cant vtool
  minos="$(printf '%s\n' "$build" | awk '$1 == "minos" { print $2 }')" || cant awk
  [[ "$minos" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] || { echo "build.sh: $lib names no minimum macOS (LC_BUILD_VERSION's minos)" >&2; return 1; }
  if newer_than "$minos" "$min_macos"; then
    echo "build.sh: $lib needs macOS $minos, newer than the app's minimum $min_macos (LSMinimumSystemVersion): refused" >&2
    return 1
  fi
  # A function newer than the minimum is imported weakly, and is missing (null) on an older macOS.
  syms="$(nm -m "$lib")" || cant nm
  [ -n "$syms" ] || cant nm
  weak="$(printf '%s\n' "$syms" | awk '/\(undefined\) weak external/')" || cant awk
  if [ -n "$weak" ]; then
    printf '%s\n' "$weak"
    echo "build.sh: $lib imports something newer than macOS $min_macos (weakly, above): refused" >&2
    return 1
  fi
  codesign --verify --strict "$lib" || { echo "build.sh: $lib's signature isn't valid (or codesign failed)" >&2; return 1; }
  echo "$lib: arm64, @rpath/libusb-1.0.dylib, macOS $minos or later (the app's minimum: $min_macos)"
}

if [ -n "$verify" ]; then
  verify_library "$verify"
  exit 0
fi

mkdir -p "$out"
out="$(cd "$out" && pwd)"
if [ -n "$(ls -A "$out")" ]; then
  echo "build.sh: --out must be a new or empty folder: $out" >&2
  exit 2
fi

work="$out/work"
mkdir "$work"
tarball="$work/libusb-$version.tar.bz2"
if [ -n "$source_file" ]; then
  cp "$source_file" "$tarball"
else
  curl --fail --location --silent --show-error --proto '=https' \
    "https://github.com/libusb/libusb/releases/download/v$version/libusb-$version.tar.bz2" -o "$tarball"
fi
actual="$(shasum -a 256 "$tarball" | cut -d' ' -f1)"
if [ "$actual" != "$sha256" ]; then
  echo "build.sh: $tarball's SHA-256 is $actual, not the pinned $sha256: refused" >&2
  exit 1
fi
echo "$tarball: SHA-256 $actual (pinned)"

tar -xjf "$tarball" -C "$work"
src="$work/libusb-$version"
[ -f "$src/configure" ] || { echo "build.sh: the tarball has no libusb-$version/configure" >&2; exit 1; }
[ -f "$src/COPYING" ] || { echo "build.sh: the tarball has no libusb-$version/COPYING" >&2; exit 1; }
actual="$(shasum -a 256 "$src/COPYING" | cut -d' ' -f1)"
if [ "$actual" != "$copying_sha256" ]; then
  echo "build.sh: the tarball's COPYING has SHA-256 $actual, not the pinned $copying_sha256: refused" >&2
  exit 1
fi

cd "$src"
export MACOSX_DEPLOYMENT_TARGET="$min_macos"
# configure finds pipe2 in a newer SDK (macOS 27) by linking, not by the minimum: an older macOS would have none, and
# libusb would call a missing function. Its pipe and fcntl fallback serves every macOS.
CFLAGS="-O2 -arch arm64 -mmacosx-version-min=$min_macos" LDFLAGS="-arch arm64 -mmacosx-version-min=$min_macos" \
  ./configure --prefix="$work/install" --host=aarch64-apple-darwin --enable-shared --disable-static \
  --disable-examples-build --disable-tests-build ac_cv_func_pipe2=no
make -j"$(getconf _NPROCESSORS_ONLN)"
make install

# One regular file with the install name the app's layout resolves (libtool's is the install prefix's path).
# (libtool names the real file libusb-1.0.<current-age>.dylib and links libusb-1.0.dylib to it.)
lib="$work/install/lib/libusb-1.0.0.dylib"
[ -f "$lib" ] && [ ! -L "$lib" ] || { echo "build.sh: libusb's dylib isn't $lib" >&2; exit 1; }
cp "$lib" "$out/libusb-1.0.dylib"
install_name_tool -id @rpath/libusb-1.0.dylib "$out/libusb-1.0.dylib"
codesign --force --sign - --timestamp=none "$out/libusb-1.0.dylib"
cp "$src/COPYING" "$out/libusb-COPYING.txt"

verify_library "$out/libusb-1.0.dylib"
otool -L "$out/libusb-1.0.dylib"
echo "Built $out/libusb-1.0.dylib (libusb $version) and $out/libusb-COPYING.txt"
