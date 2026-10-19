# Development status

What you should expect to see when you run AprsMap today, and what isn't there yet. Per-feature status lives in
[scope.md](scope.md); this page is the short version for someone trying the app.

As of 2026-10-18 (`main`, everything planned for `0.5.0-alpha.1`): **receive and transmit, over APRS-IS and your own
TNCs.** The app connects to APRS-IS and to KISS TNCs over TCP (Direwolf, soundmodem) and on serial ports (a TH-D72A's
built-in TNC, TNC-X, NinoTNC), draws the stations on a live map with their tracks and RF paths, lists them and their
packets, and has a dashboard of live numbers beside the map. Once you turn transmitting on, it sends your position and
status beacons, messages with acks and retries, and bulletins, and joins ANSRVR groups. The gateway can run as a public,
receive-only page with a private operator port, and only a signed-in operator can transmit there. The desktop app
installs, and finds and installs its own updates.

**Next:** [0.6.0](https://github.com/APRS-NET/AprsMap/milestone/4) (objects, GPS, a receive-only IGate, a conversation per
ANSRVR group), then [0.7.0](https://github.com/APRS-NET/AprsMap/milestone/5) (a digipeater and a two-way IGate).

## What you'll see

### Desktop app

An installer from the release (Windows MSI, macOS pkg, Linux AppImage), a release archive, or
`dotnet run --project src/AprsMap.Desktop`. The installed app checks for updates and offers **Update and restart**; the
AppImage adds itself to the app grid, and for an archive `tools/install-linux-desktop.sh` does (GNOME shows the icon only
through that entry). See [deployment.md](deployment.md#desktop-app) for where each system keeps the files.

1. **First run:** the setup wizard covers the window. It asks for your callsign, an optional location (Maidenhead
   locator, address or postal code, coordinates, or a point on a map; in the browser also "this device's location"),
   the APRS-IS server or **a TNC instead** (RF only, no passcode), and an optional passcode. The passcode calculator is there, behind the "licensed, own callsign" tick.
   Settings are saved in your user folder (`~/.config/aprsnet/aprsmap` on Linux, `%APPDATA%\aprsnet\aprsmap` on Windows,
   `~/Library/Application Support/aprsnet/aprsmap` on macOS).
2. **The window:** the map in the centre, with panels around it that dock, tab, split, auto-hide and float. The layout
   and the window's place are remembered. The menus (File, View, Station, Tools, Help) are hidden until Alt or F10 on
   Windows and Linux, in the system menu bar on macOS, and under ☰ everywhere; View › Panels brings back closed panels,
   View › Reset layout puts them back, and View › Layouts holds saved layouts.
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
   - **Sources:** the APRS-IS servers and TNCs profiles pick from: KISS over TCP (host, port, TNC port), KISS on a
     serial port with the TNC's type so the app can switch it into KISS, or TNC text output (TNC2) over serial (RX
     only). Each can be RX only, and has Test send.
   - **Beacons:** position and status beacons, fixed, decaying or SmartBeaconing-style, and the path per source.
   - **Map:** tile provider, brightness, labels, symbol size, the filter circles, and when stations fade and go.
   - **Data:** packet capture on or off, and its limits.
   - Actions are in the menus: **Tools › Clear data…** (stations, stale stations, tracks or the packet history),
     **Tools › Start over…** (delete everything and run first-run setup again; `--reset-all` at start-up), and
     **Help › About** (versions, the connection's details, licences, source link and credits).
5. **Transmitting:** off until you turn on the **Transmit** switch in the status bar (or Station › Transmit) with a verified passcode or
   a licensed callsign on RF. Beacons, **Messages** (conversations with ack and retry, unread in the status bar),
   **Bulletins** (reading, posting, ANSRVR groups) and the Sent marker in the packet list. Each source's pill has
   **Console…** to watch its raw traffic, and Station › Destination sets a point to show distance and bearing to.

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
| A conversation per ANSRVR group | F2.5, [0.6.0](https://github.com/APRS-NET/AprsMap/milestone/4) ([#285](https://github.com/APRS-NET/AprsMap/issues/285)) |
| Objects and items, GPS, a receive-only IGate, dead reckoning, extra map views | [0.6.0](https://github.com/APRS-NET/AprsMap/milestone/4) |
| Digipeater, two-way IGate, web pop-out panels | [0.7.0](https://github.com/APRS-NET/AprsMap/milestone/5) |
| Signed installers (Windows SmartScreen and macOS warn on first install) | F4.15 behavior 14, when there's a signing provider |
| Translations | F0.9 groundwork is done (English only); translations come from contributors |

## Known issues

- **Packet summaries are English:** the one-line summaries in the packet list come from the engine in English, whatever
  the language (F0.9, behavior 16).
- **Touchpad pinch on Linux:** under XWayland, pinch gestures don't reach the app; two-finger scrolling zooms instead.
- **Desktop performance check:** the browser's run (60 frames a second with 2,000 stations) is recorded in F1.4; the
  desktop's isn't yet.
