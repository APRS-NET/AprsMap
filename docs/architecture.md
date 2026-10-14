# Architecture

Working product name: **AprsMap** (placeholder; rename before the first release). Namespaces use `AprsMap.*`.

## Goals

- One engine, two UIs: a native desktop app (Windows, Linux, macOS) and a browser app, both calling **the same service
  interface**.
- All APRS protocol work (packets, KISS, AX.25, TNC, APRS-IS, messaging rules, digipeating, IGating) lives in
  **APRS.NET**. This repo owns the application: station state, maps, settings, UI. See [ownership](#ownership-rule).
- Modern stack: .NET 10, Avalonia 12, MVVM (CommunityToolkit.Mvvm), Mapsui 5 for the base map, async streams
  throughout, `TimeProvider` for testable time.

## The browser constraint

A browser cannot open raw TCP sockets or serial ports, so the browser app cannot talk to APRS-IS or a TNC itself. The
web UI is therefore the **same Avalonia UI compiled to WebAssembly**, talking to an **ASP.NET Core gateway** that hosts
the engine:

```
 Desktop process                         Gateway process (server / Pi / home PC)
┌──────────────────────────┐            ┌──────────────────────────────────────┐
│ AprsMap.Desktop (head)   │            │ AprsMap.Gateway (ASP.NET Core)       │
│   AprsMap.UI (views/VMs) │            │   SignalR hub ── IEngineService     │
│        │                 │            │   static files: AprsMap.Browser      │
│  IEngineService         │            │        │                             │
│  (in-process impl)       │            │  AprsMap.Core engine                 │
│        │                 │            │        │                             │
│  AprsMap.Core engine     │            │  APRS.NET (IS client, KISS, AX.25…)  │
│        │                 │            └────────┼─────────────────────────────┘
│  APRS.NET                │                     │ TCP / serial
└────────┼─────────────────┘                APRS-IS, TNCs, GPS
         │ TCP / serial                            ▲
    APRS-IS, TNCs, GPS                             │ WebSocket (SignalR)
                                        ┌──────────┴───────────────┐
                                        │ Browser: AprsMap.Browser │
                                        │   AprsMap.UI (same VMs)  │
                                        │   IEngineService        │
                                        │   (SignalR client impl)  │
                                        └──────────────────────────┘
```

Map tiles never go through `IEngineService`. Both heads use one tile cache (`AprsMap.Tiles`): the desktop in-process,
and the browser through the gateway's same-origin `/tiles/{provider}/{z}/{x}/{y}.png` endpoint, which serves the same
cache over plain HTTP. See [Map tiles](#map-tiles).

## Solution layout

| Project | License | Target | Depends on | Holds |
| --- | --- | --- | --- | --- |
| `AprsMap.Contracts` | MIT | net10.0 | — | `IEngineService`, DTOs (`StationSnapshot`, `TrackPoint`, `ChatMessage`…), change events. Every type that crosses the hub is listed in `EngineJsonContext` (System.Text.Json source generation), because browser builds have reflection-based JSON turned off. No APRS.NET types leak out. |
| `AprsMap.Core` | MIT | net10.0 | Contracts, APRS.NET | The engine: connection manager, station store, tracks, message center, object manager, beacon scheduler host, settings, the in-process `IEngineService`. No UI references. |
| `AprsMap.Client.Remote` | MIT | net10.0 (browser-safe) | Contracts | `IEngineService` over SignalR. |
| `AprsMap.Tiles` | MIT | net10.0 | — | Map tile cache: disk store, HTTP fetch that follows each provider's usage policy (User-Agent from branding, cache headers, conditional revalidation, merged duplicate downloads, capped concurrency). No UI, no Mapsui or BruTile. |
| `AprsMap.UI` | AGPL-3.0 | net10.0 | Contracts, Avalonia, Mapsui (core) | Shared views, view models, the `MapView` control, the station layer and symbol atlas, and the tile source adapter (fed by the head). Depends only on `IEngineService`, never on Core. |
| `AprsMap.Desktop` | AGPL-3.0 | net10.0 | UI, Hosting, Tiles | Desktop head; composes Core in-process and gives the map `AprsMap.Tiles` directly. Serial/GPS device pickers. |
| `AprsMap.Browser` | AGPL-3.0 | net10.0-browser | UI, Client.Remote | WebAssembly head; fetches tiles from the gateway's `/tiles` endpoint. AOT in Release builds. |
| `AprsMap.Gateway` | AGPL-3.0 | net10.0 | Hosting, Tiles | ASP.NET Core host: SignalR hub, `/tiles` endpoint, auth, serves the Browser bundle, headless operation (IGate/digi without any UI attached). |
| `AprsMap.Hosting` | AGPL-3.0 | net10.0 | Core | Host wiring shared by Desktop and Gateway: engine configuration binding and OpenTelemetry (OTLP export only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set). |
| `tests/*` | | | Core unit tests (fake `TimeProvider`, recorded packet corpora), contract round-trip tests, headless Avalonia UI tests. |

**Licensing.** The engine side (Contracts, Core, Client.Remote) is MIT, like APRS.NET, so other tools can reuse it.
The UI side is AGPL-3.0: improvements to the apps, including a hosted gateway, stay open. MIT code never references
AGPL code. AGPL projects may reference MIT ones, and the Gateway's AGPL network clause is met by a "Source" link in the
web UI.

The rule that keeps both UIs equal: **`AprsMap.UI` may only reference `AprsMap.Contracts`.** Anything a view needs is a
method or an event on `IEngineService`.

## Service interface shape (draft)

Built so far: `GetStatusAsync`, `WatchPacketsAsync` and `WatchStationsAsync` (F0.2), and `GetSettingsAsync` /
`SaveSettingsAsync` (F0.4). The rest arrives with the features that need it.

```csharp
public interface IEngineService
{
    // State: the station stream starts with a snapshot, so a late-joining browser catches up without a race.
    IAsyncEnumerable<StationBatch> WatchStationsAsync(CancellationToken ct);
    Task<StationDetail?> GetStationAsync(string callsign, CancellationToken ct);
    Task<IReadOnlyList<TrackPoint>> GetTrackAsync(string callsign, TimeSpan window, CancellationToken ct);
    IAsyncEnumerable<PacketView> WatchPacketsAsync(CancellationToken ct);   // the raw packet view; filters later
    Task<EngineStatus> GetStatusAsync(CancellationToken ct);

    // Connections
    IAsyncEnumerable<PortStatus> WatchPortsAsync(CancellationToken ct);
    Task SetViewportAsync(MapViewport viewport, CancellationToken ct);   // drives the automatic APRS-IS filter

    // Phase 2+
    Task<SendResult> SendMessageAsync(OutgoingMessage message, CancellationToken ct);
    IAsyncEnumerable<ChatEvent> WatchChatAsync(CancellationToken ct);
    Task BeaconNowAsync(CancellationToken ct);
    Task<ObjectResult> UpsertObjectAsync(ObjectDefinition obj, CancellationToken ct);

    // Settings
    Task<AppSettings> GetSettingsAsync(CancellationToken ct);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct);
}
```

SignalR supports `IAsyncEnumerable<T>` server-to-client streaming, so the remote implementation maps one to one.

Stream shapes (from [spike 2](spikes/signalr-stream.md)):

- **The map gets station changes, not packets.** `WatchStationsAsync` yields `StationBatch`es: compact states (name,
  position, symbol, last heard) that changed, plus names that left the map (killed objects and items), about every
  250 ms with only the latest state per station. A slow viewer costs at most one copy of the map, never a backlog. At
  the real full feed that is about 9 KB/s of JSON against 28–36 KB/s for per-packet messages, and the latency it adds
  is invisible on a map.
- **Raw packets are opt-in.** `WatchPacketsAsync` (raw text plus summary) is only for the raw packet view.
- **A late joiner gets a snapshot first**, as the stream's first batches (1,000 stations each). Even 40,000 stations
  arrive in well under a second.
- **Wire format: JSON with source-generated metadata** (`EngineJsonContext`). The spike measured MessagePack 20–40 %
  smaller, but SignalR's MessagePack protocol doesn't support trimming or native AOT (MessagePack-CSharp 2.x), so the
  trimmed browser build refuses it, and the AOT Release build couldn't use it. Batching stations was the real saving;
  revisit MessagePack if SignalR moves to a trim-safe version.
- **Clients outlive the gateway.** The browser reconnects forever (quickly at first, then every 30 s), and the UI
  resubscribes to each stream after a failure, showing the error meanwhile.

Throughput is not a constraint: the browser kept up with 20× the full worldwide feed, and one gateway served 100
viewers on a third of one core.

## Map rendering

From [spike 1](spikes/map-wasm.md) and [spike 3](spikes/symbols.md):

- **Mapsui for the base map only**: tiles, projection, navigation and tile fetching. Mapsui.Avalonia 5.1 doesn't run
  on Avalonia 12, so `AprsMap.UI` owns a small `MapView` control that hosts Mapsui's core and draws through Skia.
- **Stations, tracks and labels are our own Skia layer**, not Mapsui features. Mapsui's per-feature pipeline cost 3–5×
  more than drawing directly.
- **Symbols come from one sprite sheet (atlas) drawn with a single `SKCanvas.DrawAtlas` call per frame**: under 1 ms
  for 2,000 symbols in the browser. Never draw the SVGs per frame (16 fps).
  - The atlas is built at runtime from `AprsNet.Symbols` (Svg.Skia, with a cached typeface provider; without it each
    symbol takes 47 ms in font lookups).
  - The 188 base symbols are built up front (40 ms desktop, about 0.3 s browser); overlay symbols are added the first
    time a packet uses one.
  - The atlas is rebuilt when symbol size, screen scale or style changes.
- **Labels are the expensive part** (about 10 ms per 2,000 per frame). Declutter them, cache rendered labels, and
  treat label density as a performance setting.
- **The GPU resource budget must fit the tiles on screen.** Avalonia's default of 28 MB makes Skia re-upload every
  tile each frame on large canvases. Set `SkiaOptions.MaxGpuResourceSizeBytes` from the screen area (96 MB covers
  2256 × 1352).
- **Browser Release builds use AOT**, which roughly doubles managed drawing speed. The cost is a download of about
  73 MB against 35 MB uncompressed, and a slower publish.

## Map tiles

From [spike 4](spikes/tile-cache.md). `AprsMap.Tiles` follows the
[OSM tile usage policy](https://operations.osmfoundation.org/policies/tiles/) and the equivalent rules of any other
provider:

- **Identity:** a User-Agent built from branding (`Brand.DisplayName`, version, `Brand.SourceUrl`); the browser's
  Referer is passed through unchanged.
- **Freshness:**
  - honour `Cache-Control`, `Age`, `Expires` and the ETag, with a 7-day default;
  - revalidate expired tiles with conditional requests;
  - serve stale tiles only within the `stale-while-revalidate` and `stale-if-error` windows the server grants.
- **Load:** at most 4 upstream requests in flight, and concurrent requests for one tile share one download.
- **No bulk downloads:** only tiles a user is viewing. Region preloading (F4.5) uses only providers that allow it.
- **Size cap with least-recently-used purging.**

The gateway's `/tiles` endpoint is same-origin, so the browser needs no CORS or User-Agent of its own (BruTile's HTTP
source refuses requests without one, so the browser uses a small `HttpClient` tile source instead). It bounds
coordinates and providers so it isn't an open proxy. All viewers share the gateway's cache: 10 viewers asking for the
same 25 new tiles cause 25 upstream requests.

## Ownership rule

Ask: *would another APRS application (a digipeater, a tracker, a logger) need this exactly as written?*

| Goes to **APRS.NET** (scoped in [aprsnet/gaps.md](aprsnet/gaps.md)) | Stays in **this repo** |
| --- | --- |
| Packet decode and **encode**, AX.25 frames, KISS framing, TNC transports (serial, TCP, text mode) | Which ports are configured, the connection manager UI and status |
| APRS-IS login, send, reconnect, passcode, typed server filters | *Automatic* filter policy (filter follows the map viewport) |
| Message ack/retry/reply-ack state machine, duplicate suppression | Chat windows, unread counts, notification sounds |
| Beacon *algorithms* (fixed, SmartBeaconing-style, decay) | Which beacons exist, the "predictive beacon" UI indicator |
| Digipeater and IGate engines (path rules, q-constructs, gating rules) | Enabling and configuring them |
| Telemetry EQNS/BITS application, geo maths, Maidenhead, device ID lookup | Graphs, range circles, labels |
| NMEA sentence parsing (also needed for `$` packets) | GPS device discovery and selection |

If an app feature needs a missing protocol capability, the app spec links the gap section, and the app code waits or
uses a clearly marked temporary internal shim (`// SHIM: replace with APRS.NET <gap id>`), which is deleted when the
package ships.

## Settings

The engine owns settings (`AppSettings`: station profiles, the active one, and shared units), so the desktop and
browser UIs read and change them through `IEngineService` like everything else.

- **Storage:** one `settings.json` per user ([scope](scope.md#settings-storage-and-profiles)), written atomically. A
  file that can't be read is moved aside to `settings.json.invalid`, and the app starts as on a first run.
- **First run:** with no stored settings, the engine creates the first profile from the configured defaults
  (`Engine:Callsign` and friends) if they name a usable callsign. Otherwise it stays `NotConfigured` and doesn't
  connect until a profile exists.
- **Validation:** the engine checks every save. Login fields go through APRS.NET's own `AprsIsClientOptions.Validate`,
  so the login rules live in one place.
- **Switching:** saving a different active profile, or a change to its callsign, passcode, server or filter, ends the
  APRS-IS session and reconnects at once with the new values. Other changes (names, location, units) don't reconnect.
- **Gateway:** browsers get the settings read-only (`AppSettings.ReadOnly`, passcodes hidden) and can't save, because a
  profile switch changes the station for every viewer. `Gateway:AllowSettingsChanges` lifts that on a trusted
  network until operator sign-in (F2.9) exists.

## Gateway concerns

- **One station identity per gateway**: the engine is the station; browsers are viewers and controllers of it.
- Viewing may be anonymous on a LAN; **anything that transmits requires authentication** (the gateway ships with local
  accounts plus optional reverse-proxy auth headers). A licensed operator's callsign must not be usable by anonymous
  web visitors.
- Headless mode: the gateway runs the IGate, digipeater and beacons with no browser attached.

## Spikes before Phase 1 (each one is a timeboxed throwaway)

All four are done (2026-10-13/14). Their conclusions are folded into the sections above. Each write-up is in
`docs/spikes/`, and the throwaway code is on its `spike/*` branch, never merged.

1. **Mapsui 5.1 + Avalonia 12 in WebAssembly**: OSM tiles, 2,000 moving symbol features, pan and zoom performance.
   Fallback: a custom Skia tile/point layer. **Done:** [map-wasm.md](spikes/map-wasm.md).
2. **SignalR client in Avalonia.Browser**: stream `IAsyncEnumerable` changes to the WASM app; measure throughput for a
   busy full feed. **Done:** [signalr-stream.md](spikes/signalr-stream.md).
3. **Symbol rendering**: rasterize `AprsNet.Symbols` SVGs (both styles, with overlays) into a cached Skia atlas. **Done:**
   [symbols.md](spikes/symbols.md).
4. **Tile cache**: a desktop disk cache honoring the OSM tile usage policy (User-Agent, cache headers, no bulk preload
   from openstreetmap.org; preload only from providers that permit it). **Done:** [tile-cache.md](spikes/tile-cache.md).

Smaller fixes the spikes turned up, for the Phase 0 and Phase 1 work that touches them:

- ~~`Engine.Accept` lets a parse exception escape~~: it now drops and counts the line (F0.2).
- ~~`MainViewModel` swallows hub errors~~: they now show in the status bar and are retried (F0.2).
- The default `N0CALL` login is rejected by APRS-IS servers; the first-run wizard (F0.5) must collect a callsign.
