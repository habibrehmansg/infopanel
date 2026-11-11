#!/usr/bin/env bash
# Builds the media helper the macOS engine runs for the Now Playing source (InfoPanel.Core/Sensors/NowPlaying/
# MacNowPlayingReader.cs): mediaremote-adapter's framework, from the sources vendored beside this script at the commit
# pins.txt names, with clang alone (no CMake), for arm64 (as the app), and its Perl script, which Apple's /usr/bin/perl
# runs with the framework (MediaRemote answers only Apple's own programs).
#
#   native/mediaremote-adapter/build.sh --out <folder>
#   native/mediaremote-adapter/build.sh --verify <folder>
#
#   --out         the helper's folder; it gets mediaremote-adapter.pl, MediaRemoteAdapter.framework/MediaRemoteAdapter
#                 and LICENSE (mediaremote-adapter's licence text), each replaced when it is there already
#   --verify      only check a folder this script built (the checks below), then stop
#   --check-args  only check the arguments, then stop (exit 0 when they are accepted)
#
# Exit 0 built (or passed), 1 refused (a vendored file whose SHA-256 isn't pinned, a check that fails), 2 can't (not
# macOS, no clang, bad arguments). Every vendored file is checked against pins.txt before anything is compiled, and a
# source under src/ that pins.txt doesn't list is refused. The framework is built for the app's minimum macOS
# (packaging/macos/Info.plist's LSMinimumSystemVersion), with an install name of its own (no build path in it), and
# signed ad hoc (arm64 refuses unsigned code); packaging signs it again with the rest of the app. Checks, after a build
# and with --verify: arm64 only, nothing linked but macOS's own libraries, a minimum macOS no newer than the app's, a
# valid signature, and the script and licence as pinned. Needs macOS's compiler (Xcode's command line tools).
set -euo pipefail

usage() {
  sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

out=""
verify=""
check_args=0
while [ $# -gt 0 ]; do
  case "$1" in
    --out) [ $# -ge 2 ] || usage; out="$2"; shift 2 ;;
    --verify) [ $# -ge 2 ] || usage; verify="$2"; shift 2 ;;
    --check-args) check_args=1; shift ;;
    -h|--help) usage ;;
    *) echo "build.sh: unknown argument: $1" >&2; usage ;;
  esac
done
if [ -n "$verify" ]; then
  [ -z "$out" ] || { echo "build.sh: --verify takes no --out" >&2; exit 2; }
  [ -d "$verify" ] || { echo "build.sh: --verify isn't a folder: $verify" >&2; exit 2; }
else
  [ -n "$out" ] || usage
fi
[ "$check_args" -eq 0 ] || exit 0

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
plist="$here/../../packaging/macos/Info.plist"
if [ "$(uname -s)" != "Darwin" ]; then
  echo "build.sh: macOS only (the helper reads macOS's media session)" >&2
  exit 2
fi
clang="$(xcrun --find clang 2>/dev/null)" || clang=""
[ -n "$clang" ] && [ -x "$clang" ] || { echo "build.sh: no clang (install Xcode's command line tools: xcode-select --install)" >&2; exit 2; }
sdk="$(xcrun --sdk macosx --show-sdk-path 2>/dev/null)" || sdk=""
[ -n "$sdk" ] && [ -d "$sdk" ] || { echo "build.sh: no macOS SDK (install Xcode's command line tools: xcode-select --install)" >&2; exit 2; }

