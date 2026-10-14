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
| Packets kept for new viewers | `Engine__RecentPacketCapacity` | `500` |
| Address lookup server (Nominatim); empty turns lookup off | `Geocoding__Endpoint` | `https://nominatim.openstreetmap.org` |
| That server's usage policy, linked in the setup wizard | `Geocoding__UsagePolicyUrl` | the OSMF policy for the public server |
| OpenTelemetry export | `OTEL_EXPORTER_OTLP_ENDPOINT` (+ standard `OTEL_*`) | off |

Browsers see the gateway's settings read-only, with passcodes hidden. A profile switch changes the station for every
viewer, so it is an operator action; until operator sign-in exists (scope F2.9), `Gateway__AllowSettingsChanges=true`
lets *any* browser do it and see passcodes. Set it only on a network where everyone may operate the station. The
same switch gates address lookup and the passcode calculator in the setup wizard, so a public gateway is not an open
geocoding proxy. Lookups go through the gateway, at most one a second, with results cached in memory; a gateway with
many users should point `Geocoding__Endpoint` at its own Nominatim, as the public server's usage policy asks.

Endpoints:

| Path | Purpose |
| --- | --- |
| `/` | The web app |
| `/hubs/engine` | SignalR hub the web app uses |
| `/healthz` | Health check for orchestrators (the chiseled image has no shell, so probe it over HTTP) |
| `/source` | Redirects to the source code, the AGPL-3.0 network-use offer |

The image is based on `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`: no shell or package manager, running as
the non-root `app` user. Put a reverse proxy with TLS in front of it for anything beyond a LAN, and pass WebSocket
upgrades through for `/hubs/engine`. Operator sign-in for transmitting (scope F2.9) is not built yet, so the current
gateway is receive-only and should not be given a passcode.

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
