# Development status

What you should expect to see when you run AprsMap today, and what isn't there yet. Per-feature status lives in
[scope.md](scope.md); this page is the short version for someone trying the app.

As of 2026-10-15 (`0.1.0-alpha.8`): **Phase 0, foundations.** There is no map yet.

## What you'll see

### Desktop app

`dotnet run --project src/AprsMap.Desktop`, or a release archive.

1. **First run:** the setup wizard covers the window. It asks for your callsign, an optional location (Maidenhead
   locator, address or postal code, or coordinates; in the browser also "this device's location"), the APRS-IS
   server, and an optional passcode. The passcode calculator is there, behind the "licensed, own callsign" tick. Settings are saved in your user folder
   (`~/.aprsnet/aprsmap` on Linux, `%APPDATA%\aprsnet\aprsmap` on Windows, `~/Library/Application Support/aprsnet/aprsmap`
   on macOS).
2. **After that:** one window with:
   - a status bar with the product name, the profile picker (to switch between profiles, which reconnects), the APRS-IS
     connection state and server, packet and station counts, the latest error, if any, and a gear for Settings;
   - a live list of received packets, newest first, with the source callsign and a one-line summary.
3. **Settings** (the gear, or Ctrl+, / Cmd+,): **Appearance** (theme, standard, high-contrast or colour-vision-safe
   colours, text size up to 200 %, icon size, corners, density, motion, language), applied live and remembered per
   viewer, with Reset and Undo; **Profiles** (add, edit, copy, delete with Undo, switch; read-only on a gateway without
   operator sign-in); and **About** (versions, licences, source link, credits).
4. **Traffic:** a profile without a filter of its own receives everything within 150 km of its location (`r/` filter),
   or 150 km around the callsign's last position on APRS-IS when it has no location (`m/` filter). A profile can also
   carry its own APRS-IS filter, set when adding or editing it in Settings.

### Web app

The web app runs only when the **gateway** serves it: the gateway runs the engine, and browsers connect to it.

- **To try it:** `APRS_CALLSIGN=YOURCALL-10 docker compose up --build`, then open <http://localhost:8080>.
- **First start:** the callsign, server and filter come from the environment the first time only, and are then stored
  in the `settings` volume. The compose file's sample filter is around Boulder, Colorado (`r/40.0/-105.0/50`). Change
  `Engine__Filter` before the first start, or remove the volume to start over.
- **What browsers see:** the same status bar and packet list as the desktop app. Settings are read-only from a browser,
  unless `Gateway__AllowSettingsChanges=true` (see [deployment.md](deployment.md)). So without a callsign the page says
  the gateway isn't set up, rather than showing the wizard.
- **Running `src/AprsMap.Browser` on its own** (`dotnet run`, or Run in an IDE) **doesn't work.** A development build
  can't start in the browser (only a publish includes the WebAssembly native libraries, SkiaSharp among them), and
  there is no gateway at that address anyway. The page says so within seconds and tells you to start the gateway. Use
  the gateway as above.
- **If the page says it can't reach the gateway:** the gateway isn't running, or the app was opened from another
  address. The page shows the address it tried, and Try again reloads it.

## Not there yet

| What | Where it's planned |
| --- | --- |
| The map: tiles, station symbols, tracks | F1.4 and the rest of Phase 1. The approach is proven in the F0.3 spikes (Mapsui in the browser, symbol atlas, tile cache) but not built into the app |
| Station details, messages, weather, telemetry, objects | Phase 1 |
| A filter that follows the map | F1.2; until then the fixed 150 km filter above |
| A map picker in the wizard; "this device's location" on desktop | The picker comes with the map (F1.4); desktop location with F3.6 |
| Translations | F0.9 groundwork is done (English only); translations come from contributors |
| Sending anything (beacons, messages) | Phase 2 |
| RF: TNCs, KISS, digipeater, IGate | Phase 3 |

## Known issues

- **Packet summaries are English:** the one-line summaries in the packet list come from the engine in English, whatever
  the language. They become structured with F1 station details (F0.9, behavior 16).