min_macos="$(plutil -extract LSMinimumSystemVersion raw -o - "$plist")" || { echo "build.sh: plutil can't read LSMinimumSystemVersion from $plist" >&2; exit 2; }
[[ "$min_macos" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] || { echo "build.sh: $plist's LSMinimumSystemVersion isn't a version: $min_macos" >&2; exit 2; }

# Succeeds when version $1 is newer than version $2 (dotted numbers, a missing part being 0).
newer_than() {
  local v i
  local -a a b
  a=(); b=()
  v="$1."; while [ -n "$v" ]; do a+=("${v%%.*}"); v="${v#*.}"; done
  v="$2."; while [ -n "$v" ]; do b+=("${v%%.*}"); v="${v#*.}"; done
  for i in 0 1 2; do
    if (( 10#${a[i]:-0} > 10#${b[i]:-0} )); then return 0; fi
    if (( 10#${a[i]:-0} < 10#${b[i]:-0} )); then return 1; fi
  done
  return 1
}

# The pinned files: "<sha256> <path>" a line.
pinned="$(sed -n 's/^file=//p' "$here/pins.txt")"
[ -n "$pinned" ] || { echo "build.sh: pins.txt lists no file" >&2; exit 2; }
pin_of() {
  printf '%s\n' "$pinned" | awk -v p="$1" '$2 == p { print $1 }'
}

# Every pinned file as pinned, and no source under src/ that isn't pinned.
check_sources() {
  local sha path actual
  while read -r sha path; do
    [[ "$sha" =~ ^[0-9a-f]{64}$ ]] || { echo "build.sh: pins.txt's SHA-256 for $path isn't 64 lower-case hex digits" >&2; exit 2; }
    [ -f "$here/$path" ] || { echo "build.sh: $path is pinned but missing" >&2; exit 1; }
    actual="$(shasum -a 256 "$here/$path" | cut -d' ' -f1)"
    [ "$actual" = "$sha" ] || { echo "build.sh: $path has SHA-256 $actual, not the pinned $sha: refused" >&2; exit 1; }
  done <<< "$pinned"
  while IFS= read -r path; do
    path="${path#"$here/"}"
    [ -n "$(pin_of "$path")" ] || { echo "build.sh: $path isn't pinned in pins.txt: refused" >&2; exit 1; }
  done < <(find "$here/src" "$here/include" "$here/bin" -type f)
}

# The checks a folder this script built passes: a check that fails returns 1, a tool that fails stops with exit 2.
verify_folder() {
  local folder="$1" lib archs deps foreign build minos file sha
  lib="$folder/MediaRemoteAdapter.framework/MediaRemoteAdapter"
  [ -f "$lib" ] || { echo "build.sh: $lib is missing" >&2; return 1; }
  cant() { echo "build.sh: $1 failed on $lib (or printed nothing): can't check it: refused" >&2; exit 2; }
  archs="$(lipo -archs "$lib")" || cant lipo
  [ "$archs" = "arm64" ] || { echo "build.sh: $lib isn't arm64 only" >&2; return 1; }
  deps="$(otool -L "$lib")" || cant otool
  [ -n "$deps" ] || cant otool
  foreign="$(printf '%s\n' "$deps" | tail -n +3 | awk '$0 !~ /^[ \t]+(\/usr\/lib\/|\/System\/Library\/)/')" || cant awk
  if [ -n "$foreign" ]; then
    printf '%s\n' "$foreign"
    echo "build.sh: $lib links something that isn't macOS's own (above)" >&2
    return 1
  fi
  build="$(vtool -show-build "$lib")" || cant vtool
  minos="$(printf '%s\n' "$build" | awk '$1 == "minos" { print $2 }')" || cant awk
  [[ "$minos" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]] || { echo "build.sh: $lib names no minimum macOS" >&2; return 1; }
  if newer_than "$minos" "$min_macos"; then
    echo "build.sh: $lib needs macOS $minos, newer than the app's minimum $min_macos: refused" >&2
    return 1
  fi
  codesign --verify --strict "$lib" || { echo "build.sh: $lib's signature isn't valid (or codesign failed)" >&2; return 1; }
  for file in mediaremote-adapter.pl:bin/mediaremote-adapter.pl LICENSE:LICENSE; do
    sha="$(shasum -a 256 "$folder/${file%%:*}" | cut -d' ' -f1)" || { echo "build.sh: $folder/${file%%:*} is missing" >&2; return 1; }
    [ "$sha" = "$(pin_of "${file#*:}")" ] || { echo "build.sh: $folder/${file%%:*} isn't the pinned ${file#*:}" >&2; return 1; }
  done
  echo "$folder: arm64, macOS $minos or later (the app's minimum: $min_macos), the script and licence as pinned"
}

if [ -n "$verify" ]; then
  verify_folder "$(cd "$verify" && pwd)"
  exit 0
fi

check_sources
mkdir -p "$out/MediaRemoteAdapter.framework"
out="$(cd "$out" && pwd)"

sources=()
while read -r _ path; do
  case "$path" in *.m) sources+=("$here/$path") ;; esac
done <<< "$pinned"

# Built under a temporary name, then moved over the last build, so a failed build leaves no half-written library.
lib="$out/MediaRemoteAdapter.framework/MediaRemoteAdapter"
"$clang" -dynamiclib -O2 -arch arm64 -isysroot "$sdk" -mmacosx-version-min="$min_macos" -fobjc-arc -fvisibility=default \
  -I"$here/include" -I"$here/src" \
  -install_name @rpath/MediaRemoteAdapter.framework/MediaRemoteAdapter \
  -framework Foundation -framework AppKit -framework UniformTypeIdentifiers \
  "${sources[@]}" -o "$lib.new"
codesign --force --sign - --timestamp=none "$lib.new"
mv -f "$lib.new" "$lib"
cp "$here/bin/mediaremote-adapter.pl" "$out/mediaremote-adapter.pl"
cp "$here/LICENSE" "$out/LICENSE"

verify_folder "$out"
echo "Built $out (mediaremote-adapter $(sed -n 's/^commit=//p' "$here/pins.txt"))"
