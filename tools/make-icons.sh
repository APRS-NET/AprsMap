#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
# Regenerates every app icon from build/branding/icon.svg (docs/branding.md). Needs ImageMagick 7 (`magick`) with SVG
# support. The outputs are committed, so building the apps never needs ImageMagick.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/build/branding/icon.svg"
out="$root/build/branding"
web="$root/src/AprsMap.Browser/wwwroot"

render() { magick -background none -density 384 "$src" -resize "$1x$1" "PNG32:$2"; }

render 256 "$out/icon-256.png"                 # desktop window icon (all platforms)
for size in 16 24 32 48 64 128; do render "$size" "$out/icon-$size.tmp.png"; done
magick "$out"/icon-{16,24,32,48,64,128}.tmp.png "$out/icon-256.png" "$out/icon.ico"   # Windows executable icon
rm -f "$out"/icon-*.tmp.png

cp "$src" "$web/favicon.svg"                     # browser tab icon, scales to any size
render 32 "$web/favicon.png"                     # fallback for browsers without SVG favicons
render 180 "$web/apple-touch-icon.png"           # iOS home screen
echo "Icons written to $out and $web"
