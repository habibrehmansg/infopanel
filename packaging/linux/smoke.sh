#!/bin/bash
# Smoke-tests a .deb that packaging/linux/build.sh made, as root in a clean Ubuntu or Debian container (docs/2.0/packaging.md):
#
#   docker run --rm -v <folder with the .deb>:/pkg:ro -v <repository>/packaging/linux:/smoke:ro ubuntu:24.04 \
#     bash /smoke/smoke.sh /pkg/InfoPanel-<version>-linux-<arch>.deb <version> [<build>]
#
# apt installs it with what it depends on; the engine starts headless from the PATH as an ordinary user (uid 1000),
# on a data folder and a port of its own inside the container, in the time zone Europe/Berlin (TZ), and GET
# /api/session must report <version> (and <build>, when given, as engineBuild) and the clock's hour must be Berlin's,
# not UTC's (the zone resolved from tzdata); TERM stops it with exit code 0. Then apt removes it: every file it installed is
# gone but its one conffile (the sign-in entry), which the purge takes; after the purge and the removal of what it
# pulled in, nothing that was there before is gone and no path of InfoPanel's is left outside the user's home (the
# packages and paths that differ are listed: apt's autoremove keeps what an installed package recommends, such as
# ca-certificates). Never on a computer of your own: it installs and removes packages.
set -euo pipefail
deb="$1"
expected_version="$2"
expected_build="${3:-}"
port=5391
token="smoke$(od -An -tx1 -N16 /dev/urandom | tr -d ' \n')"
export DEBIAN_FRONTEND=noninteractive

step() { echo; echo "==> $*"; }
# Every path outside apt's and dpkg's own records, the logs and the caches: what an install leaves behind shows here.
snapshot() {
  find / -xdev \( -path /proc -o -path /sys -o -path /dev -o -path /tmp -o -path /run -o -path /var/lib/apt \
    -o -path /var/lib/dpkg -o -path /var/cache -o -path /var/log -o -path /smoke -o -path /pkg \) -prune -o -print |
    LC_ALL=C sort
}

step "apt-get update"
apt-get update -q
snapshot > /tmp/before.txt
dpkg-query -W -f '${Package}\n' | LC_ALL=C sort > /tmp/packages-before.txt

step "apt-get install $deb"
apt-get install -y -q --no-install-recommends "$deb"
dpkg -s infopanel | grep -E '^(Package|Status|Version|Architecture|Depends|Recommends):'
dpkg -L infopanel | grep -v '^/usr/lib/infopanel/' | LC_ALL=C sort
echo "/usr/lib/infopanel: $(dpkg -L infopanel | grep -c '^/usr/lib/infopanel/') paths"
dpkg -L infopanel | LC_ALL=C sort > /tmp/installed.txt
readlink -f /usr/bin/infopanel
command -v infopanel

step "the engine, headless, as uid 1000, from the PATH, in the time zone Europe/Berlin"
# getent's exit code 2 is "no such user": the image has none at uid 1000 (Debian's), so one is made.
user_line=""
lookup=0
user_line="$(getent passwd 1000)" || lookup=$?
case "$lookup" in
  0) user="${user_line%%:*}" ;;
  2) useradd --uid 1000 --create-home infopanel; user=infopanel ;;
  *) echo "smoke.sh: getent passwd 1000 failed with exit code $lookup" >&2; exit 1 ;;
esac
home="$(getent passwd "$user" | cut -d: -f6)"
echo "uid 1000: $user, home $home"
# setpriv runs the engine itself as the user (no process between, so TERM reaches the engine). TZ names the engine's
# local zone: .NET finds it in the system's time zone data (tzdata), as it finds every zone a document names.
as_user=(setpriv --reuid=1000 --regid="$(id -g "$user")" --init-groups env HOME="$home")
data="$("${as_user[@]}" mktemp -d)"
v1="$("${as_user[@]}" mktemp -d)"
"${as_user[@]}" TZ=Europe/Berlin infopanel --port "$port" --token "$token" --data-dir "$data" --v1-dir "$v1" \
  --no-windows --no-browser --no-tray --offline > /tmp/engine.log 2>&1 &
pid=$!
# bash's own /dev/tcp: the container needs no HTTP client.
get() {
  exec 3<>"/dev/tcp/127.0.0.1/$port"
  printf 'GET %s HTTP/1.0\r\nHost: 127.0.0.1\r\nAuthorization: Bearer %s\r\n\r\n' "$1" "$token" >&3
  cat <&3
}
session=""
for _ in $(seq 1 60); do
  if reply="$(get /api/session 2>/dev/null)" && [[ "$reply" == "HTTP/1.1 200"* ]]; then
    session="${reply#*$'\r\n\r\n'}"
    break
  fi
  kill -0 "$pid" 2>/dev/null || break
  sleep 1
