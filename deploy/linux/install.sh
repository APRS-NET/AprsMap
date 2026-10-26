#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
# Installs the AprsMap gateway as a system service on Ubuntu (docs/features/F1.37-gateway-service.md, behavior 5):
#
#   mkdir aprsmap-gateway && tar -xzf aprsmap-gateway-vX-linux-x64.tar.gz -C aprsmap-gateway
#   sudo aprsmap-gateway/install.sh aprsmap-gateway-vX-linux-x64.tar.gz
#
# Puts the gateway in /opt/aprsmap-gateway, creates the aprsmap user (in dialout, for a USB TNC), installs the unit
# and, if there's none yet, the sample /etc/aprsmap/gateway.toml, then enables the service and starts it (on a first
# install, not until you've edited the file). Run it again with a newer tarball to update; the configuration and the
# station's data are kept.
set -euo pipefail

tarball="${1:?usage: sudo $0 aprsmap-gateway-vX-linux-ARCH.tar.gz}"
here="$(cd "$(dirname "$0")" && pwd)"
[ "$(id -u)" -eq 0 ] || { echo "Run it with sudo." >&2; exit 1; }
[ -f "$tarball" ] || { echo "No such file: $tarball" >&2; exit 1; }

if ! id aprsmap >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/aprsmap --no-create-home --shell /usr/sbin/nologin aprsmap
fi
usermod -a -G dialout aprsmap

systemctl stop aprsmap-gateway 2>/dev/null || true
rm -rf /opt/aprsmap-gateway.new
mkdir -p /opt/aprsmap-gateway.new
tar -xzf "$tarball" -C /opt/aprsmap-gateway.new
rm -rf /opt/aprsmap-gateway
mv /opt/aprsmap-gateway.new /opt/aprsmap-gateway
chmod +x /opt/aprsmap-gateway/aprsmap-gateway

install -d -m 0750 -o aprsmap -g aprsmap /etc/aprsmap /var/lib/aprsmap
fresh=false
if [ ! -f /etc/aprsmap/gateway.toml ]; then
  install -m 0640 -o aprsmap -g aprsmap "$here/gateway.toml" /etc/aprsmap/gateway.toml
  fresh=true
fi
install -m 0644 "$here/aprsmap-gateway.service" /etc/systemd/system/aprsmap-gateway.service
# The updater (F1.40): the admin web app's Update and restart, run as root when the gateway asks.
install -m 0644 "$here/aprsmap-gateway-update.path" /etc/systemd/system/aprsmap-gateway-update.path
install -m 0644 "$here/aprsmap-gateway-update.service" /etc/systemd/system/aprsmap-gateway-update.service
install -d /usr/local/libexec
install -m 0755 "$here/aprsmap-gateway-update.sh" /usr/local/libexec/aprsmap-gateway-update
systemctl daemon-reload
systemctl enable aprsmap-gateway
systemctl enable --now aprsmap-gateway-update.path
if $fresh; then
  # The first start stores the station from the file, so it waits until the file is the station's.
  echo "Installed /etc/aprsmap/gateway.toml: set your callsign, TNC and gpsd there, then: sudo systemctl start aprsmap-gateway"
else
  systemctl start aprsmap-gateway
  systemctl --no-pager --lines=5 status aprsmap-gateway || true
fi
echo "Published web app: http://$(hostname):8080/   Admin web app: http://127.0.0.1:8081/ (on this PC)"
echo "Set up an operator: sudo -u aprsmap /opt/aprsmap-gateway/aprsmap-gateway operator add NAME --config /etc/aprsmap/gateway.toml"
