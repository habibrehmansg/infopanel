#!/bin/bash
# Builds the Linux downloads, a self-contained tarball and the .deb made from it (docs/2.0/packaging.md):
#
#   packaging/linux/build.sh --version <version> --allow-origin <origin> --arch <amd64|arm64> --out <folder>
#                            [--crash-reports-dsn <address>]
#
#   --version        the release's version, a SemVer such as 2.0.0, 2.0.0-beta.1 or 2.0.0+build.1 (the engine reports it;
#                    the .deb's version is it with the prerelease after a ~, 2.0.0~beta.1-1, so a release sorts after it)
#   --allow-origin   the web client the engine opens (https, a host, an optional port; no user info, path, query or
#                    fragment): built in as allow-origin's default
#   --arch           the Debian architecture to build for, amd64 or arm64 (the runtime linux-x64 or linux-arm64); any
#                    Linux machine builds either
#   --out            a new or empty folder for the results
#   --crash-reports-dsn  where the engine sends crash reports (a Sentry DSN: https, a key, a host, a project number):
#                    built in (InfoPanelCrashReportsDsn); without it the build sends none (docs/2.0/crash-reports.md)
#   --check-args     only check the arguments above, then stop (exit 0 when they are accepted)
#   --list-panel-drivers  only list the panel driver projects the build publishes into plugins/ (and those it leaves
#                    out, with why), then stop; needs no other argument
#   --print-rules    only print the udev rule lines the panel drivers need, those 70-infopanel.rules holds and those
#                    70-infopanel-opt-in.rules holds, then stop; needs no other argument
#   --check-rules    only check that the two rules files hold exactly those lines, then stop (exit 0 when they do, 1
#                    with the difference); needs no other argument
#
# A self-contained Release publish of the engine for the runtime, with every panel driver in plugins/, is checked (the
# executable and every ELF file built for the architecture, no Mach-O file, no link, none of another platform's folders,
# the udev rules exactly the ones the bundled drivers need), hashed file by file into <out>/files.sha256, and packed
# with LICENSE, LICENSES.md and linux/ (the udev rules, the opt-in rules, the menu entry, the sign-in entry, the icon)
# into InfoPanel-<version>-linux-<arch>.tar.gz. The .deb is made from that tarball: unpacked, checked against
# files.sha256, the engine in /usr/lib/infopanel with /usr/bin/infopanel linked to it, the rules in
# /usr/lib/udev/rules.d, the menu entry in /usr/share/applications, the sign-in entry in /etc/xdg/autostart (off:
# Hidden=true), the icon in /usr/share/pixmaps, the opt-in rules and LICENSE (as copyright) in /usr/share/doc/infopanel;
# then built with dpkg-deb and checked again, unpacked: its listing against what was staged, every file against its
# hash in files.sha256. The panel drivers' panel.json files are read by panel-matches.cs (dotnet run), with the
# engine's own reader.
# Needs Linux with the .NET SDK global.json pins, dpkg-deb and coreutils (packaging/linux/test.Dockerfile's image has
# them; on a Mac run it there, on a copy of the repository without bin/ and obj/).
set -euo pipefail

usage() {
  sed -n '2,23p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

version=""
origin=""
crash_dsn=""
arch=""
out=""
check_args=0
list_drivers=0
print_rules=0
check_rules=0
while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || usage; version="$2"; shift 2 ;;
    --allow-origin) [ $# -ge 2 ] || usage; origin="$2"; shift 2 ;;
    --arch) [ $# -ge 2 ] || usage; arch="$2"; shift 2 ;;
    --out) [ $# -ge 2 ] || usage; out="$2"; shift 2 ;;
    --crash-reports-dsn) [ $# -ge 2 ] || usage; crash_dsn="$2"; shift 2 ;;
    --check-args) check_args=1; shift ;;
    --list-panel-drivers) list_drivers=1; shift ;;
    --print-rules) print_rules=1; shift ;;
    --check-rules) check_rules=1; shift ;;
    -h|--help) usage ;;
    *) echo "build.sh: unknown argument: $1" >&2; usage ;;
  esac
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
rules_file="$here/70-infopanel.rules"
opt_in_file="$here/70-infopanel-opt-in.rules"

