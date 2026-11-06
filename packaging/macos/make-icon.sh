#!/bin/bash
# Makes packaging/macos/AppIcon.icns, the app's icon, from Images/logo.png with macOS's own sips and iconutil.
# The output is committed: run this only when the logo changes, then review and commit both.
#
#   packaging/macos/make-icon.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
logo="$root/Images/logo.png"
out="$here/AppIcon.icns"

[ -f "$logo" ] || { echo "make-icon.sh: no logo at $logo" >&2; exit 1; }

work="$(mktemp -d "${TMPDIR:-/tmp}/infopanel-icon.XXXXXX")"
set_dir="$work/AppIcon.iconset"
mkdir "$set_dir"

# Each size macOS asks an .icns for, at 1x and 2x.
for size in 16 32 128 256 512; do
  sips -s format png -z "$size" "$size" "$logo" --out "$set_dir/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -s format png -z "$double" "$double" "$logo" --out "$set_dir/icon_${size}x${size}@2x.png" >/dev/null
done

iconutil -c icns -o "$out" "$set_dir"

# Clean up the scratch iconset (files, then the folders).
rm -f "$set_dir"/*.png
rmdir "$set_dir" "$work"

echo "Wrote $out"
