#!/bin/bash
# Builds the macOS app and its disk image (docs/2.0/packaging.md):
#
#   packaging/macos/build.sh --version <version> --allow-origin <origin> --out <folder> [--libusb-source <tarball>]
#
#   --version        the release's version, a SemVer such as 2.0.0, 2.0.0-beta.1 or 2.0.0+build.1 (the engine reports it;
#                    the bundle's CFBundleShortVersionString is its major.minor.patch, CFBundleVersion the whole of it)
#   --allow-origin   the web client the engine opens (https, a host, an optional port; no user info, path, query or
#                    fragment): built in as allow-origin's default
#   --out            a new or empty folder for the results
#   --libusb-source  libusb's release tarball (libusb-<version>.tar.bz2), fetched elsewhere: native/libusb/build.sh,
#                    which this script runs into <out>/libusb, refuses it unless its SHA-256 is the one native/libusb/pins.txt
#                    pins; without it that build downloads the tarball (and checks it the same way)
#   --check-args     only check the arguments above, then stop (exit 0 when they are accepted)
#
# A self-contained osx-arm64 Release publish of the engine becomes InfoPanel.app. codesign takes every file in
# Contents/MacOS for code (a managed .dll, a .json or a folder there makes it refuse the bundle), so the publish goes to
# Contents/Resources/engine, its Mach-O files (the executable, the runtime's and SkiaSharp's libraries) move to
# Contents/MacOS, and each side links to the other's entries by relative symlinks: the executable finds its libraries
# and its managed files beside it, and .NET, which takes the app's folder from InfoPanel.dll's real path, finds the
# libraries in Contents/Resources/engine. libusb-1.0.dylib (USB panels), built here from libusb's pinned source and
# checked (native/libusb/build.sh --verify: arm64, its install name, a minimum macOS no newer than Info.plist's
# LSMinimumSystemVersion), joins the publish's top level first, so it takes the same way into Contents/MacOS, where the
# engine finds it beside itself. The icon and the licences (LICENSE, LICENSES.md and libusb's licence text from its
# source, libusb-COPYING.txt) go to Contents/Resources. Every Mach-O file is signed ad hoc, deepest first, then the bundle
# (no hardened runtime), and the signature is verified. Then the disk image, InfoPanel-<version>-mac-arm64.dmg:
# the app, a link to /Applications, LICENSE, LICENSES.md and libusb-COPYING.txt. Needs macOS (codesign, hdiutil, plutil),
# Xcode's command line tools and the .NET SDK global.json pins. Nothing is notarised: macOS asks the user to allow it once
# (System Settings, Privacy & Security, Open Anyway).
set -euo pipefail

usage() {
  sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

version=""
origin=""
out=""
libusb_source=""
check_args=0
while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || usage; version="$2"; shift 2 ;;
    --allow-origin) [ $# -ge 2 ] || usage; origin="$2"; shift 2 ;;
    --out) [ $# -ge 2 ] || usage; out="$2"; shift 2 ;;
    --libusb-source) [ $# -ge 2 ] || usage; libusb_source="$2"; shift 2 ;;
    --check-args) check_args=1; shift ;;
    -h|--help) usage ;;
    *) echo "build.sh: unknown argument: $1" >&2; usage ;;
  esac
done

[ -n "$version" ] && [ -n "$origin" ] && [ -n "$out" ] || usage
[ -z "$libusb_source" ] || [ -f "$libusb_source" ] || { echo "build.sh: --libusb-source isn't a file: $libusb_source" >&2; exit 2; }

# SemVer 2.0: numeric parts without leading zeros, then optional -prerelease (dot-separated, none empty, numeric ones
# without leading zeros) and optional +build (dot-separated, none empty).
num='(0|[1-9][0-9]*)'
pre_id='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
build_id='[0-9A-Za-z-]+'
if ! [[ "$version" =~ ^$num\.$num\.$num(-$pre_id(\.$pre_id)*)?(\+$build_id(\.$build_id)*)?$ ]]; then
  echo "build.sh: --version must be a SemVer such as 2.0.0, 2.0.0-beta.1 or 2.0.0+build.1, not '$version'" >&2
  exit 2
