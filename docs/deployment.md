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
| Let browsers change settings and switch profiles | `Gateway__AllowSettingsChanges` | `false` |
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
| OpenTelemetry export | `OTEL_EXPORTER_OTLP_ENDPOINT` (+ standard `OTEL_*`) | off |

Browsers see the gateway's settings read-only, with passcodes hidden. A profile switch changes the station for every
viewer, so it is an operator action; until operator sign-in exists (scope F2.9), `Gateway__AllowSettingsChanges=true`
lets *any* browser do it and see passcodes. Set it only on a network where everyone may operate the station. The
same switch gates address lookup and the passcode calculator in the setup wizard, so a public gateway is not an open
geocoding proxy. Lookups go through the gateway, at most one a second, with results cached in memory; a gateway with
many users should point `Geocoding__Endpoint` at its own Nominatim, as the public server's usage policy asks.

Browsers get map tiles from the gateway's `/tiles/{provider}/{z}/{x}/{y}`, never from the tile server directly: one
cache serves every viewer, the gateway identifies the app to the tile server, and only configured providers and real
tile coordinates are served. The cache follows the OpenStreetMap tile usage policy (no prefetching, caching and
revalidation as the server says). A busy public map should use a tile provider whose terms allow its traffic.

Endpoints:

| Path | Purpose |
| --- | --- |
| `/` | The web app (all paths move under `Gateway__PathBase` when it's set) |
| `/hubs/engine` | SignalR hub the web app uses |
| `/tiles/providers`, `/tiles/{provider}/{z}/{x}/{y}` | Map tile providers and tiles for the web app, from the gateway's cache |
| `/healthz` | Health check for orchestrators (the chiseled image has no shell, so probe it over HTTP) |
| `/source` | Redirects to the source code, the AGPL-3.0 network-use offer |

The image is based on `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`: no shell or package manager, running as
the non-root `app` user. Put a reverse proxy with TLS in front of it for anything beyond a LAN, and pass WebSocket
upgrades through for `/hubs/engine`. Operator sign-in for transmitting (scope F2.9) is not built yet, so the current
gateway is receive-only and should not be given a passcode.

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

## Desktop app

Each release attaches self-contained archives for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and
`osx-arm64`. Unpack and run `aprsmap` (`aprsmap.exe` on Windows). `appsettings.json` next to the executable holds
first-run defaults and logging; the user's settings (station profiles) live in their own folder:

| OS | Folder |
| --- | --- |
| Windows | `%APPDATA%\aprsnet\aprsmap` |
| Linux | `~/.aprsnet/aprsmap` |
| macOS | `~/Library/Application Support/aprsnet/aprsmap` |

Until the first-run wizard (scope F0.5) exists, set `Engine:Callsign` in `appsettings.json` (or the `Engine__Callsign`
environment variable) before the first start, or the app stays "not set up".

The archives are not signed yet. macOS will ask for confirmation on first launch (right-click › Open), and Windows
SmartScreen may warn. Installers, code signing and notarization are scope F4.8.
