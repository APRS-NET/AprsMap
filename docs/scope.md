# Scope

The feature inventory for AprsMap, phased. "Reference topic" names the public APRSIS32 documentation topic
(aprsisce.wikidot.com) that the feature spec will cite. It is a pointer for spec writing, not a requirement to match
APRSIS32 exactly. "Needs" lists the APRS.NET gaps ([aprsnet/gaps.md](aprsnet/gaps.md)) that block the feature.

Status values: `scoped` → `spec'd` (feature doc exists in `docs/features/`) → `building` → `done`.

## Non-goals

- Windows CE / Windows Mobile, and pixel-level or wording-level UI cloning (see [clean-room.md](clean-room.md)).
- Reading APRSIS32 configuration files.
- Multi-station gateways (one station identity per gateway; see [architecture.md](architecture.md)).

## Phase 0: Foundations

| ID | Feature | Owner | Needs | Status |
| --- | --- | --- | --- | --- |
| F0.1 | Solution scaffold, CI (build, test, format), Directory.Build.props matching APRS.NET's rigor | App | — | scoped |
| F0.2 | `IAprsMapService` contracts, in-process and SignalR implementations, round-trip tests | App | — | scoped |
| F0.3 | Spikes: Mapsui in WASM, SignalR streaming in WASM, symbol atlas, tile cache ([architecture.md](architecture.md#spikes-before-phase-1-each-one-is-a-timeboxed-throwaway)) | App | — | scoped |
| F0.4 | Settings model and persistence (desktop: JSON in the user's config folder; gateway: server-side store) | App | — | scoped |

## Phase 1: Receive-only APRS-IS map (MVP, desktop + web)

| ID | Feature | Reference topic | Owner | Needs | Status |
| --- | --- | --- | --- | --- | --- |
| F1.1 | APRS-IS connection: server pick, login (receive-only without a passcode), status, auto-reconnect | Passcode | App | G03 | scoped |
| F1.2 | Automatic filter: range filter around the map viewport, updated on pan and zoom, plus tracked and buddy stations | Automatic Filters | App | G04 | scoped |
| F1.3 | Station store: latest state per station, object and item ownership, kill handling, aging and expiry, duplicate drop | No Visible Stations | App | G05, G06 | scoped |
| F1.4 | Map: OSM-style tiles, symbols with overlays, labels, selectable tile providers, transparency | Screen Elements, Transparency | App | — | scoped |
| F1.5 | Tracks / trails for moving stations, with configurable history length | — (core feature list) | App | G08 | scoped |
| F1.6 | Station info panel: last packet, path, comment, speed/course/altitude, distance and bearing from me, device | Station Info | App | G07, G08, G20 | scoped |
| F1.7 | Station list: sortable and searchable; center on, track (keep centered) | — | App | G08 | scoped |
| F1.8 | Raw packet scroller with filters, plus parse-issue display for debugging | Debug Logs | App | — | scoped |
| F1.9 | View filters: RF only, message-capable, weather, objects, by symbol category, by age | (View menu) | App | G20 | scoped |
| F1.10 | Weather station display: current readings, units preference | Weather Stations | App | — | scoped |
| F1.11 | Telemetry: latest values with PARM/UNIT/EQNS applied | Telemetry | App | G09 | scoped |
| F1.12 | Area objects and signposts drawn as shapes | — | App | — | scoped |
| F1.13 | My location: fixed position (set by map click or coordinates), used for distance and the default filter | Set Location | App | G08 | scoped |
| F1.14 | Gateway: hosts the engine, browser bundle, anonymous read-only viewing | — | App | — | scoped |

## Phase 2: Transmit via APRS-IS

| ID | Feature | Reference topic | Owner | Needs | Status |
| --- | --- | --- | --- | --- | --- |
| F2.1 | Own station: callsign, SSID guidance, symbol, comment, passcode, transmit enable and safety interlocks | SSID, Passcode, Why Won't APRSIS Transmit | App | G01, G02 | scoped |
| F2.2 | Fixed and SmartBeaconing-style position beacons, status beacons, PHG | PHG | App | G01, G11 | scoped |
| F2.3 | Predictive beaconing indicator: show where others last saw you versus where you are (our own name for the feature) | Red Dot | App | G11 | scoped |
| F2.4 | Messaging: chat per correspondent, ack/retry status, reply-ack, rejects, offline queue | APRS Messaging Explained | App | G01, G10 | scoped |
| F2.5 | Bulletins and announcements, group bulletins; ANSRVR group join and leave | ANSRVR | App | G10 | scoped |
| F2.6 | Email via the APRS email gateway, and the QRU/query service helpers | Email, QRU | App | G10 | scoped |
| F2.7 | Objects and items: create, move, re-beacon with decay, kill; multiline objects | Multiline | App | G01, G11 | scoped |
| F2.8 | Respond to directed queries (?APRSP, ?APRSS…) | — | App | G19 | scoped |
| F2.9 | Gateway auth: transmit features require a signed-in operator | — | App | — | scoped |

## Phase 3: RF, GPS, IGate, digipeater

| ID | Feature | Reference topic | Owner | Needs | Status |
| --- | --- | --- | --- | --- | --- |
| F3.1 | RF ports: KISS over serial, Bluetooth serial and TCP (Direwolf, soundmodem), several at once | Compatible Hardware/Software | App | G12, G13 | scoped |
| F3.2 | Text-mode and Kenwood built-in TNCs, open and close command scripts | Open/Close Commands | App | G14 | scoped |
| F3.3 | Per-port RF beacon paths, RF versus IS source attribution on the map, heard-direct indication | — | App | G20 | scoped |
| F3.4 | IGate: RF→IS, IS→RF for messages to local stations, objects; configurable rules | (core feature list), Receive-Only IGates Break Messaging | App | G16 | scoped |
| F3.5 | Digipeater: WIDEn-N, fill-in, own-call digipeat | Digipeating | App | G15 | scoped |
| F3.6 | GPS: NMEA from serial or TCP (gpsd), the OS location API on desktop, browser geolocation sent to the gateway | Android GPS | App | G17 | scoped |
| F3.7 | Frequency objects and radio tune format in comments | Frequencies, Radio Tune Format | App | G01 | scoped |

## Phase 4: Enhancements

| ID | Feature | Reference topic | Owner | Needs | Status |
| --- | --- | --- | --- | --- | --- |
| F4.1 | Telemetry graphs | Telemetry | App | G09 | scoped |
| F4.2 | Coverage circles from PHG and from altitude (radio horizon) | Altitude Communication Range Circles, PHG | App | G08 | scoped |
| F4.3 | NWS warning areas (shapefiles) and storm objects | Shapefiles | App | G18 | scoped |
| F4.4 | Satellite tracking and operation (ISS passes, SATSRV) | Satellite Tracking, SATSRV | App | — (third-party SGP4) | scoped |
| F4.5 | Offline tiles: preload a region from a provider that permits it, purge policy | Disable OSM Tile Purging | App | — | scoped |
| F4.6 | Reverse geocode a station's address | Lookup Address | App | — | scoped |
| F4.7 | Alerts and sounds: messages, proximity, specific callsigns; nicknames | Quick Keys | App | — | scoped |
| F4.8 | Packaging: Windows installer/MSIX, Linux AppImage/Flatpak, macOS bundle, gateway container image | — | App | — | scoped |
| F4.9 | Raw weather station input (Peet Bros, Ultimeter) for local weather beacons | Weather Stations | App | G18 | scoped |

## Order of work

1. Phase 0 completely, including spikes; their results may change architecture.md.
2. Write the Phase 1 feature specs (`docs/features/1xx-*.md`) and get them reviewed before coding.
3. In parallel, APRS.NET picks up the Phase 1 gaps (G03–G09, G20). Phase 1 needs no encoder, so it can ship before
   any transmit work lands in APRS.NET.
