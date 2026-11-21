#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
# Installs the zedMap gateway as a system service on Ubuntu (docs/features/F1.37-gateway-service.md, behavior 5):
#
#   mkdir zedmap-gateway && tar -xzf zedmap-gateway-vX-linux-x64.tar.gz -C zedmap-gateway
#   sudo zedmap-gateway/install.sh zedmap-gateway-vX-linux-x64.tar.gz
#
# Puts the gateway in /opt/zedmap-gateway, creates the zedmap user (in dialout, for a USB TNC), installs the unit
# and, if there's none yet, the sample /etc/zedmap/gateway.toml, then enables the service and starts it (on a first
# install, not until you've edited the file). Run it again with a newer tarball to update; the configuration and the
# station's data are kept.
#
#   sudo zedmap-gateway/install.sh --migrate-from OLD zedmap-gateway-vX-linux-x64.tar.gz
#
# Moves a gateway installed under an earlier name OLD over to this one (docs/features/F1.40-gateway-service-updates.md,
# behavior 14; the old updater runs it when it follows a rename, behavior 13): copies /etc/OLD and /var/lib/OLD to this
# name's folders, installs and starts this gateway, and once it stays running disables OLD's units. If it doesn't stay
# running, OLD is started again. OLD's folders, program and user are left in place, to remove by hand.
# The ZEDMAP_* variables below are for the tests.
set -euo pipefail

root="${ZEDMAP_ROOT:-}" # a folder standing in for /, for the tests
settle="${ZEDMAP_SETTLE_SECONDS:-30}"
migrate_from=""
if [ "${1:-}" = "--migrate-from" ]; then
  migrate_from="${2:?usage: sudo $0 --migrate-from OLD zedmap-gateway-vX-linux-ARCH.tar.gz}"
  shift 2
  [[ "$migrate_from" =~ ^[a-z][a-z0-9]{0,31}$ ]] || { echo "Not a slug: $migrate_from" >&2; exit 1; }
  [ "$migrate_from" != zedmap ] || { echo "It's already zedmap." >&2; exit 1; }
fi
tarball="${1:?usage: sudo $0 [--migrate-from OLD] zedmap-gateway-vX-linux-ARCH.tar.gz}"
here="$(cd "$(dirname "$0")" && pwd)"
[ "$(id -u)" -eq 0 ] || { echo "Run it with sudo." >&2; exit 1; }
[ -f "$tarball" ] || { echo "No such file: $tarball" >&2; exit 1; }

app="$root/opt/zedmap-gateway"
etc="$root/etc/zedmap"
data="$root/var/lib/zedmap"
units="$root/etc/systemd/system"
libexec="$root/usr/local/libexec"

# stays_up SERVICE: whether it started and stayed up for $settle seconds without systemd restarting it.
stays_up() {
  local before
  before="$(systemctl show -p NRestarts --value "$1")"
  sleep "$settle"
  [ "$(systemctl is-active "$1")" = active ] && [ "$(systemctl show -p NRestarts --value "$1")" = "$before" ]
}

if ! id zedmap >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/zedmap --no-create-home --shell /usr/sbin/nologin zedmap
fi
usermod -a -G dialout zedmap

if [ -n "$migrate_from" ]; then
  old_etc="$root/etc/$migrate_from"
  old_data="$root/var/lib/$migrate_from"
  echo "Moving the $migrate_from gateway to zedmap"
  systemctl stop "$migrate_from-gateway" 2>/dev/null || true
  # Copies, not moves: the old gateway can start again from its own folders if this one doesn't stay up.
  if [ -d "$old_etc" ] && [ ! -e "$etc" ]; then
    cp -a "$old_etc" "$etc"
    # Its folders, named in the file, become this name's.
    if [ -f "$etc/gateway.toml" ]; then
      sed -i -e "s#/var/lib/$migrate_from\b#/var/lib/zedmap#g" -e "s#/etc/$migrate_from\b#/etc/zedmap#g" \
        "$etc/gateway.toml"
    fi
    chown -R zedmap:zedmap "$etc"
  fi
  if [ -d "$old_data" ] && [ ! -e "$data" ]; then
    cp -a --reflink=auto "$old_data" "$data"
    rm -f "$data/update-request.json" "$data/update-status.json"
    chown -R zedmap:zedmap "$data"
  fi
