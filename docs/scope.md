# Scope

The feature inventory for AprsMap, phased. Derived from the [functional inventory](reference/functional-inventory.md) and the
[2026-10-13 observation](reference/observation-2026-10-13.md);
the UI shape is in [ux](ux/README.md). "Reference topic" names the public APRSIS32 documentation topic
(aprsisce.wikidot.com) that the feature spec will cite. It is a pointer for spec writing, not a requirement to match
APRSIS32 exactly. "Needs" lists the APRS.NET gaps ([aprsnet/gaps.md](aprsnet/gaps.md)) that block the feature.

Status values: `scoped` → `spec'd` (feature doc exists in `docs/features/`) → `building` → `done`.

Work is tracked on GitHub: each feature planned for a release is an issue labelled `feature` in that release's
milestone ([0.3.0-alpha.1](https://github.com/APRS-NET/AprsMap/milestone/1), done; now
[0.4.0-alpha.1](https://github.com/APRS-NET/AprsMap/milestone/2)), and PRs close it (`Closes #N`) or reference it
(`Part of #N`). This table stays the full inventory; the milestone shows what's in flight, and a status of the form
"spec'd (0.4.0)" names the milestone a feature is planned for.

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
| F0.5 | First-run wizard ([spec](features/F0.5-first-run-wizard.md)): callsign → location → server → optional passcode ([ux](ux/README.md#key-flows)). Location by Maidenhead locator, street address, ZIP/postal code, GPS coordinates, or a map picker with a crosshair ([below](#settings-storage-and-profiles)). Built: all but the map picker, which is [#130](https://github.com/APRS-NET/AprsMap/issues/130) | App | G08 | done (map picker in 0.4.0) |
| F0.6 | Settings screen and appearance ([spec](features/F0.6-appearance.md)): theme (light/dark/auto), palettes (standard, high contrast, colour-vision-safe), text and icon size, map symbol size, corner style, density, reduced motion; token-based so it applies live ([ux](ux/README.md#appearance-and-accessibility)) | App | — | done |
| F0.7 | Rebrandable ([spec](features/F0.7-rebranding.md)): product identity in `build/Branding.props`, `Brand` accessor, rename script ([branding.md](branding.md)) | App | — | done |
| F0.8 | Settings profiles ([spec](features/F0.8-profiles.md)): after first run, several named profiles (callsign, filters, location…); switching profiles disconnects from APRS-IS and reconnects with the new one ([below](#settings-storage-and-profiles)). Built: model, storage, switching from the status bar and from Settings, and adding, editing, copying and deleting profiles in Settings | App | — | done |
| F0.9 | Ready for translation ([spec](features/F0.9-localization.md)): UI text in resource files with a live language switch, a pseudo-language and CI guards; the engine reports errors as codes, not English, so each viewer sees their own language. English only; translations follow from contributors. Built: engine errors as codes, UI strings in resources with a live switch, the pseudo-language and CI guards; the language picker in Settings (built with F0.6, #41) and [translating.md](translating.md); the per-language completeness report (behavior 20) comes with the first translation | App | G27 (delivered) | done |
| F0.10 | Start over ([spec](features/F0.10-start-over.md)): Tools › Start over… deletes profiles, settings, appearance and layout (desktop), or this browser's data and optionally the gateway's profiles (web), then first-run setup; `--reset-all` | App | — | done |

### Settings storage and profiles

- **Per-user storage (F0.4).** Settings live in the user's own folders, never next to the executable or in a shared
  location. Each kind of file goes where its system expects it ([F4.15 behavior 13](features/F4.15-updates.md)):

  | | Windows | Linux (XDG) | macOS |
  | --- | --- | --- | --- |
  | Settings, profiles, preferences | `%APPDATA%\aprsnet\<slug>` | `~/.config/aprsnet/<slug>` | `~/Library/Application Support/aprsnet/<slug>` |
  | Message history, captures | `%LOCALAPPDATA%\aprsnet\<slug>` | `~/.local/share/aprsnet/<slug>` | as settings |
  | Tile cache | `%LOCALAPPDATA%\aprsnet\<slug>\tiles` | `~/.cache/aprsnet/<slug>/tiles` | `~/Library/Caches/aprsnet/<slug>/tiles` |
  | Machine-wide defaults | `%ProgramData%\aprsnet\<slug>` | `/etc/xdg/aprsnet/<slug>` | `/Library/Application Support/aprsnet/<slug>` |

  `<slug>` is `Brand.Slug` (`aprsmap` today), so a rename moves the folder and migrates it through `PreviousSlugs`
  ([branding.md](branding.md)). Linux's folders were `~/.aprsnet/<slug>` before 0.5 and move on the first start. The
  gateway keeps the same model in its server-side store (`/data`). The browser app stores nothing itself: its settings
  belong to the gateway's station.
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
  delete profiles; each holds its own callsign-SSID, passcode, location and the sources it uses (its APRS-IS server
  and filter text, and later TNCs, from the master list in [F1.25](features/F1.25-sources.md)) (for example,
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
| F1.0 | Shell and docking ([spec](features/F1.0-shell-and-docking.md)): map in the centre with dockable, tabbed, floatable panels; layout saved per viewer; floating windows kept on a screen that exists | — (maintainer request) | App | — | done |
| F1.1 | APRS-IS connection ([spec](features/F1.1-aprs-is-connection.md)): server pick, login (receive-only without a passcode), status, auto-reconnect | Passcode | App | G03 | done |
| F1.2 | Automatic filter ([spec](features/F1.2-automatic-filter.md)): range filter around my location and the map viewport, updated on pan and zoom, plus followed stations and the source's own filter text; optional filter circles on the map | Automatic Filters | App | G04 | done |
| F1.3 | Station store ([spec](features/F1.3-station-store.md)): latest state per station, object and item ownership, kill handling, aging and expiry, duplicate drop | No Visible Stations | App | G05, G06 | done |
| F1.4 | Map ([spec](features/F1.4-map.md)): OSM-style tiles, symbols with overlays, labels, selectable tile providers, transparency, map dimming, symbol size, wind barbs, auto-zoom-to-fit | Screen Elements, Transparency | App | — | done |
| F1.5 | Tracks / trails ([spec](features/F1.5-tracks.md)) for moving stations, with configurable history length | — (core feature list) | App | G08, G24 | spec'd (0.4.0) |
| F1.6 | Station info panel ([spec](features/F1.6-station-details.md)): last packet, path, comment, speed/course/altitude, distance and bearing from me, device | Station Info | App | G07, G08, G20 | done |
| F1.7 | Station list ([spec](features/F1.7-station-list.md)): sortable and searchable; center on, track (keep centered) | — | App | G08 | done |
| F1.8 | Raw packet scroller ([spec](features/F1.8-packet-list.md)) with filters, plus parse-issue display for debugging | Debug Logs | App | G05, G06, G20 | done |
| F1.9 | View filters ([spec](features/F1.9-view-filters.md)): RF only, message-capable, weather, objects, by symbol category, by age, by platform, by Mic-E status, by `!shriek!` and `#hashtag` tags | (View menu) | App | G07, G20 | spec'd (0.4.0) |
| F1.10 | Weather station display ([spec](features/F1.10-weather.md)): current readings, units preference | Weather Stations | App | G08 | spec'd (0.4.0) |
| F1.11 | Telemetry ([spec](features/F1.11-telemetry.md)): latest values with PARM/UNIT/EQNS applied | Telemetry | App | G09 | spec'd (0.4.0) |
| F1.12 | Area objects and signposts ([spec](features/F1.12-area-objects.md)) drawn as shapes | — | App | — | spec'd (0.4.0) |
| F1.13 | My location ([spec](features/F1.13-my-location.md)): fixed position (set by map click or coordinates), used for distance and the default filter | Set Location | App | G08 | done |
| F1.14 | Gateway ([spec](features/F1.14-gateway.md)): hosts the engine, browser bundle, anonymous read-only viewing | — | App | — | done |
| F1.15 | Quick toggles ([spec](features/F1.15-quick-toggles.md)): APRS-IS, internet-access master switch (gates tiles too), GPS, beaconing, sound, tile fetching | (Enables menu) | App | — | spec'd (0.4.0) |
| F1.16 | Extra map views following one station each (split view; desktop tear-off windows), auto-added to the server filter ([spec](features/F1.16-extra-map-views.md)) | Station Info, Automatic Filters | App | G04 | built (0.6.0, [#207](https://github.com/APRS-NET/AprsMap/issues/207)); decisions proposed |
| F1.17 | Dead-reckoned positions for moving stations, with a freshness indicator ([spec](features/F1.17-dead-reckoning.md)) | (Screen menu) | App | G08, G11 | built (0.6.0, [#198](https://github.com/APRS-NET/AprsMap/issues/198)); decisions proposed |
| F1.18 | Set destination: distance and bearing to a chosen point ([spec](features/F1.18-destination.md)) | Station Info | App | G08 | built (0.5.0, [#194](https://github.com/APRS-NET/AprsMap/issues/194)) |
| F1.19 | Data housekeeping: clear stations, tracks, messages, bulletins, telemetry, saved texts ([spec](features/F1.19-housekeeping.md)) | (Clear menu) | App | — | spec'd (0.4.0) |
| F1.20 | Packet and station anomaly flags (duplicate positions, impossible speed, sub-second repeats, invalid) ([spec](features/F1.20-anomalies.md)) | Scroller | App | G24 | spec'd (0.4.0) |
| F1.21 | RF path visualization: per-packet digipeater paths, digipeater-to-digipeater network links, local-RF-only mode ([spec](features/F1.21-rf-paths.md)) | (observed) | App | G20 | spec'd (0.4.0) |
| F1.22 | Statistics and dashboard ([spec](features/F1.22-statistics.md)): a Dashboard tab beside the map with live tiles and sparklines (stations heard, new stations, packets received, arrival, types, problems, connection, busiest stations and digipeaters), and tables of platforms and symbols | (observed) | App | G07, G20 | done (0.4.0; extras and My view 0.5.0, [#176](https://github.com/APRS-NET/AprsMap/issues/176)) |
| F1.23 | Saved layouts ([spec](features/F1.23-saved-layouts.md)): named layouts, and the layout last used with a monitor setup offered when monitors change | — (maintainer request) | App | — | built (0.5.0, [#195](https://github.com/APRS-NET/AprsMap/issues/195)) |
| F1.24 | Web pop-out panels: a panel in its own browser tab or window (a second app instance on the same gateway), with selection kept in sync; after F1.14's viewer limits account for it | — (maintainer request) | App | — | scoped (0.7.0, [#212](https://github.com/APRS-NET/AprsMap/issues/212)) |
| F1.25 | Sources ([spec](features/F1.25-sources.md)): a master list of sources (APRS-IS now; KISS over TCP and serial, and GPS, later) that profiles pick from, at most one APRS-IS; each packet records its source, with Seen on for duplicates | — (maintainer request) | App | G05, G29 | done for APRS-IS; KISS with F3.1 (0.4.0) |
| F1.26 | Public receive-only console ([spec](features/F1.26-public-console.md)): a viewer port and a private operator port on the gateway, a receive-only switch that holds after Phase 2, location privacy for viewers, a page title, a first-run KISS source from the environment, and the Unraid + Raspberry Pi deployment | — (maintainer request) | App | — | built (0.5.0, [#174](https://github.com/APRS-NET/AprsMap/issues/174)) |
| F1.27 | Packet capture for analysis ([spec](features/F1.27-packet-capture.md)): record the packets APRS.NET couldn't parse or decode, byte for byte in the corpus line format, look through them, and export them anonymized for APRS.NET's test corpus with a note for an issue | — (maintainer request) | App | G31 | built (0.5.0, [#233](https://github.com/APRS-NET/AprsMap/issues/233)) |
| F1.28 | Source console ([spec](features/F1.28-source-console.md)): a source's live traffic for troubleshooting (frames, unframed text from a TNC not in KISS mode, non-APRS and dropped frames with why, the enter-KISS steps, what was sent; APRS-IS lines with the passcode masked), operator only on a gateway | — (maintainer request) | App | G33 | built (0.5.0, [#246](https://github.com/APRS-NET/AprsMap/issues/246)) |
| F1.29 | Menus and commands ([spec](features/F1.29-menus.md)): File, View, Station, Tools and Help menus with every command and its shortcut; hidden until Alt on Windows and Linux, the system menu bar on macOS, under ☰ in the web app; panels shown and hidden from View › Panels; actions moved out of Settings | — (maintainer request) | App | — | built (0.5.0, [#278](https://github.com/APRS-NET/AprsMap/issues/278)) |
| F1.31 | Where files are kept ([spec](features/F1.31-files.md)): Help › About lists the settings, data, cache and machine-defaults folders and a new rolling log file, each with Copy; on a gateway only for a signed-in operator, viewers told their layout is kept in this browser | — (maintainer request) | App | — | built (0.6.0, [#306](https://github.com/APRS-NET/AprsMap/issues/306)) |
| F1.32 | Logging and telemetry settings ([spec](features/F1.32-logging.md)): Settings › Logging with an overall level, a level per area and privacy switches; a daily log file with a maximum size, days kept and a total cap (on on the desktop, off on a gateway); a list of OTLP targets with masked headers | — (maintainer request) | App | — | spec'd (0.6.0, [#310](https://github.com/APRS-NET/AprsMap/issues/310)) |
| F1.33 | Station lists ([spec](features/F1.33-station-lists.md)): named lists of callsigns and objects per profile, in and out as plain text; the View filter shows only a list, and a list in use joins the APRS-IS filter (`b/`, `o/`); for events and EOC spotters | — (maintainer request) | App | G04 | spec'd (0.7.0, [#328](https://github.com/APRS-NET/AprsMap/issues/328)) |
| F1.34 | Components and versions in Help › About ([spec](features/F1.34-about-components.md)): every APRS.NET package the app uses (each `AprsNet.*` with its version), the main third-party libraries (.NET runtime, Avalonia, Dock, Mapsui, SkiaSharp, SignalR, Velopack…) with version and licence, and the OS and runtime it runs on; on a gateway both sides, the browser app's and the gateway's engine (they can differ during an update); taken from the build (the resolved package graph embedded at build time), not typed by hand; **Copy** puts the whole list on the clipboard for a bug report; a link to the full third-party licence notices, including the aprs-deviceid list's CC BY-SA credit (F4.16) | — (maintainer request) | App | — | built for 0.6.0 ([#351](https://github.com/APRS-NET/AprsMap/issues/351)) |
| F1.35 | Desktop web viewer ([spec](features/F1.35-desktop-web-viewer.md)): the desktop app (AppImage, Windows, macOS) can serve its own station to browsers on this computer or the local network, view only, from the browser app bundled with it: Settings › Web viewer turns it on (off by default), with the port and who may connect, and shows the addresses to open; nothing in the browser changes settings or transmits | — (maintainer request, 2026-10-24) | App | — | building (0.6.0, [#364](https://github.com/APRS-NET/AprsMap/issues/364)) |
| F1.36 | Operator sign-in for the desktop web viewer: a browser (a phone, say) signs in to the desktop's viewer to change settings and transmit, as F2.9 on a gateway | — (maintainer request, 2026-10-24) | App | — | scoped (later) |

## Phase 2: Transmit via APRS-IS

| ID | Feature | Reference topic | Owner | Needs | Status |
| --- | --- | --- | --- | --- | --- |
| F2.1 | Own station ([spec](features/F2.1-own-station.md)): callsign, SSID guidance, symbol, comment, passcode, transmit enable and safety interlocks, Send position now; one Transmit switch in the status bar, RX only and Test send per source, RF through KISS TNCs | SSID, Passcode, Why Won't APRSIS Transmit | App | G01, G02, G13 | built (0.5.0, [#190](https://github.com/APRS-NET/AprsMap/issues/190)) |
| F2.2 | Beacons ([spec](features/F2.2-beacons.md)): fixed and SmartBeaconing-style position beacons, status beacons, PHG; **Send via** checkmarks per beacon over the sources that may transmit (F2.1), all ticked by default | PHG | App | G01, G11 | built (0.5.0, [#196](https://github.com/APRS-NET/AprsMap/issues/196)); the moving schedule (SmartBeaconing from the GPS) built for 0.6.0 ([#335](https://github.com/APRS-NET/AprsMap/issues/335)), decisions proposed |
| F2.3 | Where others see you ([spec](features/F2.3-seen-as.md)): the last position sent, moved on along its course and speed as others' maps would, against where the GPS says the station is: a ring and line on the map, and the distance in the Transmit tooltip (our own name for the feature) | Red Dot | App | G11 | built (0.6.0, [#197](https://github.com/APRS-NET/AprsMap/issues/197)); decisions proposed |
| F2.4 | Messaging ([spec](features/F2.4-messaging.md)): chat per correspondent, ack/retry status, reply-ack, rejects, offline queue, selectable retry profiles (default, APRS-IS, none, slow HF modem), optional double-ack on RF; **Send via** per thread: Automatic (RF to a correspondent heard on a TNC recently, APRS-IS otherwise, falling back to APRS-IS when RF retries go unacked), or chosen sources; acks go back on the source the message came in on | APRS Messaging Explained | App | G01, G10 | built (0.5.0, [#192](https://github.com/APRS-NET/AprsMap/issues/192)) |
| F2.5 | Bulletins and announcements, group bulletins; ANSRVR group join and leave; Send via checkmarks as for beacons ([spec](features/F2.5-bulletins.md)) | ANSRVR | App | G10, G34 | built; a conversation per group deferred ([#285](https://github.com/APRS-NET/AprsMap/issues/285), waits on APRS.NET#281, paused) (0.5.0, [#193](https://github.com/APRS-NET/AprsMap/issues/193)) |
| F2.6 | Email and QRU ([spec](features/F2.6-email-and-qru.md)): an email as one message to the EMAIL-2 gateway, and asking QRU servers in range for a group; being a QRU server later | Email, QRU | App | G10 | built except the QRU server (0.6.0, [#201](https://github.com/APRS-NET/AprsMap/issues/201)); decisions proposed |
| F2.7 | Objects and items ([spec](features/F2.7-objects-and-items.md)): placed from the map or Settings › Objects, moved, re-sent on a decaying schedule, killed (sent killed three times) or forgotten, kept per profile; multiline objects wait for G36 | Multiline | App | G01, G11, G36 | built except multiline (0.6.0, [#199](https://github.com/APRS-NET/AprsMap/issues/199)); decisions proposed |
| F2.8 | Answering queries ([spec](features/F2.8-queries.md)): ?APRSP, ?APRSS, ?APRSO, ?APRSD, ?APRST/?PING? and the general ?APRS? with APRS.NET's QueryResponder, back the way they came, on by default under every transmit rule; ?APRSH, ?APRSM, ?IGATE? and ?WX? later | — | App | G19 | built (0.6.0, [#200](https://github.com/APRS-NET/AprsMap/issues/200)); decisions proposed |
| F2.9 | Gateway operator sign-in ([spec](features/F2.9-operator-sign-in.md)): operators with hashed passwords set up on the host, a session cookie, guessing limited and logged; transmitting, settings and TNC commands need a signed-in operator | — | App | — | built (0.5.0, [#191](https://github.com/APRS-NET/AprsMap/issues/191)) |
| F2.10 | Auto-reply ([spec](features/F2.10-auto-reply.md)): an away message, off by default, sent once per correspondent per hour after a 30 s wait (cancelled by a reply by hand), only when nothing was sent by hand for 15 minutes, never to pings, auto-replies or services, the way the message came | Auto-Reply | App | G10 | built (0.6.0, [#202](https://github.com/APRS-NET/AprsMap/issues/202)); decisions proposed |
| F2.11 | Status reports ([spec](features/F2.11-status-reports.md)): up to 20 saved texts per profile to choose from, the grid square or the UTC time added if chosen, and Send status now | (Configure › Status Report) | App | G01 | built (0.6.0, [#203](https://github.com/APRS-NET/AprsMap/issues/203)); decisions proposed |
| F2.12 | Mic-E status ([spec](features/F2.12-mic-e-status.md)): Mic-E as a third position format with a status to send (never Emergency, by decision); received statuses filtered by F1.9, alert styles with F4.7 | (observed) | App | G01 | built (0.6.0, [#204](https://github.com/APRS-NET/AprsMap/issues/204)); decisions proposed |
| F2.13 | Ping ([spec](features/F2.13-ping.md)): a remote connectivity check; a message starting with the ping phrase ("AprsMap ping" by default, from the brand) is answered with the pong phrase and the UTC time, the way it came; off by default, who may ping and how often (never under 10 s) set by the operator | — (maintainer request) | App | G10 | built (0.6.0, [#307](https://github.com/APRS-NET/AprsMap/issues/307)) |

## Phase 3: RF, GPS, IGate, digipeater

| ID | Feature | Reference topic | Owner | Needs | Status |
| --- | --- | --- | --- | --- | --- |
| F3.1 | RF ports: KISS over serial, Bluetooth serial and TCP (Direwolf, soundmodem), several at once, as [F1.25](features/F1.25-sources.md) sources. Receive only in 0.4.0 ([#120](https://github.com/APRS-NET/AprsMap/issues/120)); the test bed is Direwolf on Windows, MFJ TNC-X, later NinoTNC | Compatible Hardware/Software | App | G12, G13 | TCP done (0.4.0), transmitting through it with F2.1 (0.5.0); serial built (0.5.0, [#177](https://github.com/APRS-NET/AprsMap/issues/177)), to be tested on a TH-D72A's built-in TNC |
| F3.2 | Radios' built-in TNCs ([spec](features/F3.2-built-in-tncs.md), TH-D72A first): switched into KISS and back by a TNC type (custom scripts for others), or read receive-only in the radio's APRS mode; commands typed to the TNC over the open connection | Open/Close Commands | App | G14 | built (0.5.0, [#242](https://github.com/APRS-NET/AprsMap/issues/242)) |
| F3.3 | Per-port RF beacon paths, RF versus IS source attribution on the map, heard-direct indication ([spec](features/F3.3-rf-paths.md)) | — | App | G20 | built (0.5.0, [#210](https://github.com/APRS-NET/AprsMap/issues/210)) |
| F3.4 | IGate: RF→IS, IS→RF for messages to local stations, objects; configurable rules | (core feature list), Receive-Only IGates Break Messaging | App | G16 | receive only built for 0.6.0 ([#206](https://github.com/APRS-NET/AprsMap/issues/206), [spec](features/F3.4-rx-igate.md)); two-way in 0.7.0, [#208](https://github.com/APRS-NET/AprsMap/issues/208)) |
| F3.5 | Digipeater: WIDEn-N, fill-in, own-call digipeat ([spec](features/F3.5-digipeater.md)): per KISS TNC, Off, Fill-in or Wide-area, only while the station may transmit there; what it repeated in the packet list and dashboard | Digipeating | App | G15 | built for 0.7.0 ([#209](https://github.com/APRS-NET/AprsMap/issues/209)) |
| F3.6 | GPS ([spec](features/F3.6-gps.md)): NMEA over serial or TCP, and gpsd, as [F1.25](features/F1.25-sources.md) sources; while the fix is fresh the station is where the GPS says (beacons with course and speed, the filter's range, the map's house); the OS location API, browser geolocation and a tracked position later | Android GPS | App | G17, G30 | built (0.6.0, [#205](https://github.com/APRS-NET/AprsMap/issues/205)) except the OS and browser location; decisions proposed |
| F3.7 | Frequency objects and radio tune format in comments | Frequencies, Radio Tune Format | App | G01 | scoped |
| F3.8 | AGWPE over TCP as an RF port: Dire Wolf's, UZ7HO SoundModem's or AGWPE's AGW port as a source, a radio port picked from the TNC's list, receive and transmit like a KISS TNC | Port Configuration | App | G22 | scoped for 0.8.0 ([#384](https://github.com/APRS-NET/AprsMap/issues/384)) |
| F3.9 | CWOP weather feed port (unfiltered; filtered locally) | CWOP | App | G21 | scoped |
| F3.10 | IS-Server: re-serve our stream to other apps/instances over TCP, with per-type pass-through and direction controls | IS-Server Port | App | G21 | scoped |
| F3.11 | Frequency monitor: decode frequency objects and comment frequency specs, flag malformed ones, list by frequency | (observed) | App | G25 | scoped |
| F3.12 | GPS status for a [F3.6](features/F1.25-sources.md) GPS source: the fix (position, altitude, speed and course, fix type and time), its accuracy (estimated error in metres where the receiver or gpsd gives it, otherwise HDOP/VDOP/PDOP and satellites used), a sky view (polar diagram of the satellites in view by azimuth and elevation, signal strength, used or not, by constellation), and the raw stream (NMEA lines from the serial port or TCP, or gpsd's JSON) in the Console panel ([F1.28](features/F1.28-source-console.md)) | — (maintainer request) | App | G17, G30 | built for 0.6.0 ([#331](https://github.com/APRS-NET/AprsMap/issues/331), [spec](features/F3.12-gps-status.md)) |
| F3.13 | GPS from a TNC's port ([spec](features/F3.13-tnc-gps.md)): a radio in its APRS mode (TH-D72A) sends its GPS with its packets; Auto, On or Off per source; a GPS source wins, the radio stands in while it has no fresh fix | APRS.NET#320 | App | — | built for 0.7.0 ([#362](https://github.com/APRS-NET/AprsMap/issues/362)) |
| F3.14 | GPS statistics on the dashboard ([spec](features/F3.14-gps-dashboard.md)): time with and without a fix, fix losses and outages, satellites used and in view by constellation, the dilutions of precision, the GPS's error estimates and signal, wander while still, over the dashboard's range; the GPS in use's fix time and losses as OTLP metrics | — (maintainer request) | App | G37 (metrics per connection) | built for 0.7.0 ([#382](https://github.com/APRS-NET/AprsMap/issues/382)) |

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
| F4.15 | Updates ([spec](features/F4.15-updates.md)): a daily check of the GitHub releases with a quiet notice, in-app install on desktop with Velopack, an operator-only notice on the gateway; store packages later | — (maintainer request) | App | — | built but for signing (0.5.0, [#186](https://github.com/APRS-NET/AprsMap/issues/186)) |
| F4.16 | Device list updates ([spec](features/F4.16-device-list.md)): fetch the aprs-deviceid list weekly (or now) from the APRS Foundation or a mirror, use whichever of it and APRS.NET's built-in list is newer, and name stations already heard again | — (maintainer request) | App | G35 | built for 0.6.0 ([#355](https://github.com/APRS-NET/AprsMap/issues/355)) |
| F4.17 | SBOM for every release ([spec](features/F4.17-sbom.md)): the release workflow builds a software bill of materials for each thing it ships (the desktop app per platform, the gateway, the browser app and the container image), in a standard format (CycloneDX or SPDX JSON, chosen in the spec), listing every package with its version, licence and hash, including `AprsNet.*` and transitive ones; attached to the GitHub release beside the files it describes, and to the image as an attestation; generated from the same resolved package graph as F1.34's About list, so the two always agree; a CI check that it's produced and well-formed | — (maintainer request) | App | — | built for 0.6.0 ([#352](https://github.com/APRS-NET/AprsMap/issues/352)) |

## Order of work

1. Phase 0 completely, including spikes (done).
2. The Phase 1 specs, reviewed before coding (written; each spec's decisions are settled when its milestone starts).
3. 0.3.0-alpha.1, the first map (done), then 0.4.0-alpha.1: KISS receive (F3.1) and the rest of the map features
   above marked 0.4.0. The F1 features left after that, then Phase 2.
4. In parallel, APRS.NET picks up the gaps each milestone is blocked by. For 0.4.0 all three are published as of
   APRS.NET `0.3.0-alpha.3`: G09 ([APRS.NET#153](https://github.com/APRS-NET/APRS.NET/issues/153), `TelemetryProfile`),
   G24 ([APRS.NET#154](https://github.com/APRS-NET/APRS.NET/issues/154), `TrackAnalysis`) and G13's serial connection
   ([APRS.NET#126](https://github.com/APRS-NET/APRS.NET/issues/126), `AprsNet.Kiss.Serial`, open until tested on
   hardware). Phase 1 needs no transmit work in APRS.NET.
5. 0.5.0-alpha.1 to 0.7.0-alpha.1 follow APRS.NET's milestones of the same numbers, each feature issue blocked by
   the APRS.NET capability it needs: 0.5.0 transmitting through APRS-IS and KISS TNCs, beacons and
   messaging (F2.1, F2.9 and F1.26 before anything sends; APRS.NET [#189](https://github.com/APRS-NET/APRS.NET/issues/189), [#196](https://github.com/APRS-NET/APRS.NET/issues/196)), and serial TNCs
   ([#160](https://github.com/APRS-NET/APRS.NET/issues/160)); 0.6.0 objects, queries, GPS, moving-station beacons and a receive-only IGate ([#195](https://github.com/APRS-NET/APRS.NET/issues/195), [#200](https://github.com/APRS-NET/APRS.NET/issues/200),
   [#203](https://github.com/APRS-NET/APRS.NET/issues/203), [#206](https://github.com/APRS-NET/APRS.NET/issues/206)); 0.7.0 the digipeater and the two-way IGate
   ([#209](https://github.com/APRS-NET/APRS.NET/issues/209), [#212](https://github.com/APRS-NET/APRS.NET/issues/212)), with the registered TOCALL first. The milestones:
   [0.5.0](https://github.com/APRS-NET/AprsMap/milestone/3), [0.6.0](https://github.com/APRS-NET/AprsMap/milestone/4),
   [0.7.0](https://github.com/APRS-NET/AprsMap/milestone/5).
