# Development status

What you should expect to see when you run AprsMap today, and what isn't there yet. Per-feature status lives in
[scope.md](scope.md); this page is the short version for someone trying the app.

As of 2026-10-16 (`main`, `0.4.0-alpha.1`): **receive-only, from APRS-IS and from your own TNC.** The app
connects to APRS-IS and to KISS TNCs over TCP (Direwolf, soundmodem), draws the stations on a live map with their
tracks and RF paths, lists them and their packets, and has a dashboard of live numbers beside the map. It doesn't send
anything yet.

**Next:** [0.5.0-alpha.1](https://github.com/APRS-NET/AprsMap/milestone/3): transmitting through APRS-IS and KISS
TNCs (your station's transmit switch, TX enabled and Test send per source, Disable TX per profile), beacons, messaging
and bulletins, operator sign-in on the gateway, the public receive-only console (F1.26), capturing unexpected packets for the test corpus (F1.27), KISS TNCs on USB serial such as a TH-D72A's built-in TNC (F3.1), switched into KISS by the app (F3.2) and update checks. Then
[0.6.0](https://github.com/APRS-NET/AprsMap/milestone/4) (objects, GPS, a receive-only IGate) and
[0.7.0](https://github.com/APRS-NET/AprsMap/milestone/5) (a digipeater and a two-way IGate).

## What you'll see

### Desktop app

`dotnet run --project src/AprsMap.Desktop`, or a release archive. On Linux, `tools/install-linux-desktop.sh` adds the app
to the app grid with its icon (GNOME shows the icon only through that entry).

1. **First run:** the setup wizard covers the window. It asks for your callsign, an optional location (Maidenhead
   locator, address or postal code, coordinates, or a point on a map; in the browser also "this device's location"),
   the APRS-IS server or **a TNC instead** (RF only, no passcode), and an optional passcode. The passcode calculator is there, behind the "licensed, own callsign" tick.
   Settings are saved in your user folder (`~/.aprsnet/aprsmap` on Linux, `%APPDATA%\aprsnet\aprsmap` on Windows,
   `~/Library/Application Support/aprsnet/aprsmap` on macOS).
2. **The window:** the map in the centre, with panels around it that dock, tab, split, auto-hide and float. The layout
   and the window's place are remembered; the menu (☰) brings back closed panels and resets the layout.
   - **Status bar:** the product name, the profile picker (switching reconnects), the APRS-IS pill and one pill per
     TNC, packet and station counts, and a gear for Settings. Each pill has an on/off switch on its left, is light green
     when connected, and its menu offers Reconnect now; the APRS-IS pill shows an eye when the login isn't verified
     (receive-only), and its menu also has the quick toggles for the Internet and map tiles. Connections reconnect by
     themselves and say why and when; a refused login waits instead.
   - **Map:** OpenStreetMap-style tiles (cached), station symbols with overlays, callsign labels where they fit, and
     stations that fade when stale and go when old. Tracks, area objects (circles, boxes, lines) and, for the selected
     station, its RF paths as dashed lines to the digipeaters and IGates that heard it (or all of them with **Show all
     connections**). Pan, zoom (mouse, wheel, touchpad, keys), Home and Fit; a button clears the stations off the map.
     Hover for a station's name and distance; click to select it, or the house to select your own station and see what
     your radio heard. Right-click (or long-press) sets my location, with Undo. Optionally, the filter's ranges as
     dashed circles. A **View filter** narrows what's drawn (kind, category, device, how it arrived, with weather).
   - **Dashboard** (a tab beside the map): stations and packets heard, new stations, packet rates and types, problems,
     each connection's state with its switch, busiest stations and digipeaters, farthest direct hops, weather extremes,
     and tables of devices, symbols, kinds and reception, over the last hour to the last day.
   - **Stations** (left): every station, object and item, searchable, sorted by last heard, name or distance, and
     filtered to stations or objects and items; Centre and Follow.
   - **Details** (right): the selected station's position and locator, distance and bearing from me, speed, course,
     altitude, comment and status, how it arrived (heard by this station, direct, digipeated or internet) and its path,
     which source it came from and where else it was seen, device, weather, telemetry with named channels, anomalies
     (positions that don't fit its track), and last packet. Centre, Follow and Copy position.
   - **Packets** (bottom): the live packet list, newest first: time, symbol, source, summary and markers (direct,
     digipeated with the hop count, duplicate, heard here, parse issues and anomalies). A row expands to the raw text
     with the faulty part marked. Filter by text, station, type, source or issues; pause (Space), and resuming jumps
     back to the newest; Copy, and Save as text, CSV, JSON or YAML.
3. **Traffic:** the APRS-IS filter is automatic: my range around my location, plus the area the map shows (up to
   1,000 km across), plus a followed station's surroundings, plus the source's own filter text if it has one. It
   updates as you pan and zoom without logging in again.
4. **Settings** (the gear, or Ctrl+, / Cmd+,):
   - **Appearance:** theme, standard, high-contrast or colour-vision-safe colours, text size up to 200 %, icon size,
     corners, density, motion, language; applied live, per viewer, with Reset and Undo.
   - **Profiles:** add, edit, copy, delete with Undo, switch.
   - **Sources:** the APRS-IS servers and KISS over TCP TNCs profiles pick from (host, port, TNC port). KISS over
     serial is listed as coming.
   - **Map:** tile provider, brightness, labels, symbol size, the filter circles, and when stations fade and go.
   - **Data:** clear stations, stale stations, tracks or the packet history, and Reset the app (delete everything and
     run first-run setup again; `--reset-all` at start-up).
   - **About:** versions, the connection's details, licences, source link and credits.

### Web app

The web app runs only when the **gateway** serves it: the gateway runs the engine, and browsers connect to it. They see
the same window as the desktop app, live.

- **To try it:** `APRS_CALLSIGN=YOURCALL-10 docker compose up --build`, then open <http://localhost:8080>. Release images
  are at `ghcr.io/aprs-net/aprsmap`.
- **First start:** the callsign, server and filter come from the environment the first time only, and are then stored
  in the `settings` volume. The compose file's sample filter is around Boulder, Colorado (`r/40.0/-105.0/50`). Change
  `Engine__Filter` before the first start, or remove the volume to start over.
- **Viewers:** anyone who can reach the gateway can watch. Settings are read-only from a browser unless
  `Gateway__AllowSettingsChanges=true`, so without a callsign the page says the gateway isn't set up rather than showing
  the wizard. Each browser keeps its own layout and appearance, and with `Gateway__FollowViewers` its map view widens
  the gateway's filter. The gateway limits viewers and their call rate; see [deployment.md](deployment.md) for these
  and for running behind a proxy or under a path.
- **Running `src/AprsMap.Browser` on its own** (`dotnet run`, or Run in an IDE) **doesn't work.** A development build
  can't start in the browser (only a publish includes the WebAssembly native libraries, SkiaSharp among them), and
  there is no gateway at that address anyway. The page says so within seconds. Use the gateway as above.
- **If the page says it can't reach the gateway:** the gateway isn't running, or the app was opened from another
  address. The page shows the address it tried, and Try again reloads it.

## Not there yet

| What | Where it's planned |
| --- | --- |
| RF: TNCs over USB serial (a TH-D72A's built-in TNC in KISS mode, then MFJ TNC-X, NinoTNC) | F3.1, [0.5.0-alpha.1](https://github.com/APRS-NET/AprsMap/milestone/3) ([#177](https://github.com/APRS-NET/AprsMap/issues/177)) |
| Dashboard extras: sparkline values on hover and by keyboard, My view, Copy on the tiles | F1.22, [#176](https://github.com/APRS-NET/AprsMap/issues/176) |
| A public receive-only console: viewer and operator ports, location privacy | F1.26, spec in review |
| Recording unexpected packets for the developer, exported anonymized for the test corpus | F1.27, spec in review |
| Extra map views, dead reckoning, destination, saved layouts, web pop-out panels | Phase 1, later |
| Objects, bulletins and operator sign-in on the gateway | Phase 2 |
| GPS, IGate, digipeater | Phase 3 |
| Translations | F0.9 groundwork is done (English only); translations come from contributors |

## Known issues

- **Packet summaries are English:** the one-line summaries in the packet list come from the engine in English, whatever
  the language (F0.9, behavior 16).
- **Touchpad pinch on Linux:** under XWayland, pinch gestures don't reach the app; two-finger scrolling zooms instead.
- **Desktop performance check:** the browser's run (60 frames a second with 2,000 stations) is recorded in F1.4; the
  desktop's isn't yet.
