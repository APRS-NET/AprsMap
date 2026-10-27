# Deployment

## Web app: Docker first

The gateway image contains the ASP.NET Core gateway, the engine and the browser app (as static files). One container
serves everything on port 8080.

```sh
docker run -d --name aprsmap -p 8080:8080 -v aprsmap-settings:/data \
  -e Engine__Callsign=YOURCALL-10 -e Engine__Filter="r/40.0/-105.0/50" \
  ghcr.io/aprs-net/aprsmap:alpha
```

Or with Compose, from a clone: `APRS_CALLSIGN=YOURCALL-10 docker compose up --build`.

The station's settings (its profiles) are stored in `/data/settings.json`; keep `/data` on a volume. The callsign,
passcode, server and filter variables are **first-run defaults**: when `/data` has no settings yet, the gateway creates
its first profile from them. After that the stored settings win and the variables are ignored; to start over, remove
`settings.json`. Without a callsign the gateway runs but doesn't connect, and the web app says it isn't set up. APRS-IS
servers reject `N0CALL`, so use your own callsign (receive-only needs no passcode).

| Setting | Environment variable | Default |
| --- | --- | --- |
| Connect to APRS-IS | `Engine__Enabled` | `true` |
| Settings folder | `Engine__SettingsFolder` | `/data` in the image |
| First-run login callsign | `Engine__Callsign` | none (not set up) |
| First-run passcode (`-1` = receive-only) | `Engine__Passcode` | `-1` |
| First-run server | `Engine__Server` | `rotate.aprs2.net:14580` |
| First-run server-side filter | `Engine__Filter` | none |
| KISS over TCP source: host, port, radio port, name (with `Engine__Server` empty, a TNC-only station). Set from here on every start, not only the first | `Engine__Kiss__Host`, `__Port`, `__TncPort`, `__Name` | none |
| USB TNC source (KISS over serial): port, baud, radio port, name. Set from here on every start | `Engine__KissSerial__Port`, `__Baud`, `__TncPort`, `__Name` | none (baud `9600`) |
| gpsd source: host, port, name. Set from here on every start | `Engine__Gpsd__Host`, `__Port`, `__Name` | none (port `2947`) |
| Receive only for good: nothing is sent, and APRS-IS logs in unverified | `Engine__ReceiveOnly` | `false` |
| Start with the APRS-IS switch off every time, to connect it by hand (it otherwise keeps its last state) | `Engine__AprsIsOnStart` | `true` |
| A second port where an operator signs in; the main port then never offers sign-in | `Gateway__OperatorPort` | none |
| Where the published app listens, `address:port` (with either of these, they replace the ports above) | `Gateway__Listen` | `0.0.0.0:8080` |
| Where the operator (admin) app listens, `address:port`; wins over `Gateway__OperatorPort` | `Gateway__OperatorListen` | none |
| How much of the station's location viewers learn: `Exact`, `Grid` (its 4-character square) or `Hidden` | `Gateway__HomePrivacy` | `Grid` with an operator port, `Exact` without |
| The page's title, beside the product name and on the browser tab | `Gateway__Title` | none |
| Let browsers change settings and switch profiles, with no operator set up | `Gateway__AllowSettingsChanges` | `false` |
| Let operators sign in, and apps use the API key, over plain HTTP from other computers (a trusted network only) | `Gateway__AllowInsecureSignIn` | `false` |
| Operators set up in configuration: a name and a hash from `operator add` | `Gateway__Operators__0__Name`, `…__PasswordHash` | none (use `operators.json`) |
| Other sites whose pages may use the hub and sign-in | `Gateway__AllowedOrigins__0`, `__1`… | none (the gateway's own only) |
| Serve under a path, for a proxy that maps one to the gateway | `Gateway__PathBase` | none (the root) |
| Proxies trusted for `X-Forwarded-For`/`-Proto`: addresses or networks | `Gateway__KnownProxies__0`, `__1`… | none (forwarded headers ignored) |
| Who may show the app in a frame (CSP `frame-ancestors`) | `Gateway__FrameAncestors` | `'self'` |
| Most browsers connected at once, in all / from one address | `Gateway__MaxViewers` / `Gateway__MaxViewersPerAddress` | `100` / `5` |
| Hub calls per second per browser, and the burst above it | `Gateway__CallsPerSecond` / `Gateway__CallBurst` | `20` / `40` |
| Packets kept for new viewers | `Engine__RecentPacketCapacity` | `5000` |
| Address lookup server (Nominatim); empty turns lookup off | `Geocoding__Endpoint` | `https://nominatim.openstreetmap.org` |
| Map tile cache folder | `Tiles__CacheFolder` | `/data/tiles` in the image |
| Tile cache size per provider, in MB (least recently used tiles go first) | `Tiles__MaxCacheMegabytes` | `512` |
| Tile providers, by key: `Name`, `UrlTemplate` (`{z}`, `{x}`, `{y}` or `{-y}`), `Attribution`, `AttributionUrl`, `MinZoom`, `MaxZoom`, `ImageType` | `Tiles__Providers__<key>__…` | OpenStreetMap as `osm` |
| That server's usage policy, linked in the setup wizard | `Geocoding__UsagePolicyUrl` | the OSMF policy for the public server |
| OpenTelemetry export | `OTEL_EXPORTER_OTLP_ENDPOINT` (+ standard `OTEL_*`); more targets in Settings › Logging | off |
| Log levels; overrides Settings › Logging | `Logging__LogLevel__Default`, `Logging__LogLevel__AprsMap.Connections` and the other areas' categories | `Information` |
| Log file | Settings › Logging (operator) | off on a gateway; `/data/logs` when on |

Browsers see the gateway's settings read-only, with passcodes hidden, until an operator signs in (below). A profile
switch changes the station for every viewer, so it is an operator action. On a gateway with no operator set up,
`Gateway__AllowSettingsChanges=true` lets *any* browser do it and see passcodes; set it only on a network where everyone
may operate the station. Once an operator is set up it's ignored, with a warning at start: signing in decides. The
same switch gates address lookup and the passcode calculator in the setup wizard, so a public gateway is not an open
geocoding proxy. Lookups go through the gateway, at most one a second, with results cached in memory; a gateway with
many users should point `Geocoding__Endpoint` at its own Nominatim, as the public server's usage policy asks.

Browsers get map tiles from the gateway's `/tiles/{provider}/{z}/{x}/{y}`, never from the tile server directly: one
cache serves every viewer, the gateway identifies the app to the tile server, and only configured providers and real
tile coordinates are served. The cache follows the OpenStreetMap tile usage policy (no prefetching, caching and
revalidation as the server says). A busy public map should use a tile provider whose terms allow its traffic.

### Operators: signing in to transmit

Only a signed-in operator may transmit, change the station's settings, read its messages or type to its TNC from a
browser (docs/features/F2.9-operator-sign-in.md). Operators are set up on the host, never from a browser:

```sh
docker exec -it aprsmap aprsmap-gateway operator add NAME       # asks for the password twice
docker exec -it aprsmap aprsmap-gateway operator password NAME  # changes it, ending NAME's sessions
docker exec -it aprsmap aprsmap-gateway operator remove NAME
docker exec -it aprsmap aprsmap-gateway operator list
```

Passwords have at least 12 characters and are stored in `/data/operators.json` as salted PBKDF2-SHA256 hashes; the
gateway reads the file again when it changes. Sign in from the web app's menu. Sign-in is offered over HTTPS only (or
from the gateway's own host), so put the gateway behind a TLS proxy and list it in `Gateway__KnownProxies`. A session
lasts 12 hours from its last use and 7 days at most, in a `__Host-` cookie signed with keys kept in `/data/keys`;
sessions are kept in memory, so restarting the gateway signs everyone out. After 5 failed sign-ins from one address in
15 minutes that address waits 15 minutes; after 10 for one name in an hour that name waits an hour. Sign-ins, failures,
waits, sign-outs and every transmitting call are logged with the operator's name and address, never the password.

#### An API key for apps

An app, such as the desktop app used as the gateway's client (F1.39), gets the operator's rights with the gateway's API
key instead of a password (docs/features/F1.38-gateway-api-key.md). A signed-in operator sets it in the web app's
**Settings › Gateway access**: **Generate**, **Copy**, then **Apply**. The key is kept only as a hash in `api-key.json`
in the data folder, so copy it before applying. Switching **Let apps in with an API key** off and applying removes the
key; applying a new key disconnects the apps using the old one. The app sends it as `Authorization: Bearer <key>`, which
the gateway accepts only on the operator port, over HTTPS or from the gateway's own host. Wrong keys count as failed
sign-ins from that address, and each accepted or refused key is logged, never the key.

Endpoints:

| Path | Purpose |
| --- | --- |
| `/` | The web app (all paths move under `Gateway__PathBase` when it's set) |
| `/hubs/engine` | SignalR hub the web app uses |
| `/tiles/providers`, `/tiles/{provider}/{z}/{x}/{y}` | Map tile providers and tiles for the web app, from the gateway's cache |
| `/auth/sign-in`, `/auth/sign-out`, `/auth/me` | Operator sign-in (F2.9); `/auth/me` with an API key says whether the gateway took it (F1.38) |
| `/healthz` | Health check for orchestrators (the chiseled image has no shell, so probe it over HTTP) |
| `/source` | Redirects to the source code, the AGPL-3.0 network-use offer |

The image is based on `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`: no shell or package manager, running as
the non-root `app` user. Put a reverse proxy with TLS in front of it for anything beyond a LAN, and pass WebSocket
upgrades through for `/hubs/engine`. Give the station a passcode only once an operator is set up: until then nobody can
transmit from a browser anyway.

### A TNC on a serial port

A KISS TNC on USB serial (a radio's built-in TNC, a TNC-X, a NinoTNC) plugs into the gateway's host, and the container
needs the device passed in, plus access to it: the image runs as the non-root `app` user, so add the device's group.

```sh
docker run -d --name aprsmap -p 8080:8080 -v aprsmap-settings:/data \
  --device /dev/serial/by-id/usb-Silicon_Labs_CP2102-if00-port0:/dev/ttyTNC \
  --group-add "$(stat -c %g /dev/serial/by-id/usb-Silicon_Labs_CP2102-if00-port0)" \
  ghcr.io/aprs-net/aprsmap:alpha
```

Then add a **KISS over serial** source on `/dev/ttyTNC` in Settings › Sources (an operator action, like any source
change; the gateway's serial ports are only listed to an operator). Use the `/dev/serial/by-id/` name on the host: it
stays the same when the TNC is plugged in again, where `/dev/ttyUSB0` may come back as `/dev/ttyUSB1`. With Compose,
the same goes under `devices:` and `group_add:`. A device passed in must be plugged in when the container starts; after
unplugging the TNC, restart the container once it's back.

### Behind a reverse proxy

The gateway can sit behind a proxy at the root of a host or under a path. Under a path, set `Gateway__PathBase` and
have the proxy pass the path through unchanged; the web app finds its hub under its own address, so it needs no
setting. Name the proxy in `Gateway__KnownProxies` so the gateway sees viewers' real addresses and the original
scheme; forwarded headers from anyone else are ignored. With Compose and Caddy (TLS from Let's Encrypt):

```yaml
services:
  aprsmap:
    image: ghcr.io/aprs-net/aprsmap:alpha
    volumes: [aprsmap-settings:/data]
    environment:
      Engine__Callsign: YOURCALL-10
      Gateway__PathBase: /aprs
      Gateway__KnownProxies__0: 172.30.0.0/24
    networks: [web]
  caddy:
    image: caddy:2
    ports: ["80:80", "443:443"]
    command: >
      sh -c "printf 'aprs.example.org {\n handle /aprs/* {\n  reverse_proxy aprsmap:8080\n }\n}\n' > /etc/caddy/Caddyfile
      && caddy run --config /etc/caddy/Caddyfile"
    networks: [web]
networks:
  web:
    ipam: { config: [{ subnet: 172.30.0.0/24 }] }
volumes:
  aprsmap-settings:
```

Caddy passes WebSocket upgrades through by itself; other proxies need it set for `/hubs/engine`. Every response
carries a Content-Security-Policy (the gateway's own origin only, with WebAssembly), `X-Content-Type-Options: nosniff`
and `Referrer-Policy: same-origin`. The app's files are served Brotli- or gzip-compressed. Files named with their
content hash are cached for a year, and the rest (`index.html`, the boot scripts) revalidate on every load, so an
upgrade reaches browsers the next time they open the page.

Tags: `X.Y.Z-alpha.N` for each release, and `alpha` for the newest. `latest` starts at 1.0.

### A public page of what your TNC hears

A gateway can be an always-on public page of what your TNC hears, safe to share, with the station's settings changed
privately (docs/features/F1.26-public-console.md). The pieces:

- **Two ports.** The main port (8080) is for everyone: they watch and can't change or transmit anything, and they don't
  see the controls that would. `Gateway__OperatorPort` (8081) is where you sign in as the operator (above). Publish it on
  the host's loopback only and reach it over SSH (`ssh -L 8081:127.0.0.1:8081 yourserver`, then open
  `http://localhost:8081`; signing in over the tunnel counts as the host's own) or over a VPN such as Tailscale. A
  session from the operator port isn't honoured on the public one, and nothing a browser or proxy sends can move
  sign-in there: the gateway goes by the port the connection arrived on.
- **Receive only.** `Engine__ReceiveOnly=true` refuses every send whatever the settings, and logs in to APRS-IS with
  passcode `-1`, which a server won't take packets from.
- **Your location.** With an operator port, viewers get `Gateway__HomePrivacy=Grid` unless you set it: the house is
  drawn at the centre of your 4-character Maidenhead square (about 110 × 220 km), and every distance, bearing, filter
  circle and route end is measured from there, so it can't be worked back to your door. `Hidden` shows no location at
  all. You, signed in, see it exactly. A position you beacon is public on APRS-IS anyway.
- **A title.** `Gateway__Title`, such as "What YOURCALL's TNC hears", heads the page and the browser tab.
- **Map tiles.** The gateway fetches and caches tiles for every viewer. OpenStreetMap's tile servers allow light use
  only, so a busy public page should use a provider whose terms allow its traffic, with your own key, or a tile server
  you run: set `Tiles__Providers__<key>__…` (above).
- **No setup screen needed.** On an empty `/data`, `Engine__Callsign` with `Engine__Kiss__Host` and an empty
  `Engine__Server` starts a TNC-only station. After the first start the stored settings win; change them on the operator
  port.

On a home server such as Unraid, with `/data` in appdata (owned by the image's `app` user, UID 1654):

```yaml
services:
  aprsmap:
    image: ghcr.io/aprs-net/aprsmap:alpha
    restart: unless-stopped
    ports:
      - "8080:8080"             # the public page, behind your TLS proxy
      - "127.0.0.1:8081:8081"   # the operator port: this host only
    volumes:
      - /mnt/user/appdata/aprsmap:/data
    environment:
      Engine__Callsign: YOURCALL-10
      Engine__Server: ""                 # no APRS-IS: what the TNC hears
      Engine__Kiss__Host: 192.168.1.50   # the Raspberry Pi below
      Engine__Kiss__Port: "8001"
      Engine__Kiss__Name: Pi TNC
      Engine__ReceiveOnly: "true"
      Gateway__OperatorPort: "8081"
      Gateway__Title: What YOURCALL's TNC hears
      Gateway__KnownProxies__0: 192.168.1.2    # your reverse proxy
      Gateway__MaxViewers: "50"
      Gateway__MaxViewersPerAddress: "3"
```

Put Nginx Proxy Manager, SWAG, Caddy or a Cloudflare Tunnel in front of port 8080 for TLS, name it in
`Gateway__KnownProxies`, and pass WebSocket upgrades for `/hubs/engine` (see "Behind a reverse proxy").

**The TNC on another computer.** A Raspberry Pi with the TNC (a TNC-X or NinoTNC) on USB can serve it as KISS over TCP,
and the gateway connects to it as a KISS over TCP source, so what it hears is heard by this station (F3.1 behavior 8).
No USB device is passed into the container. On the Pi, `ser2net` bridges the serial port to a TCP port as raw bytes,
which is KISS over TCP; in `/etc/ser2net.yaml`:

```yaml
connection: &tnc
  accepter: tcp,8001
  connector: serialdev,/dev/ttyUSB0,9600n81,local
  options:
    kickolduser: true
```

To pass a USB TNC into the container instead, see "A TNC on a serial port".

## Gateway as a Linux service

Without Docker, the gateway runs as a systemd service on a Linux PC, for example one with the TNC on USB and gpsd
already running. Each release has `aprsmap-gateway-<version>-linux-x64.tar.gz` and `…-linux-arm64.tar.gz`:
self-contained, with the web app in it, so nothing else needs installing.

```sh
mkdir aprsmap-gateway && tar -xzf aprsmap-gateway-v0.8.0-linux-x64.tar.gz -C aprsmap-gateway
sudo aprsmap-gateway/install.sh aprsmap-gateway-v0.8.0-linux-x64.tar.gz
sudoedit /etc/aprsmap/gateway.toml   # your callsign, TNC and gpsd, before the first start
sudo systemctl start aprsmap-gateway
sudo -u aprsmap /opt/aprsmap-gateway/aprsmap-gateway operator add YOURNAME --config /etc/aprsmap/gateway.toml
journalctl -u aprsmap-gateway -f
```

The script installs the gateway in `/opt/aprsmap-gateway`, creates the `aprsmap` user (in `dialout`, for the TNC),
installs `aprsmap-gateway.service`, the updater (below) and a commented `/etc/aprsmap/gateway.toml` (never over one you
already have), and enables the service. On a first install it doesn't start it: the first start stores the station
(callsign, passcode, server) from the file, and after that the admin web app changes them. The TNC and gpsd follow the
file on every start. Run the script again with a newer
tarball to update; it keeps the file and the data in `/var/lib/aprsmap`, and restarts the service.

The TOML file holds the same settings as the table above: each table is a section and `snake_case` keys are the
settings' names, so `[gateway] operator_listen` is `Gateway__OperatorListen` and `[engine.kiss_serial] port` is
`Engine__KissSerial__Port`. It's read after `appsettings.json`, so environment variables still win. Use it anywhere
with `--config PATH` or `APRSMAP_CONFIG`; a file that isn't valid TOML stops the gateway with the line at fault.

```toml
[gateway]
listen = "0.0.0.0:8080"            # the published, read-only app, on every address
operator_listen = "127.0.0.1:8081" # the admin app: only on this PC; 0.0.0.0:8081 to reach it from the network

[engine]
settings_folder = "/var/lib/aprsmap"
callsign = "N0CALL-10"             # first run only; the devices below are set from here on every start

[engine.kiss_serial]
port = "/dev/serial/by-id/usb-Silicon_Labs_CP2102-if00-port0"

[engine.gpsd]
host = "localhost"
```

**Updating and restarting.** When a new version is out, the operator sees it in the admin web app's status bar; its
dialog has **Update and restart** (docs/features/F1.40-gateway-service-updates.md). The gateway only leaves a request
in `/var/lib/aprsmap`; `aprsmap-gateway-update.path` starts the updater as root, which downloads the release's tarball
for this machine and its `.sha256`, checks it, swaps it into `/opt/aprsmap-gateway` (keeping the old one as
`/opt/aprsmap-gateway.previous`) and restarts the service. If the new version doesn't stay up for 30 seconds, the old
one is put back. Your TOML file and data are kept. Help › About has **Restart gateway**. Its log:
`journalctl -u aprsmap-gateway-update`. A Docker gateway has neither: Docker restarts it, and you update it by pulling
the new image.

The ports are 8080 and 8081, and the desktop app's web viewer uses 8090, so a desktop app can run beside the service on
the same PC.

## Desktop app

### Installing

Each release attaches an installer for every platform, built with Velopack. An installed app updates itself (F4.15):

| OS | Installer | Where the program goes |
| --- | --- | --- |
| Windows x64, arm64 | the `.msi` for `win-<arch>` | `C:\Program Files\<publisher>\<product>`, for every user of the PC |
| macOS x64, arm64 | the `.pkg` for `osx-<arch>` | `/Applications` (or `~/Applications`, which the installer offers) |
| Linux x64, arm64 | `aprsmap-linux-<arch>.AppImage` | wherever you put it; `~/Applications` is usual |

- **Windows:** installing asks for administrator approval, and so does each update, because Program Files is
  protected. An administrator can install somewhere else with `msiexec /i <file>.msi VELOPACK_INSTALLDIR="D:\Apps\..."`.
- **macOS:** the package is unsigned for now, so open it with right-click › Open the first time.
- **Linux:** make the AppImage executable (`chmod +x`) and run it. On its first start it adds itself to the applications
  menu (`~/.local/share/applications`, with its icon under `~/.local/share/icons`), and updates replace the AppImage
  in place. If it's somewhere only root can write to, an update asks for your password through `pkexec`.
- **Unsigned:** the Windows installer isn't signed yet, so SmartScreen warns on the first install, but not on updates,
  which the app applies itself. Code signing is scope F4.8.

The self-contained archives (`aprsmap-v<version>-<rid>.zip` or `.tar.gz`) are still attached. Unpack one anywhere and run
`aprsmap` (`aprsmap.exe` on Windows). An archive can't update itself, so it offers **Download** instead.

### The desktop app as a gateway's client

The desktop app can show a gateway's station instead of running its own (docs/features/F1.39-desktop-gateway-client.md),
for example on the PC that runs the gateway service, or on another one. In **Settings › Connection** choose **A
gateway**, enter its admin address (`http://127.0.0.1:8081/` on the gateway's own PC) and, to transmit and change the
gateway's settings, its API key (above); then **Test**, which checks the address, the key and the two versions
without saving, and **Apply and restart**. `--gateway URL` does the same for one run.

As a client the app runs no station of its own: no APRS-IS or TNC connection, GPS, beacons or history, and its web
viewer is off. Everything is received and processed once, by the gateway; the map's tiles come through it too. Without
the key, or with one the gateway refuses, the app only watches, and Settings › Connection says why. The app still
checks for its own updates. The choice and the key are kept in `gateway-client.json` in the app's settings folder,
readable by your user only; **This computer** and **Apply and restart** go back to the app's own station, whose settings
are untouched.

### Where the app keeps its files

Nothing is kept beside the program, so an update or an uninstall leaves your settings alone. Each kind of file goes
where the operating system expects it:

| | Windows | Linux | macOS |
| --- | --- | --- | --- |
| Settings, profiles, layout | `%APPDATA%\aprsnet\aprsmap` | `~/.config/aprsnet/aprsmap` | `~/Library/Application Support/aprsnet/aprsmap` |
| Message history, captures | `%LOCALAPPDATA%\aprsnet\aprsmap` | `~/.local/share/aprsnet/aprsmap` | as settings |
| Map tiles | `%LOCALAPPDATA%\aprsnet\aprsmap\tiles` | `~/.cache/aprsnet/aprsmap/tiles` | `~/Library/Caches/aprsnet/aprsmap/tiles` |
| Logs (Settings › Logging; one file a UTC day, kept 14 days, 100 MB at most) | `%LOCALAPPDATA%\aprsnet\aprsmap\logs` | `~/.local/share/aprsnet/aprsmap/logs` | `~/Library/Application Support/aprsnet/aprsmap/logs` |
| Defaults for every user | `%ProgramData%\aprsnet\aprsmap\appsettings.json` | `/etc/xdg/aprsnet/aprsmap/appsettings.json` | `/Library/Application Support/aprsnet/aprsmap/appsettings.json` |

- **Linux:** follows the XDG Base Directory specification, so `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_CACHE_HOME` and
  `XDG_CONFIG_DIRS` move these folders. The app creates its folders readable only by you, because the settings hold
  passcodes.
- **Earlier versions:** on Linux, versions before 0.5 kept everything in `~/.aprsnet/aprsmap`, and on Windows the
  message history sat with the settings in `%APPDATA%`. The first start of a newer version moves those files to the
  folders above, never replacing one that's already there, and removes the old folder once it's empty.
- **Defaults for every user:** `appsettings.json` beside the program holds the shipped defaults, and an update replaces
  it. To set defaults for every user of a machine, such as `"Engine": { "Updates": { "Check": false } }`, an
  administrator puts an `appsettings.json` in the machine folder. It goes over the shipped file; environment
  variables and the command line still go over both.
- **Removing everything:** Tools › Start over… (or `--reset-all`) deletes the settings and preferences.
  Uninstalling leaves all of these folders in place.

Until the first-run wizard (scope F0.5) exists, set `Engine:Callsign` in `appsettings.json` (or the `Engine__Callsign`
environment variable) before the first start, or the app stays "not set up".