fi

systemctl stop zedmap-gateway 2>/dev/null || true
rm -rf "$app.new"
mkdir -p "$app.new"
tar -xzf "$tarball" -C "$app.new"
rm -rf "$app"
mv "$app.new" "$app"
chmod +x "$app/zedmap-gateway"

install -d -m 0750 -o zedmap -g zedmap "$etc" "$data"
fresh=false
if [ ! -f "$etc/gateway.toml" ]; then
  install -m 0640 -o zedmap -g zedmap "$here/gateway.toml" "$etc/gateway.toml"
  fresh=true
fi
install -d "$units"
install -m 0644 "$here/zedmap-gateway.service" "$units/zedmap-gateway.service"
# The updater (F1.40): the admin web app's Update and restart, run as root when the gateway asks.
install -m 0644 "$here/zedmap-gateway-update.path" "$units/zedmap-gateway-update.path"
install -m 0644 "$here/zedmap-gateway-update.service" "$units/zedmap-gateway-update.service"
install -d "$libexec"
install -m 0755 "$here/zedmap-gateway-update.sh" "$libexec/zedmap-gateway-update"
systemctl daemon-reload
systemctl enable zedmap-gateway
systemctl enable --now zedmap-gateway-update.path

if [ -n "$migrate_from" ]; then
  version="$(tr -d '[:space:]' <"$app/version" 2>/dev/null || true)"
  systemctl start zedmap-gateway || true
  if stays_up zedmap-gateway; then
    # This one runs: the old name's units go, so nothing starts it again; its folders stay, for the operator.
    systemctl disable "$migrate_from-gateway" 2>/dev/null || true
    systemctl disable --now "$migrate_from-gateway-update.path" 2>/dev/null || true
    rm -f "$units/$migrate_from-gateway.service" "$units/$migrate_from-gateway-update.path" \
      "$units/$migrate_from-gateway-update.service" "$libexec/$migrate_from-gateway-update"
    systemctl daemon-reload
    # The notice after the restart (F1.40 behavior 5), in this name's data folder.
    printf '{"version":"%s","step":"done","reason":""}\n' "${version#v}" >"$data/update-status.json"
    chown zedmap:zedmap "$data/update-status.json" 2>/dev/null || true
    echo "Moved to zedmap. $migrate_from's folders are left: /opt/$migrate_from-gateway, /etc/$migrate_from," \
      "/var/lib/$migrate_from and the $migrate_from user can be removed once you're happy."
    exit 0
  fi
  # This one didn't stay up: the old one back, from the folders it never left.
  systemctl stop zedmap-gateway 2>/dev/null || true
  systemctl disable zedmap-gateway 2>/dev/null || true
  systemctl disable --now zedmap-gateway-update.path 2>/dev/null || true
  systemctl start "$migrate_from-gateway" || true
  echo "zedmap didn't stay running, so $migrate_from is back (journalctl -u zedmap-gateway)." >&2
  exit 1
fi

if $fresh; then
  # The first start stores the station from the file, so it waits until the file is the station's.
  echo "Installed /etc/zedmap/gateway.toml: set your callsign, TNC and gpsd there, then: sudo systemctl start zedmap-gateway"
else
  systemctl start zedmap-gateway
  systemctl --no-pager --lines=5 status zedmap-gateway || true
fi
echo "Published web app: http://$(hostname):8080/   Admin web app: http://127.0.0.1:8081/ (on this PC)"
echo "Set up an operator: sudo -u zedmap /opt/zedmap-gateway/zedmap-gateway operator add NAME --config /etc/zedmap/gateway.toml"
