# Deployment

## Web app: Docker first

The gateway image contains the ASP.NET Core gateway, the engine and the browser app (as static files). One container
serves everything on port 8080.

```sh
docker run -d --name aprsmap -p 8080:8080 \
  -e Engine__Callsign=N0CALL -e Engine__Filter="r/40.0/-105.0/50" \
  ghcr.io/aprs-net/aprsmap:alpha
```

Or with Compose, from a clone: `docker compose up --build`.

| Setting | Environment variable | Default |
| --- | --- | --- |
| Connect to APRS-IS | `Engine__Enabled` | `true` |
| Login callsign | `Engine__Callsign` | `N0CALL` |
| Passcode (`-1` = receive-only) | `Engine__Passcode` | `-1` |
| Server | `Engine__Server` | `rotate.aprs2.net:14580` |
| Server-side filter | `Engine__Filter` | none |
| Packets kept for new viewers | `Engine__RecentPacketCapacity` | `500` |
| OpenTelemetry export | `OTEL_EXPORTER_OTLP_ENDPOINT` (+ standard `OTEL_*`) | off |

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
`osx-arm64`. Unpack and run `aprsmap` (`aprsmap.exe` on Windows). Settings are in `appsettings.json` next to the
executable for now; a settings UI and per-user storage come with scope F0.4.

The archives are not signed yet. macOS will ask for confirmation on first launch (right-click › Open), and Windows
SmartScreen may warn. Installers, code signing and notarization are scope F4.8.