# The panel driver projects, as packaging/macos/build.sh lists them: every InfoPanel.Panels.{Family}/ with its
# {folder}.csproj and a panel.json, but the virtual panel (a test fixture, never shipped).
panel_drivers() {
  local dir name
  for dir in "$root"/InfoPanel.Panels.*/; do
    [ -d "$dir" ] || continue
    name="$(basename "$dir")"
    if [ "$name" = "InfoPanel.Panels.Virtual" ]; then
      [ "${1:-}" != verbose ] || echo "left out: $name (the virtual panel, a test fixture)"
    elif [ ! -f "$dir/panel.json" ] || [ ! -f "$dir/$name.csproj" ]; then
      [ "${1:-}" != verbose ] || echo "left out: $name (no panel.json beside $name.csproj: not a panel driver)"
    elif [ "${1:-}" = verbose ]; then
      echo "published: $name -> {engine}/plugins/$name"
    else
      echo "$name"
    fi
  done
}

# The (USB id, transport) pairs the drivers whose panel.json files are given match, one "vvvv:pppp transport" line
# each: read by panel-matches.cs with the engine's own reader (PanelPackages.ReadManifest; only each
# families[].match[] entry's own members, its transport usb when it names none; a package the engine would skip or
# not run, or whose driver's PanelManifest reads it otherwise, stops the build), and BeadaPanel's one id, built in
# (InfoPanel.Core/Panels/BeadaPanel/BeadaPanelProtocol.cs), over usb.
matches_of() {
  local printed line protocol vid pid
  local matches=()
  printed="$(cd "$root" && dotnet run --file "$here/panel-matches.cs" -- "$@")" ||
    { echo "build.sh: a panel driver's panel.json is refused (above)" >&2; return 1; }
  # Its own lines start "panel-match "; whatever else the dotnet CLI prints (a restore's notice) goes to stderr.
  while IFS= read -r line; do
    if [[ "$line" == "panel-match "* ]]; then
      [[ "$line" =~ ^panel-match\ [0-9a-f]{4}:[0-9a-f]{4}\ (usb|serial|hid)$ ]] ||
        { echo "build.sh: panel-matches.cs printed a line that isn't 'panel-match <vvvv:pppp> <usb|serial|hid>': $line" >&2; return 1; }
      matches+=("${line#panel-match }")
    elif [ -n "$line" ]; then
      echo "$line" >&2
    fi
  done <<< "$printed"
  [ "${#matches[@]}" -gt 0 ] || { echo "build.sh: the panel drivers match no device" >&2; return 1; }
  protocol="$root/InfoPanel.Core/Panels/BeadaPanel/BeadaPanelProtocol.cs"
  vid="$(sed -n 's/.*const ushort VendorId = 0x\([0-9A-Fa-f]\{4\}\);.*/\1/p' "$protocol" | tr 'A-F' 'a-f')"
  pid="$(sed -n 's/.*const ushort ProductId = 0x\([0-9A-Fa-f]\{4\}\);.*/\1/p' "$protocol" | tr 'A-F' 'a-f')"
  [ -n "$vid" ] && [ -n "$pid" ] || { echo "build.sh: BeadaPanel's VendorId and ProductId aren't in $protocol" >&2; return 1; }
  printf '%s\n' "${matches[@]}" "$vid:$pid usb"
}
# The ids a bundled driver matches that aren't a panel's own: the Linux Foundation's vendor id 1d6b, which the kernel's
# USB gadgets use (1d6b:0106 is its serial and mass storage gadget), 0525:a4a7, the kernel's serial gadget (g_serial's
# default id), and 1a86:5722, a CDC serial id of WCH's that boards with its chips keep. A rule on such an id would give
# the signed-in user any device with it (a board's serial console), and the drivers' data names a device by its id,
# transport and interface only, with nothing that tells a panel from those. So they get no rule by default:
# 70-infopanel-opt-in.rules holds their lines, for the owner of such a panel to copy into /etc/udev/rules.d
# (docs/2.0/packaging.md).
generic_id() {
  case "$1" in
    1d6b:*|0525:a4a7|1a86:5722) return 0 ;;
    *) return 1 ;;
  esac
}
# One udev rule line per (USB id, transport) a driver matches, giving the signed-in user the device's node
# (TAG+="uaccess", never a world-writable mode): usb, the device node libusb opens; hid, its hidraw nodes (HidSharp);
# serial, its tty nodes.
rule_line() {
  local vid="$1" pid="$2" transport="$3"
  case "$transport" in
    usb) echo "SUBSYSTEM==\"usb\", ENV{DEVTYPE}==\"usb_device\", ATTR{idVendor}==\"$vid\", ATTR{idProduct}==\"$pid\", TAG+=\"uaccess\"" ;;
    hid) echo "SUBSYSTEM==\"hidraw\", ATTRS{idVendor}==\"$vid\", ATTRS{idProduct}==\"$pid\", TAG+=\"uaccess\"" ;;
    serial) echo "SUBSYSTEM==\"tty\", ATTRS{idVendor}==\"$vid\", ATTRS{idProduct}==\"$pid\", TAG+=\"uaccess\"" ;;
    *) echo "build.sh: a panel driver matches $vid:$pid over '$transport', which this script has no udev rule for" >&2; return 1 ;;
  esac
}
# The rule lines, sorted, for the matches on stdin: "default", a panel's own ids (70-infopanel.rules); "opt-in", the
# generic ones (70-infopanel-opt-in.rules).
rules_for() {
  local kind="$1" id transport
  while read -r id transport; do
    if generic_id "$id"; then [ "$kind" = opt-in ] || continue; else [ "$kind" = default ] || continue; fi
    rule_line "${id%%:*}" "${id##*:}" "$transport" || return 1
  done | LC_ALL=C sort -u
}
# The rule lines a rules file holds: every line but comments and blank ones, sorted.
committed_rules() {
  grep -v '^[[:space:]]*\(#\|$\)' "$1" | LC_ALL=C sort
}
# Both rules files must hold exactly the lines the matches on stdin need: none missing, none more.
check_rules_files() {
  local matches expected file kind ok=0
  matches="$(cat)"
  for kind in default opt-in; do
    if [ "$kind" = default ]; then file="$rules_file"; else file="$opt_in_file"; fi
    expected="$(rules_for "$kind" <<< "$matches")"
    if [ "$expected" != "$(committed_rules "$file")" ]; then
      echo "build.sh: packaging/linux/$(basename "$file") isn't the $kind rules the bundled panel drivers need (< needed, > in the file):" >&2
      diff <(echo "$expected") <(committed_rules "$file") >&2 || true
      ok=1
    else
      echo "$(basename "$file"): $(grep -c . <<< "$expected") lines, as the panel drivers need"
    fi
  done
  return "$ok"
}
# The drivers' panel.json files in the source.
source_jsons() {
  local driver
  while IFS= read -r driver; do echo "$root/$driver/panel.json"; done < <(panel_drivers)
}

