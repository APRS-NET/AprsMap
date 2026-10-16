#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
# Installs a desktop entry and the app icon for the current user on Linux (docs/branding.md), so GNOME, KDE and other
# freedesktop shells show the icon in the dock, the app grid and Alt+Tab. Those shells take a window's icon from the
# desktop entry whose StartupWMClass matches the window's class (the executable's name, the product slug), and GNOME
# ignores the icon the window itself carries.
#
# usage: tools/install-linux-desktop.sh [EXECUTABLE]   (default: the Release build of the desktop app)
#        tools/install-linux-desktop.sh --uninstall
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
prop() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" "$root/build/Branding.props" | head -1; }
name="$(prop ProductName)"
display="$(prop ProductDisplayName)"
slug="$(prop ProductSlug)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
entry="$data/applications/$slug.desktop"
icon="$data/icons/hicolor/scalable/apps/$slug.svg"

refresh() {
  update-desktop-database "$data/applications" 2>/dev/null || true
  gtk-update-icon-cache -q -t "$data/icons/hicolor" 2>/dev/null || true
}

if [[ "${1:-}" == "--uninstall" ]]; then
  rm -f "$entry" "$icon"
  refresh
  echo "Removed $entry and $icon"
  exit 0
fi

exe="$(realpath -m "${1:-$root/src/$name.Desktop/bin/Release/net10.0/$slug}")"
if [[ ! -x "$exe" ]]; then
  echo "No executable at $exe; build it first (dotnet build -c Release) or pass its path." >&2
  exit 1
fi

mkdir -p "$(dirname "$entry")" "$(dirname "$icon")"
cp "$root/build/branding/icon.svg" "$icon"
cat > "$entry" <<EOF
[Desktop Entry]
Type=Application
Name=$display
GenericName=APRS map
Comment=Show APRS stations on a map
Exec="$exe"
Path=$(dirname "$exe")
Icon=$slug
StartupWMClass=$slug
Terminal=false
Categories=Network;HamRadio;
Keywords=APRS;ham;amateur;radio;map;
EOF
refresh
echo "Installed $entry (runs $exe) and $icon"
