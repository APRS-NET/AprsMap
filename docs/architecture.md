# Architecture

Working product name: **AprsMap** (placeholder; rename before the first release). Namespaces use `AprsMap.*`.

## Goals

- One engine, two UIs: a native desktop app (Windows, Linux, macOS) and a browser app, both calling **the same service
  interface**.
- All APRS protocol work (packets, KISS, AX.25, TNC, APRS-IS, messaging rules, digipeating, IGating) lives in
  **APRS.NET**. This repo owns the application: station state, maps, settings, UI. See [ownership](#ownership-rule).
- Modern stack: .NET 10, Avalonia 12, MVVM (CommunityToolkit.Mvvm), Mapsui 5 for the map, async streams throughout,
  `TimeProvider` for testable time.

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

Map tiles are fetched directly by each UI (desktop: disk cache; browser: HTTP cache), never proxied through the
engine.

## Solution layout

| Project | License | Target | Depends on | Holds |
| --- | --- | --- | --- | --- |
| `AprsMap.Contracts` | MIT | net10.0 | — | `IEngineService`, DTOs (`StationSnapshot`, `TrackPoint`, `ChatMessage`…), change events. Serializable with System.Text.Json source generation. No APRS.NET types leak out. |
| `AprsMap.Core` | MIT | net10.0 | Contracts, APRS.NET | The engine: connection manager, station store, tracks, message center, object manager, beacon scheduler host, settings, the in-process `IEngineService`. No UI references. |
| `AprsMap.Client.Remote` | MIT | net10.0 (browser-safe) | Contracts | `IEngineService` over SignalR. |
| `AprsMap.UI` | AGPL-3.0 | net10.0 | Contracts, Avalonia, Mapsui | Shared views, view models, map layers, symbol rendering. Depends only on `IEngineService`, never on Core. |
| `AprsMap.Desktop` | AGPL-3.0 | net10.0 | UI, Core | Desktop head; composes Core in-process. Disk tile cache, serial/GPS device pickers. |
| `AprsMap.Browser` | AGPL-3.0 | net10.0-browser | UI, Client.Remote | WebAssembly head. |
| `AprsMap.Gateway` | AGPL-3.0 | net10.0 | Core, Contracts | ASP.NET Core host: SignalR hub, auth, serves the Browser bundle, headless operation (IGate/digi without any UI attached). |
| `tests/*` | | | Core unit tests (fake `TimeProvider`, recorded packet corpora), contract round-trip tests, headless Avalonia UI tests. |

**Licensing.** The engine side (Contracts, Core, Client.Remote) is MIT, like APRS.NET, so other tools can reuse it.
The UI side is AGPL-3.0: improvements to the apps, including a hosted gateway, stay open. MIT code never references
AGPL code. AGPL projects may reference MIT ones, and the Gateway's AGPL network clause is met by a "Source" link in the
web UI.

The rule that keeps both UIs equal: **`AprsMap.UI` may only reference `AprsMap.Contracts`.** Anything a view needs is a
method or an event on `IEngineService`.

## Service interface shape (draft)

```csharp
public interface IEngineService
{
    // State: snapshot plus a change stream, so a late-joining browser can catch up.
    Task<StationSetSnapshot> GetStationsAsync(MapViewport viewport, CancellationToken ct);
    IAsyncEnumerable<StationChange> WatchStationsAsync(MapViewport viewport, CancellationToken ct);
    Task<StationDetail?> GetStationAsync(string callsign, CancellationToken ct);
    Task<IReadOnlyList<TrackPoint>> GetTrackAsync(string callsign, TimeSpan window, CancellationToken ct);
    IAsyncEnumerable<RawPacketLine> WatchRawAsync(RawFilter filter, CancellationToken ct);

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

## Gateway concerns

- **One station identity per gateway**: the engine is the station; browsers are viewers and controllers of it.
- Viewing may be anonymous on a LAN; **anything that transmits requires authentication** (the gateway ships with local
  accounts plus optional reverse-proxy auth headers). A licensed operator's callsign must not be usable by anonymous
  web visitors.
- Headless mode: the gateway runs the IGate, digipeater and beacons with no browser attached.

## Spikes before Phase 1 (each one is a timeboxed throwaway)

1. **Mapsui 5.1 + Avalonia 12 in WebAssembly**: OSM tiles, 2,000 moving symbol features, pan and zoom performance.
   Fallback: a custom Skia tile/point layer.
2. **SignalR client in Avalonia.Browser**: stream `IAsyncEnumerable` changes to the WASM app; measure throughput for a
   busy full feed.
3. **Symbol rendering**: rasterize `AprsNet.Symbols` SVGs (both styles, with overlays) into a cached Skia atlas.
4. **Tile cache**: a desktop disk cache honoring the OSM tile usage policy (User-Agent, cache headers, no bulk preload
   from openstreetmap.org; preload only from providers that permit it).