if [ "$list_drivers" -eq 1 ]; then
  panel_drivers verbose
  exit 0
fi
if [ "$print_rules" -eq 1 ] || [ "$check_rules" -eq 1 ]; then
  jsons=()
  while IFS= read -r json; do jsons+=("$json"); done < <(source_jsons)
  matches="$(matches_of "${jsons[@]}")"
  if [ "$check_rules" -eq 1 ]; then
    check_rules_files <<< "$matches"
    exit $?
  fi
  echo "# 70-infopanel.rules"
  rules_for default <<< "$matches"
  echo "# 70-infopanel-opt-in.rules"
  rules_for opt-in <<< "$matches"
  exit 0
fi

[ -n "$version" ] && [ -n "$origin" ] && [ -n "$arch" ] && [ -n "$out" ] || usage

# SemVer 2.0, as packaging/macos/build.sh checks it.
num='(0|[1-9][0-9]*)'
pre_id='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
build_id='[0-9A-Za-z-]+'
if ! [[ "$version" =~ ^$num\.$num\.$num(-$pre_id(\.$pre_id)*)?(\+$build_id(\.$build_id)*)?$ ]]; then
  echo "build.sh: --version must be a SemVer such as 2.0.0, 2.0.0-beta.1 or 2.0.0+build.1, not '$version'" >&2
  exit 2