fi
short_version="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"
# What the engine accepts as a built-in origin (EngineOptions.TryNormalizeExactOrigin): https, a host (a name, an IPv4
# address or a bracketed IPv6 one) and an optional port; no user info, path, query or fragment.
origin_ok=0
if [[ "$origin" =~ ^https://([A-Za-z0-9.-]+|\[[0-9A-Fa-f:.]+\])(:([0-9]{1,5}))?$ ]]; then
  origin_ok=1
  [ -z "${BASH_REMATCH[3]}" ] || [ "$((10#${BASH_REMATCH[3]}))" -le 65535 ] || origin_ok=0
fi
if [ "$origin_ok" -eq 0 ]; then
  echo "build.sh: --allow-origin must be https, a host and an optional port, with no user info, path, query or fragment, such as https://client.example or https://client.example:8443, not '$origin'" >&2
  exit 2
fi
[ "$check_args" -eq 0 ] || exit 0
if [ "$(uname -s)" != "Darwin" ]; then
  echo "build.sh: macOS only (codesign, hdiutil)" >&2
  exit 2
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"

mkdir -p "$out"
out="$(cd "$out" && pwd)"
if [ -n "$(ls -A "$out")" ]; then
  echo "build.sh: --out must be a new or empty folder: $out" >&2
  exit 2
fi

publish="$out/publish"
stage="$out/dmg"
app="$stage/InfoPanel.app"
dmg="$out/InfoPanel-$version-mac-arm64.dmg"

echo "==> Building libusb-1.0 for USB panels from its pinned source"
# Only from the source tarball pins.txt pins (downloaded, or --libusb-source), never a library built elsewhere.
if [ -n "$libusb_source" ]; then
  "$root/native/libusb/build.sh" --out "$out/libusb" --source "$libusb_source"
else
  "$root/native/libusb/build.sh" --out "$out/libusb"
fi
libusb="$out/libusb/libusb-1.0.dylib"
libusb_copying="$out/libusb/libusb-COPYING.txt"
"$root/native/libusb/build.sh" --verify "$libusb"
copying_sha256="$(sed -n 's/^source_copying_sha256=//p' "$root/native/libusb/pins.txt")"
[ -n "$copying_sha256" ] && [ "$(shasum -a 256 "$libusb_copying" | cut -d' ' -f1)" = "$copying_sha256" ] || {
  echo "build.sh: $libusb_copying isn't the COPYING native/libusb/pins.txt pins" >&2
  exit 1
}

echo "==> Publishing the engine $version for osx-arm64"
(cd "$root" && dotnet publish InfoPanel/InfoPanel.csproj -c Release -r osx-arm64 --self-contained true \
  -p:Version="$version" -p:InfoPanelAllowOrigin="$origin" -p:DebugType=none -p:DebugSymbols=false \
  -o "$publish")
[ -x "$publish/InfoPanel" ] || { echo "build.sh: the publish has no InfoPanel executable" >&2; exit 1; }
cp "$libusb" "$publish/libusb-1.0.dylib"

echo "==> Assembling InfoPanel.app"
macos="$app/Contents/MacOS"
engine="$app/Contents/Resources/engine"
mkdir -p "$macos" "$app/Contents/Resources"
ditto "$publish" "$engine"
is_macho() { file -b "$1" | grep -q '^Mach-O'; }
# A Mach-O file below the publish's top level would stay in Resources, sealed as a resource rather than signed as code.
while IFS= read -r -d '' file; do
  if is_macho "$file"; then
    echo "build.sh: a Mach-O file below the publish's top level, which this layout doesn't place: $file" >&2
    exit 1
  fi
done < <(find "$engine" -mindepth 2 -type f -print0)
for entry in "$engine"/*; do
  name="$(basename "$entry")"
  if [ -f "$entry" ] && [ ! -L "$entry" ] && is_macho "$entry"; then
    mv "$entry" "$macos/$name"
    ln -s "../../MacOS/$name" "$engine/$name"
  else
    ln -s "../Resources/engine/$name" "$macos/$name"
  fi
done
[ -f "$macos/InfoPanel" ] && [ ! -L "$macos/InfoPanel" ] || { echo "build.sh: Contents/MacOS/InfoPanel isn't the executable" >&2; exit 1; }
cp "$here/Info.plist" "$app/Contents/Info.plist"
plutil -replace CFBundleShortVersionString -string "$short_version" "$app/Contents/Info.plist"
plutil -replace CFBundleVersion -string "$version" "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"
cp "$here/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
cp "$root/LICENSE" "$root/LICENSES.md" "$libusb_copying" "$app/Contents/Resources/"

echo "==> Signing ad hoc, deepest first"
# Every Mach-O file but the main executable, the deepest paths first, so each is signed before what contains it; then
# the bundle, which signs the main executable with it (signing that file alone would sign the bundle too early).
machos=()
while IFS= read -r -d '' file; do
  if [ "$file" != "$macos/InfoPanel" ] && is_macho "$file"; then
    machos+=("$file")
  fi
done < <(find "$app/Contents" -type f -print0)
if [ "${#machos[@]}" -eq 0 ]; then
  echo "build.sh: no Mach-O libraries found beside the executable" >&2
  exit 1
fi
while IFS= read -r file; do
  codesign --force --sign - --timestamp=none "$file"
done < <(for file in "${machos[@]}"; do
  depth="${file//[^\/]/}"
  printf '%d\t%s\n' "${#depth}" "$file"
done | sort -rn -k1,1 | cut -f2-)
codesign --force --sign - --timestamp=none "$app"
codesign --verify --deep --strict --verbose=2 "$app"
echo "Signed $(( ${#machos[@]} + 1 )) Mach-O files (the executable with the bundle)"

echo "==> Making the disk image"
ln -s /Applications "$stage/Applications"
cp "$root/LICENSE" "$root/LICENSES.md" "$libusb_copying" "$stage/"
hdiutil create -volname "InfoPanel $version" -srcfolder "$stage" -fs HFS+ -format UDZO "$dmg"
hdiutil verify "$dmg"

echo "==> Done"
echo "App: $app"
echo "Disk image: $dmg"
shasum -a 256 "$dmg"