done
echo "GET /api/session: $session"
# The time zone: the clock source's 24-hour reading is the hour in Europe/Berlin, 1 or 2 hours past UTC's (CET, CEST).
# Without the zone's data .NET takes UTC, 0 hours past.
zone_hour=""
utc_before=""
utc_after=""
if [ -n "$session" ]; then
  for _ in $(seq 1 10); do
    utc_before="$(date -u +%-H)"
    reply="$(get /api/sensors/values 2>/dev/null || true)"
    utc_after="$(date -u +%-H)"
    zone_hour="$(sed -n 's/.*"key":"infopanel:\/clock\/24-hour","now":\([0-9]\{1,2\}\)[,}].*/\1/p' <<< "$reply")"
    [ -z "$zone_hour" ] || break
    sleep 1
  done
fi
echo "The clock's hour in Europe/Berlin: ${zone_hour:-none}; UTC's: $utc_before (before), $utc_after (after)"
kill -TERM "$pid" 2>/dev/null || true
code=0
wait "$pid" || code=$?
echo "The engine stopped with exit code $code"
echo "--- its log, the token taken out:"
sed "s/$token/<token>/g" /tmp/engine.log
echo "---"
[ -n "$session" ] || { echo "smoke.sh: the engine didn't answer GET /api/session within 60 s" >&2; exit 1; }
[ "$code" -eq 0 ] || { echo "smoke.sh: the engine's exit code was $code, not 0" >&2; exit 1; }
[[ "$session" == *"\"engineVersion\":\"$expected_version\""* ]] ||
  { echo "smoke.sh: engineVersion isn't $expected_version" >&2; exit 1; }
[ -z "$expected_build" ] || [[ "$session" == *"\"engineBuild\":\"$expected_build\""* ]] ||
  { echo "smoke.sh: engineBuild isn't $expected_build" >&2; exit 1; }
[ -n "$zone_hour" ] || { echo "smoke.sh: GET /api/sensors/values has no reading of infopanel:/clock/24-hour" >&2; exit 1; }
zone_ok=0
for utc in "$utc_before" "$utc_after"; do
  case $(( (zone_hour - utc + 24) % 24 )) in 1|2) zone_ok=1 ;; esac
done
[ "$zone_ok" -eq 1 ] ||
  { echo "smoke.sh: the engine's hour in Europe/Berlin isn't 1 or 2 hours past UTC's: the zone didn't resolve" >&2; exit 1; }

step "apt-get remove infopanel"
apt-get remove -y -q infopanel
left=""
while IFS= read -r path; do
  # Folders it shares with other packages stay; its own files and links go, but the conffile.
  if [ -e "$path" ] || [ -L "$path" ]; then
    [ -d "$path" ] && [ ! -L "$path" ] && continue
    left="$left$path"$'\n'
  fi
done < /tmp/installed.txt
echo "Left after the removal: ${left:-nothing}"
[ "$left" = $'/etc/xdg/autostart/infopanel.desktop\n' ] ||
  { echo "smoke.sh: the removal left more than the conffile" >&2; exit 1; }
[ ! -e /usr/lib/infopanel ] || { echo "smoke.sh: /usr/lib/infopanel is still there" >&2; exit 1; }
dpkg -l infopanel | tail -n 1

step "apt-get purge infopanel, then autoremove what it pulled in"
apt-get purge -y -q infopanel
apt-get autoremove -y -q --purge
[ ! -e /etc/xdg/autostart/infopanel.desktop ] || { echo "smoke.sh: the purge left the sign-in entry" >&2; exit 1; }
! dpkg -l infopanel >/dev/null 2>&1 || dpkg -l infopanel | tail -n 1
snapshot > /tmp/after.txt
dpkg-query -W -f '${Package}\n' | LC_ALL=C sort > /tmp/packages-after.txt
echo "Packages that differ from before the install (< before, > after; autoremove keeps what an installed package recommends):"
diff /tmp/packages-before.txt /tmp/packages-after.txt || true
echo "Paths that differ from before the install (< before, > after):"
diff /tmp/before.txt /tmp/after.txt || true
# Nothing of InfoPanel's is left outside the user's home (its data and caches), and nothing that was there is gone.
gone="$(diff /tmp/before.txt /tmp/after.txt | grep '^<' || true)"
# (The home itself is new when this script made the user, after the first snapshot.)
left="$(diff /tmp/before.txt /tmp/after.txt | grep '^>' | grep -v -e "^> $home/" -e "^> $home\$" | grep -i infopanel || true)"
[ -z "$gone" ] || { echo "smoke.sh: paths that were there before are gone" >&2; exit 1; }
[ -z "$left" ] || { echo "smoke.sh: InfoPanel's paths are left outside the user's home" >&2; exit 1; }
echo
echo "smoke.sh: installed, answered as $expected_version, stopped with 0, removed and purged; nothing of InfoPanel's left outside the user's home"
