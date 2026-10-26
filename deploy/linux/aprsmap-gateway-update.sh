#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
# The gateway's updater (docs/features/F1.40-gateway-service-updates.md, behavior 4), run as root by
# aprsmap-gateway-update.service when the gateway leaves update-request.json in its data folder. The request holds a
# version and nothing else: the addresses are built here, from the release address fixed at install and this
# machine's architecture. Downloads the release tarball and its checksum, checks it, swaps it in keeping the installed
# one, restarts the service and puts the previous one back if the new one doesn't stay running. Reports each step in
# update-status.json, which the gateway shows its operator. The APRSMAP_* variables below are for the tests.
set -euo pipefail
export LC_ALL=C # SemVer compares identifiers in ASCII order

data="${APRSMAP_DATA:-/var/lib/aprsmap}"
app="${APRSMAP_APP:-/opt/aprsmap-gateway}"
service="${APRSMAP_SERVICE:-aprsmap-gateway}"
settle="${APRSMAP_SETTLE_SECONDS:-30}"
units="${APRSMAP_UNITS:-/etc/systemd/system}"
libexec="${APRSMAP_LIBEXEC:-/usr/local/libexec}"
request="$data/update-request.json"
status="$data/update-status.json"
previous="$app.previous"
staging="$app.new"
version=""

# report STEP [REASON]: what the operator's notice shows, readable by the gateway's user.
report() {
  local reason="${2:-}"
  reason="${reason//[$'\n\r\t"\\']/ }"
  printf '{"version":"%s","step":"%s","reason":"%s"}\n' "$version" "$1" "$reason" >"$status.tmp"
  chown --reference="$data" "$status.tmp" 2>/dev/null || true
  chmod 0644 "$status.tmp"
  mv -f "$status.tmp" "$status"
  echo "update ${version:-?}: $1${reason:+: $reason}"
}

fail() {
  report failed "$1"
  exit 1
}