fi
# Debian's version: the prerelease after a ~ (sorts before the release), then the package's revision 1.
core="${version%%[-+]*}"
rest="${version#"$core"}"
[ "${rest:0:1}" != "-" ] || rest="~${rest:1}"
deb_version="$core$rest-1"
origin_ok=0
if [[ "$origin" =~ ^https://([A-Za-z0-9.-]+|\[[0-9A-Fa-f:.]+\])(:([0-9]{1,5}))?$ ]]; then
  origin_ok=1
  [ -z "${BASH_REMATCH[3]}" ] || [ "$((10#${BASH_REMATCH[3]}))" -le 65535 ] || origin_ok=0
fi
if [ "$origin_ok" -eq 0 ]; then
  echo "build.sh: --allow-origin must be https, a host and an optional port, with no user info, path, query or fragment, such as https://client.example or https://client.example:8443, not '$origin'" >&2
  exit 2
fi
if [ -n "$crash_dsn" ] && ! [[ "$crash_dsn" =~ ^https://[0-9A-Za-z]+@[A-Za-z0-9.-]+(:[0-9]{1,5})?/[0-9]+$ ]]; then
  echo "build.sh: --crash-reports-dsn must be a Sentry DSN, https://<key>@<host>/<project>" >&2
  exit 2
fi
crash_property=()
[ -z "$crash_dsn" ] || crash_property=("-p:InfoPanelCrashReportsDsn=$crash_dsn")
# The architecture: the runtime it publishes for and the ELF machine every native file must be (EM_X86_64 62,
# EM_AARCH64 183).
case "$arch" in
  amd64) rid=linux-x64; elf_machine=62 ;;
  arm64) rid=linux-arm64; elf_machine=183 ;;
  *) echo "build.sh: --arch must be amd64 or arm64 (Debian's names), not '$arch'" >&2; exit 2 ;;
esac
[ "$check_args" -eq 0 ] || exit 0
if [ "$(uname -s)" != "Linux" ]; then
  echo "build.sh: Linux only (dpkg-deb); on a Mac run it in packaging/linux/test.Dockerfile's image (docs/2.0/packaging.md)" >&2
  exit 2
fi
for tool in dotnet dpkg-deb tar gzip sha256sum md5sum od du; do
  command -v "$tool" >/dev/null 2>&1 || { echo "build.sh: $tool isn't on the PATH" >&2; exit 2; }
done

mkdir -p "$out"
out="$(cd "$out" && pwd)"
if [ -n "$(ls -A "$out")" ]; then
  echo "build.sh: --out must be a new or empty folder: $out" >&2
  exit 2
fi

name="InfoPanel-$version-linux-$arch"
publish="$out/publish"
tar_root="$out/tarball"
tarball="$out/$name.tar.gz"
manifest="$out/files.sha256"
deb_root="$out/deb"
deb="$out/$name.deb"

echo "==> Publishing the engine $version for $rid"
(cd "$root" && dotnet publish InfoPanel/InfoPanel.csproj -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -p:InfoPanelAllowOrigin="$origin" ${crash_property[@]+"${crash_property[@]}"} -p:DebugType=none -p:DebugSymbols=false \
  -o "$publish")
echo "==> Publishing the panel drivers into plugins/"
drivers=()
while IFS= read -r driver; do
  [ -n "$driver" ] || continue
  (cd "$root" && dotnet publish "$driver/$driver.csproj" -c Release -r "$rid" --self-contained false \
    -p:Version="$version" -p:DebugType=none -p:DebugSymbols=false -o "$publish/plugins/$driver")
  [ -f "$publish/plugins/$driver/$driver.dll" ] && [ -f "$publish/plugins/$driver/panel.json" ] || {
    echo "build.sh: the panel driver $driver published no $driver.dll and panel.json" >&2
    exit 1
  }
  drivers+=("$driver")
done < <(panel_drivers)
[ "${#drivers[@]}" -gt 0 ] || { echo "build.sh: no panel driver was published" >&2; exit 1; }

echo "==> Checking the publish"
fail() { echo "build.sh: $*" >&2; exit 1; }
# Another platform's folders: the sensor helper (Windows), the media helper (macOS), a web client.
for folder in sensor-helper mediaremote-adapter wwwroot; do
  [ ! -e "$publish/$folder" ] || fail "the publish has $folder/, which a Linux build never carries"
done
links="$(find "$publish" -type l | head -n 1)"
[ -z "$links" ] || fail "a link in the publish, which this package doesn't carry: $links"
others="$(find "$publish" -mindepth 1 ! -type f ! -type d | head -n 1)"
[ -z "$others" ] || fail "something in the publish that is neither a file nor a folder: $others"
magic() { od -An -tx1 -N4 "$1" | tr -d ' \n'; }
elf_ok() {
  # 64-bit (EI_CLASS 2), little-endian (EI_DATA 1), the architecture's machine.
  [ "$(od -An -tu1 -j4 -N2 "$1" | tr -s ' ' | sed 's/^ //;s/ $//')" = "2 1" ] &&
    [ "$(od -An -tu2 -j18 -N2 "$1" | tr -d ' ')" = "$elf_machine" ]
}
elf_count=0
while IFS= read -r -d '' file; do
  case "$(magic "$file")" in
    7f454c46)
      elf_ok "$file" || fail "an ELF file not built for $arch: ${file#"$publish"/}"
      elf_count=$((elf_count + 1)) ;;
    cffaedfe|cefaedfe|feedfacf|feedface|cafebabe|bebafeca)
      fail "a Mach-O file in a Linux publish: ${file#"$publish"/}" ;;
  esac
