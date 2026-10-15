# Scope

The feature inventory for AprsMap, phased. Derived from the [functional inventory](reference/functional-inventory.md) and the
[2026-10-13 observation](reference/observation-2026-10-13.md);
the UI shape is in [ux](ux/README.md). "Reference topic" names the public APRSIS32 documentation topic
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
| F0.1 | Solution scaffold, CI (build, test, format), Directory.Build.props matching APRS.NET's rigor | App | — | done |
| F0.2 | `IEngineService` contracts, in-process and SignalR implementations, round-trip tests | App | — | done |
| F0.3 | Spikes: Mapsui in WASM, SignalR streaming in WASM, symbol atlas, tile cache ([architecture.md](architecture.md#spikes-before-phase-1-each-one-is-a-timeboxed-throwaway), write-ups in [spikes](spikes/)) | App | — | done |
| F0.4 | Settings model and persistence in per-user storage ([below](#settings-storage-and-profiles)); gateway: server-side store; units chosen per quantity (altitude, distance, pressure, rain, temperature, wind) | App | — | done |
| F0.5 | First-run wizard ([spec](features/F0.5-first-run-wizard.md)): callsign → location → server → optional passcode ([ux](ux/README.md#key-flows)). Location by Maidenhead locator, street address, ZIP/postal code, GPS coordinates, or a map picker with a crosshair ([below](#settings-storage-and-profiles)) | App | G08 | spec'd |
| F0.6 | Appearance: theme (light/dark/auto), palettes (standard, high contrast, colour-vision-safe), text and icon size, map symbol size, corner style, density, reduced motion; token-based so it applies live ([ux](ux/README.md#appearance-and-accessibility)) | App | — | scoped |
| F0.7 | Rebrandable: product identity in `build/Branding.props`, `Brand` accessor, rename script ([branding.md](branding.md)) | App | — | scoped |
| F0.8 | Settings profiles: after first run, several named profiles (callsign, filters, location…); switching profiles disconnects from APRS-IS and reconnects with the new one ([below](#settings-storage-and-profiles)). Built: model, storage, switching and the status-bar switcher; still to build: adding, renaming, copying and deleting profiles in Settings | App | — | building |
| F0.9 | Ready for translation ([spec](features/F0.9-localization.md)): UI text in resource files with a live language switch, a pseudo-language and CI guards; the engine reports errors as codes, not English, so each viewer sees their own language. English only; translations follow from contributors. Built: engine errors as codes, UI strings in resources with a live switch, the pseudo-language and CI guards; still to build: the language picker in Settings (with F0.6) and docs/translating.md | App | G27 (delivered) | building |

### Settings storage and profiles

- **Per-user storage (F0.4).** Settings live in the user's own folder, never next to the executable or in a shared
  location:

  | OS | Folder |
  | --- | --- |
  | Windows | `%APPDATA%\aprsnet\<slug>` (roaming; caches such as tiles go under `%LOCALAPPDATA%`) |
  | Linux | `~/.aprsnet/<slug>` |
  | macOS | `~/Library/Application Support/aprsnet/<slug>` |

  `<slug>` is `Brand.Slug` (`aprsmap` today), so a rename moves the folder and migrates it through `PreviousSlugs`
  ([branding.md](branding.md)). The gateway keeps the same model in its server-side store. The browser app stores
  nothing itself: its settings belong to the gateway's station.
- **First-run location (F0.5).** The wizard asks for the callsign and the station's location. The location can be entered
  in any of these ways, and every one ends as latitude and longitude shown on a small map for confirmation:
  - Maidenhead locator (4–10 characters; conversion is APRS.NET gap G08);
  - street address or ZIP/postal code, looked up with a geocoding service only when the user asks. This sends the text
    to a third party, so the wizard says so, and the service's usage policy applies (for example, Nominatim's limits on
    request rate and no autocomplete);
  - GPS coordinates typed in (decimal degrees or degrees and minutes), or read from a GPS or the browser's geolocation;
  - a map picker: a modal map with a fixed crosshair in the centre that the user pans under. It reuses the F1.4 map,
    so until that lands the wizard offers the other ways only.
- **Profiles (F0.8).** The first-run answers become the first profile. After that the user can add, rename, copy and
  delete profiles; each holds its own callsign-SSID, passcode, APRS-IS server and filters, and location (for example,
  home and portable, or a club callsign). Settings that describe the person or the machine (appearance, units, tile
  cache) are shared, not per profile. Switching profiles:
  - disconnects from APRS-IS and reconnects with the new profile's login and filter, with the connection status
    showing each step;
  - never transmits anything queued under the old callsign (messages, beacons, objects) with the new one: queued items
    stay with their profile;
  - on the gateway, changes the station identity for every viewer, so it needs an authenticated operator, like
    transmitting.

## Phase 1: Receive-only APRS-IS map (MVP, desktop + web)

| ID | Feature | Reference topic | Owner | Needs | Status |
| --- | --- | --- | --- | --- | --- |
| F1.1 | APRS-IS connection: server pick, login (receive-only without a passcode), status, auto-reconnect | Passcode | App | G03 | scoped |
| F1.2 | Automatic filter: range filter around the map viewport, updated on pan and zoom, plus tracked and buddy stations | Automatic Filters | App | G04 | scoped |
| F1.3 | Station store: latest state per station, object and item ownership, kill handling, aging and expiry, duplicate drop | No Visible Stations | App | G05, G06 | scoped |
| F1.4 | Map: OSM-style tiles, symbols with overlays, labels, selectable tile providers, transparency, map dimming, symbol size, wind barbs, auto-zoom-to-fit | Screen Elements, Transparency | App | — | scoped |
| F1.5 | Tracks / trails for moving stations, with configurable history length | — (core feature list) | App | G08 | scoped |
| F1.6 | Station info panel: last packet, path, comment, speed/course/altitude, distance and bearing from me, device | Station Info | App | G07, G08, G20 | scoped |
| F1.7 | Station list: sortable and searchable; center on, track (keep centered) | — | App | G08 | scoped |
| F1.8 | Raw packet scroller with filters, plus parse-issue display for debugging | Debug Logs | App | — | scoped |
| F1.9 | View filters: RF only, message-capable, weather, objects, by symbol category, by age, by platform, by Mic-E status, by `!shriek!` and `#hashtag` tags | (View menu) | App | G20 | scoped |
| F1.10 | Weather station display: current readings, units preference | Weather Stations | App | — | scoped |
| F1.11 | Telemetry: latest values with PARM/UNIT/EQNS applied | Telemetry | App | G09 | scoped |
| F1.12 | Area objects and signposts drawn as shapes | — | App | — | scoped |
| F1.13 | My location: fixed position (set by map click or coordinates), used for distance and the default filter | Set Location | App | G08 | scoped |
| F1.14 | Gateway: hosts the engine, browser bundle, anonymous read-only viewing | — | App | — | scoped |
| F1.15 | Quick toggles: APRS-IS, internet-access master switch (gates tiles too), GPS, beaconing, sound, tile fetching | (Enables menu) | App | — | scoped |
| F1.16 | Extra map views following one station each (split view; desktop tear-off windows), auto-added to the server filter | Station Info, Automatic Filters | App | G04 | scoped |
| F1.17 | Dead-reckoned positions for moving stations, with a freshness indicator | (Screen menu) | App | G11 | scoped |
| F1.18 | Set destination: distance and bearing to a chosen point | Station Info | App | G08 | scoped |
| F1.19 | Data housekeeping: clear stations, tracks, messages, bulletins, telemetry, saved texts | (Clear menu) | App | — | scoped |
| F1.20 | Packet and station anomaly flags (duplicate positions, impossible speed, sub-second repeats, invalid) | Scroller | App | G24 | scoped |
| F1.21 | RF path visualization: per-packet digipeater paths, digipeater-to-digipeater network links, local-RF-only mode | (observed) | App | G20 | scoped |
| F1.22 | Statistics: heard platforms (device families) and symbols with counts and percentages | (observed) | App | G07 | scoped |

## Phase 2: Transmit via APRS-IS

| ID | Feature | Reference topic | Owner | Needs | Status |
| --- | --- | --- | --- | --- | --- |
| F2.1 | Own station: callsign, SSID guidance, symbol, comment, passcode, transmit enable and safety interlocks | SSID, Passcode, Why Won't APRSIS Transmit | App | G01, G02 | scoped |
| F2.2 | Fixed and SmartBeaconing-style position beacons, status beacons, PHG | PHG | App | G01, G11 | scoped |
| F2.3 | Predictive beaconing indicator: show where others last saw you versus where you are (our own name for the feature) | Red Dot | App | G11 | scoped |
| F2.4 | Messaging: chat per correspondent, ack/retry status, reply-ack, rejects, offline queue, selectable retry profiles (default, APRS-IS, none, slow HF modem), optional double-ack on RF | APRS Messaging Explained | App | G01, G10 | scoped |
| F2.5 | Bulletins and announcements, group bulletins; ANSRVR group join and leave | ANSRVR | App | G10 | scoped |
| F2.6 | Email via the APRS email gateway, and the QRU/query service helpers | Email, QRU | App | G10 | scoped |
| F2.7 | Objects and items: create, move, re-beacon with decay, kill; multiline objects | Multiline | App | G01, G11 | scoped |
| F2.8 | Respond to directed queries (?APRSP, ?APRSS…) | — | App | G19 | scoped |
| F2.9 | Gateway auth: transmit features require a signed-in operator | — | App | — | scoped |
| F2.10 | Auto-reply with delay and rate limits | Auto-Reply | App | G10 | scoped |
| F2.11 | Status reports with saved alternatives | (Configure › Status Report) | App | G01 | scoped |
| F2.12 | Mic-E status codes: choose the status to send; per-code filtering and alert styles for received ones, with emergency handling | (observed) | App | G01 | scoped |

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
| F3.8 | AGWPE over TCP as an RF port | Port Configuration | App | G22 | scoped |
| F3.9 | CWOP weather feed port (unfiltered; filtered locally) | CWOP | App | G21 | scoped |
| F3.10 | IS-Server: re-serve our stream to other apps/instances over TCP, with per-type pass-through and direction controls | IS-Server Port | App | G21 | scoped |
| F3.11 | Frequency monitor: decode frequency objects and comment frequency specs, flag malformed ones, list by frequency | (observed) | App | G25 | scoped |

## Phase 4: Enhancements

| ID | Feature | Reference topic | Owner | Needs | Status |
| --- | --- | --- | --- | --- | --- |
| F4.1 | Telemetry graphs | Telemetry | App | G09 | scoped |
| F4.2 | Coverage circles from PHG and from altitude (radio horizon) | Altitude Communication Range Circles, PHG | App | G08 | scoped |
| F4.3 | NWS warning areas (shapefiles) and storm objects | Shapefiles | App | G18 | scoped |
| F4.4 | Satellite tracking and operation (ISS passes, SATSRV) | Satellite Tracking, SATSRV | App | — (third-party SGP4) | scoped |
| F4.5 | Offline tiles: preload a region from a provider that permits it, purge policy | Disable OSM Tile Purging | App | — | scoped |
| F4.6 | Reverse geocode a station's address | Lookup Address | App | — | scoped |
| F4.7 | Alerts and sounds: messages, proximity, specific callsigns; nicknames and tactical callsigns | Quick Keys | App | — | scoped |
| F4.8 | Packaging: Windows installer/MSIX, Linux AppImage/Flatpak, macOS bundle, gateway container image | — | App | — | scoped |
| F4.9 | Raw weather station input (Peet Bros, Ultimeter) for local weather beacons | Weather Stations | App | G18 | scoped |
| F4.10 | Direction-finding circles and DF reports | (Screen menu) | App | — | scoped |
| F4.11 | DX tracking (furthest zero-hop station, alerts) and AltNet monitor/ignore | Configure DX, Configure AltNet | App | G20, G08 | scoped |
| F4.12 | Overlays: GPX and UI-View POS files, waypoint → object, drive a route to prefetch tiles | Overlays | App | — | scoped |
| F4.13 | Logging exports: GPX track auto-save, beacon-trigger CSV | (Enables menu) | App | — | scoped |
| F4.14 | Digital-mode activity objects (FT8 / JS8Call / JT65); semantics pending maintainer observation | (observed) | App | G01 | scoped |

## Order of work

1. Phase 0 completely, including spikes; their results may change architecture.md.
2. Write the Phase 1 feature specs (`docs/features/1xx-*.md`) and get them reviewed before coding.
3. In parallel, APRS.NET picks up the Phase 1 gaps (G03–G09, G20, G24). Phase 1 needs no encoder, so it can ship before
   any transmit work lands in APRS.NET.