# semver_gt A B: whether version A is newer than B, by SemVer 2.0 precedence (build metadata ignored).
semver_gt() {
  local a="${1%%+*}" b="${2%%+*}"
  local a_main="${a%%-*}" b_main="${b%%-*}" a_pre="" b_pre=""
  [[ "$a" == *-* ]] && a_pre="${a#*-}"
  [[ "$b" == *-* ]] && b_pre="${b#*-}"
  local IFS=.
  local -a am=($a_main) bm=($b_main)
  for i in 0 1 2; do
    ((10#${am[i]} > 10#${bm[i]})) && return 0
    ((10#${am[i]} < 10#${bm[i]})) && return 1
  done
  # Same x.y.z: a release is newer than its pre-releases.
  [[ -z "$a_pre" && -n "$b_pre" ]] && return 0
  [[ -z "$b_pre" ]] && return 1
  local -a ap=($a_pre) bp=($b_pre)
  for ((i = 0; i < ${#ap[@]} || i < ${#bp[@]}; i++)); do
    ((i >= ${#ap[@]})) && return 1
    ((i >= ${#bp[@]})) && return 0
    local x="${ap[i]}" y="${bp[i]}"
    if [[ "$x" =~ ^[0-9]+$ && "$y" =~ ^[0-9]+$ ]]; then
      ((10#$x > 10#$y)) && return 0
      ((10#$x < 10#$y)) && return 1
    elif [[ "$x" =~ ^[0-9]+$ ]]; then
      return 1 # numbers sort before words
    elif [[ "$y" =~ ^[0-9]+$ ]]; then
      return 0
    elif [[ "$x" > "$y" ]]; then
      return 0
    elif [[ "$x" < "$y" ]]; then
      return 1
    fi
  done
  return 1
}

[[ "${1:-}" == "--semver-gt" ]] && { semver_gt "$2" "$3"; exit; } # for the tests

[ -f "$request" ] || exit 0
version="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$request" | head -n 1)"
rm -f "$request" # picked up, whatever comes of it
version="${version#v}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  version=""
  fail "the request isn't a version"
fi

installed="$(tr -d '[:space:]' <"$app/version" 2>/dev/null || true)"
installed="${installed#v}"
[[ "$installed" =~ ^[0-9]+\.[0-9]+\.[0-9]+ ]] || fail "the installed version isn't known ($app/version)"
semver_gt "$version" "$installed" || fail "$version isn't newer than $installed"

source="$(tr -d '[:space:]' <"$app/release-source" 2>/dev/null || true)"
[[ "$source" =~ ^https://github\.com/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$ ]] ||
  fail "the release address isn't known ($app/release-source)"
case "$(uname -m)" in
x86_64) rid=linux-x64 ;;
aarch64 | arm64) rid=linux-arm64 ;;
*) fail "no release for $(uname -m)" ;;
esac
program=""
for candidate in "$app"/*-gateway; do
  [ -f "$candidate" ] && [ -x "$candidate" ] && program="$(basename "$candidate")" && break
done
[ -n "$program" ] || fail "the installed gateway isn't in $app"
name="$program-v$version-$rid.tar.gz"
url="$source/releases/download/v$version/$name"

work="$(mktemp -d)"
trap 'rm -rf "$work" "$staging"' EXIT

report downloading
fetch() {
  if command -v curl >/dev/null; then
    curl -fsSL --proto '=https' --tlsv1.2 --retry 3 -o "$2" "$1"
  else
    wget -q --https-only -O "$2" "$1"
  fi
}
fetch "$url" "$work/$name" || fail "couldn't download $name"
fetch "$url.sha256" "$work/$name.sha256" || fail "couldn't download $name.sha256"

report checking
expected="$(awk 'NR == 1 { print tolower($1) }' "$work/$name.sha256")"
[[ "$expected" =~ ^[0-9a-f]{64}$ ]] || fail "the checksum file isn't a SHA-256"
actual="$(sha256sum "$work/$name" | awk '{ print $1 }')"
[ "$expected" = "$actual" ] || fail "the download doesn't match its checksum"

report installing
rm -rf "$staging"
mkdir -p "$staging"
tar -xzf "$work/$name" -C "$staging" --no-same-owner || fail "the download couldn't be unpacked"
[ -x "$staging/$program" ] || chmod +x "$staging/$program" 2>/dev/null || fail "the download has no $program"

# Puts the units and this updater from $app in place.
install_units() {
  install -m 0644 "$app/aprsmap-gateway.service" "$units/aprsmap-gateway.service"
  install -m 0644 "$app/aprsmap-gateway-update.path" "$units/aprsmap-gateway-update.path"
  install -m 0644 "$app/aprsmap-gateway-update.service" "$units/aprsmap-gateway-update.service"
  install -d "$libexec"
  # A copy then a rename, so this running script's own file is never written over.
  install -m 0755 "$app/aprsmap-gateway-update.sh" "$libexec/aprsmap-gateway-update.tmp"
  mv -f "$libexec/aprsmap-gateway-update.tmp" "$libexec/aprsmap-gateway-update"
  systemctl daemon-reload
}

# Whether the service started and stayed up for $settle seconds without systemd restarting it.
stays_up() {
  local before
  before="$(systemctl show -p NRestarts --value "$service")"
  sleep "$settle"
  [ "$(systemctl is-active "$service")" = active ] &&
    [ "$(systemctl show -p NRestarts --value "$service")" = "$before" ]
}

systemctl stop "$service"
rm -rf "$previous"
mv "$app" "$previous"
mv "$staging" "$app"
install_units
report restarting
systemctl start "$service" || true
if stays_up; then
  report done
  exit 0
fi

# The new version didn't stay up: the previous one back (behavior 4.5).
systemctl stop "$service" || true
rm -rf "$app.failed"
mv "$app" "$app.failed"
mv "$previous" "$app"
install_units
systemctl start "$service" || true
fail "$version didn't stay running, so $installed is back (journalctl -u $service)"