done < <(find "$publish" -type f -print0)
for file in InfoPanel libcoreclr.so libSkiaSharp.so libHarfBuzzSharp.so; do
  [ -f "$publish/$file" ] && [ "$(magic "$publish/$file")" = 7f454c46 ] || fail "the publish has no $file built for Linux"
done
[ -x "$publish/InfoPanel" ] || fail "the publish's InfoPanel isn't executable"
[ -f "$publish/InfoPanel.dll" ] || fail "the publish has no InfoPanel.dll"
echo "$elf_count ELF files, every one built for $arch"
# The udev rules hold exactly the lines the bundled drivers (their published panel.json files) and BeadaPanel need, the
# generic ids' in the opt-in file and the rest in the default one: none missing, none more.
jsons=()
for driver in "${drivers[@]}"; do jsons+=("$publish/plugins/$driver/panel.json"); done
matches="$(matches_of "${jsons[@]}")"
check_rules_files <<< "$matches" || exit 1

echo "==> Making $name.tar.gz"
top="$tar_root/$name"
mkdir -p "$top/linux"
cp -R "$publish/." "$top/"
cp "$root/LICENSE" "$root/LICENSES.md" "$top/"
cp "$rules_file" "$opt_in_file" "$here/infopanel.desktop" "$here/infopanel-autostart.desktop" "$top/linux/"
cp "$root/Images/logo.png" "$top/linux/infopanel.png"
chmod -R u+rwX,go+rX,go-w "$top"
# Every file hashed once, here, after the checks: the tarball and the .deb are checked against this list.
(cd "$top" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum) > "$manifest"
echo "$(wc -l < "$manifest" | tr -d ' ') files hashed into files.sha256"
epoch="${SOURCE_DATE_EPOCH:-$(git -C "$root" log -1 --format=%ct 2>/dev/null || date +%s)}"
(cd "$tar_root" && tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$epoch" -cf - "$name") | gzip -n -9 > "$tarball"

echo "==> Making $name.deb from the tarball"
unpacked="$out/unpacked"
mkdir -p "$unpacked"
tar -xzf "$tarball" -C "$unpacked"
[ "$(ls -A "$unpacked")" = "$name" ] || fail "the tarball holds more than its one folder $name"
(cd "$unpacked/$name" && sha256sum --quiet --strict -c "$manifest") || fail "the tarball's files aren't the ones hashed"
[ "$(cd "$unpacked/$name" && find . -type f | wc -l)" = "$(wc -l < "$manifest")" ] || fail "the tarball has files that weren't hashed"
[ -z "$(cd "$unpacked/$name" && find . ! -type f ! -type d | head -n 1)" ] || fail "the tarball holds a link or a special file"
from="$unpacked/$name"
# Where each of the tarball's files goes in the .deb: each of linux/'s to its own place (a file there with none stops
# the build), every other one under /usr/lib/infopanel; LICENSE also to /usr/share/doc/infopanel/copyright.
deb_path() {
  case "$1" in
    ./linux/70-infopanel.rules) echo ./usr/lib/udev/rules.d/70-infopanel.rules ;;
    ./linux/70-infopanel-opt-in.rules) echo ./usr/share/doc/infopanel/70-infopanel-opt-in.rules ;;
    ./linux/infopanel.desktop) echo ./usr/share/applications/infopanel.desktop ;;
    ./linux/infopanel-autostart.desktop) echo ./etc/xdg/autostart/infopanel.desktop ;;
    ./linux/infopanel.png) echo ./usr/share/pixmaps/infopanel.png ;;
    ./linux/*) return 1 ;;
    *) echo "./usr/lib/infopanel/${1#./}" ;;
  esac
}
copyright=./usr/share/doc/infopanel/copyright
# Every file the .deb holds (DEBIAN/ aside) with its hash in files.sha256, at its place in the .deb: the .deb is
# staged from this list and checked against it once built.
deb_manifest="$out/deb-files.sha256"
while IFS= read -r line; do
  hash="${line%%  *}"
  path="${line#*  }"
  [[ "$hash" =~ ^[0-9a-f]{64}$ ]] && [ "${path:0:2}" = ./ ] || fail "files.sha256 has a line this script can't read: $line"
  place="$(deb_path "$path")" || fail "the tarball's $path has no place in the .deb"
  echo "$hash  $place"
  [ "$path" != ./LICENSE ] || echo "$hash  $copyright"
done < "$manifest" > "$deb_manifest"
grep -q "  $copyright\$" "$deb_manifest" || fail "the tarball has no LICENSE"
mkdir -p "$deb_root/usr/bin" "$deb_root/DEBIAN"
while IFS= read -r line; do
  path="${line#*  }"
  place="$deb_root/$(deb_path "$path")"
  mkdir -p "$(dirname "$place")"
  cp "$from/$path" "$place"
done < "$manifest"
cp "$from/LICENSE" "$deb_root/$copyright"
ln -s ../lib/infopanel/InfoPanel "$deb_root/usr/bin/infopanel"
find "$deb_root/usr/lib/udev" "$deb_root/usr/share" "$deb_root/etc" -type f -exec chmod 0644 {} +
installed_size="$(du -sk --apparent-size "$deb_root/usr" "$deb_root/etc" | awk '{ s += $1 } END { print s }')"
sed -e "s/@VERSION@/$deb_version/" -e "s/@ARCH@/$arch/" -e "s/@SIZE@/$installed_size/" \
  "$here/debian/control" > "$deb_root/DEBIAN/control"
cp "$here/debian/conffiles" "$here/debian/postinst" "$here/debian/postrm" "$deb_root/DEBIAN/"
chmod 0644 "$deb_root/DEBIAN/control" "$deb_root/DEBIAN/conffiles"
chmod 0755 "$deb_root/DEBIAN/postinst" "$deb_root/DEBIAN/postrm"
(cd "$deb_root" && find usr etc -type f -print0 | LC_ALL=C sort -z | xargs -0 md5sum) > "$deb_root/DEBIAN/md5sums"
chmod 0644 "$deb_root/DEBIAN/md5sums"
chmod -R u+rwX,go+rX,go-w "$deb_root"
find "$deb_root" -exec touch -h -d "@$epoch" {} +
SOURCE_DATE_EPOCH="$epoch" dpkg-deb --root-owner-group -Zxz --build "$deb_root" "$deb"

echo "==> Checking the .deb"
dpkg-deb --info "$deb"
check="$out/deb-check"
mkdir -p "$check"
dpkg-deb -x "$deb" "$check"
listing() { (cd "$1" && find . -mindepth 1 \( -type f -o -type l -o -type d \) -printf '%y %m %p %l\n' | LC_ALL=C sort); }
staged="$(listing "$deb_root" | grep -v ' \./DEBIAN')"
[ "$(listing "$check")" = "$staged" ] || {
  echo "build.sh: the .deb's files aren't the ones staged (< staged, > in the .deb):" >&2
  diff <(echo "$staged") <(listing "$check") >&2 || true
  exit 1
}
# Every file in the .deb, the relocated ones too, against its hash in files.sha256; and no file the list doesn't have.
(cd "$check" && sha256sum --quiet --strict -c "$deb_manifest") || fail "the .deb's files aren't the ones hashed"
[ "$(cd "$check" && find . -type f | LC_ALL=C sort)" = "$(sed 's/^[0-9a-f]\{64\}  //' "$deb_manifest" | LC_ALL=C sort)" ] ||
  fail "the .deb's files aren't exactly the hashed ones"
echo "$(wc -l < "$deb_manifest" | tr -d ' ') files in the .deb, each as hashed"
dpkg-deb --field "$deb" Package Version Architecture Depends

echo "==> Done"
echo "Tarball: $tarball"
echo "Package: $deb"
ls -l "$tarball" "$deb"
sha256sum "$tarball" "$deb"
