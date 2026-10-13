# Black-box observation, 2026-10-13

APRSIS32 running under Wine on the maintainer's Fedora workstation (XWayland), driven by Claude through synthetic X11
input (XTest) and window screenshots. Only the UI was observed: no binaries, resources, memory or configuration files
were read. This adds detail to the [functional inventory](functional-inventory.md), which is based on the wiki and
describes an older version.

**Method and safety.** Menus were opened and submenus revealed by hovering. Menu items were **not** clicked, except
dialog-opening items (ending in "…"), which were captured and then cancelled. The Transmit and Exit menus were not
touched. The app was in its first-run state (home location not yet confirmed, passcode not set, so receive-only) and
stayed that way. Screenshots are kept out of the repository: they contain third parties' callsigns and comments (one
included an email address), and the reference's look is not to be copied.

Everything below is our paraphrase of *what exists and what it does*. Option names are descriptive, not the reference's
labels.

## Main window

- The map fills the window, with a range ring around the centre marked with compass letters. A one-line first-run hint
  at the bottom centre asks the user to position the map on home and then transmit.
- A narrow column down the left edge is the live heard list: callsign and symbol per entry, colour-coded (green, olive,
  red, yellow highlight), newest at the top, with a small +/- zoom strip next to it.
- Top-left: connection state ("connected" plus a session timer). Top-right: product badge. Bottom-left stacked boxes:
  zoom/scale value, grid square, and lat/lon of my position. Bottom-right: a clock/date. Vertical bars on the right edge
  (battery, station count).
- Map right-click (empty area): centre on me, keep window on top, trace log shortcuts, and the clicked point's
  coordinates with a submenu (point actions).

## Station pop-up (left-click a station)

- Header: symbol, name, device/software with packet count, comment, age, and reception class ("remote RF" vs local,
  IS…).
- Actions: open a tracking window for it, follow (centre), remove it locally, set as destination, set a nickname,
  internet lookup (reverse-geocode address), registered-owner (WHO-IS) lookup.
- A path submenu shows the source, its TOCALL, the q-construct, and the IGate (itself a station submenu).

## Feature toggles (Enables)

APRS-IS on/off; GPX track auto-save; beaconing; CSV log; **frequency monitor**; GPS (greyed until configured); internet
access master switch; tile fetching; sound; telemetry transmit. Submenus: ports (all on, log all, receive-only,
**digipeat**), logging (all at start-up, log to file, all off, then about 37 per-subsystem categories such as filter,
filter test, frequency-spec parsing, IGate, messages, tiles, packets, per-port packets, timers, transmit, wind barbs),
and a log viewer per category.

## Messages

Read next, send to callsign, send email, pending list. Announcement groups (active, describe, mine, join). Auto-reply
(text, only when inactive for N, no more than once per N). Bulletins (create, notify, view). Chats (new).

## Display (Screen)

- Toggles: dead reckoning, filter circle (shows the current range filter, for example `r///40km`), tracks.
- Auto-zoom (zoom to fit all, zoom in only, zoom out only, auto-centre, follow-locked).
- Map brightness (bright, dim). DF circles (on, opacity).
- Follow: find, centre, me, lock.
- Labels: altitude, ambiguity, callsign, footprint, nicknames (submenu), NWS calls, speed (submenu), weather.
- **RF paths drawn on the map**: appearance settings, local RF only, network links (digipeater-to-digipeater), per-station
  packet paths, my own packet paths.
- PHG circles (full, half, opacity). Saved views (restore, save, per tracking window). Tile set choice.

## View filters

All (with count) / none / a chooser dialog, then categories with counts: has altitude, ambiguous, buddies, duplicates,
**frequency objects**, **frequency objects with problems**, invalid, message-capable, nicknamed, NWS objects, objects,
**paths**, telemetry, tracks. Submenus:

- **Hashtags** (`#tag` in comments; dynamically built, "0/64" capacity shown).
- **Mic-E status**: one entry per Mic-E message code (emergency, off duty, en route, in service, special, priority,
  custom, and so on) with counts and how each is alerted.
- **Platforms**: device families with percentages (generic, UI-View, Kenwood, xastir, APRSdroid, aprx, Kantronics…),
  nested by vendor. That is device identification in use.
- **RF**: all / direct / local / third party. **Shrieks** (`!tag!`), dynamic with capacity.
- **Symbols**: every heard symbol by name with count and percentage.
- **Transport**: RF only, RF + IS, IS only, plus digipeaters and IGates as roles.
- EchoLink and geocache layers.

## Configuration (Configure)

| Area | What exists |
| --- | --- |
| General (dialog) | Callsign-SSID; passcode (`-1` = none, receive-only); quiet-time reconnect (60 s); default range in tenths of a mile (500 = 50 mi); extra filter text; beacon comment; symbol picker; confirm on close; restart from About; buttons to the predictive-beacon, GPS and PHG settings |
| Predictive beaconing (dialog) | Minimum time (10 s), maximum time (30 min), time-only mode, beacon on start/stop of motion, heading-change threshold (30°), forecast error (0.1 mi), maximum distance (1 mi) |
| Status (dialog) | Beacon comment with rotation interval (0 = every beacon); status report on/off, include grid square, timestamp, interval (30 min), DX info, status text |
| Aliases | Known, accumulate, possible, unused |
| AltNet | None, or a new AltNet |
| Beacon | Wait for first manual transmit; timestamp; HMS timestamp; altitude; compressed; course/speed; saved comments; **Mic-E status to send**; saved symbols; trigger reason in comment; precision (normal, extra digit / DAO, reduced digit counts, ambiguity ±0.1/1/10/60 nm); path |
| Companions | Enable count and a "new companion" entry. **Purpose unknown**: needs maintainer observation |
| DX | Minimum distance (5 mi), alert trigger (10 mi), alert interval (10 min), window (60 min), all-time max, ignore list |
| Map | Prefetch, tile purger, tile sets (new tile set; built-in "Original") |
| Messages | Messages to any of my SSIDs; **double-ack on RF**; eavesdrop RF; eavesdrop all; except NWS; hide queries; notify on query; notify on new; open tracking windows for new items; WHO-IS detail (brief or full address); **retry profiles**: default (8,16,8,32,64,96,128 s), APRS-IS (32,32,64,96,128 s), no retries, and a slow HF-modem profile (45,90,180 s) |
| Units | Chosen per quantity: altitude, distance, pressure, rainfall, temperature, wind |
| Mic-E notifications | Suppress all, suppress emergency too; per Mic-E code: on/off with alert style (message box, new, active, flash, colour) |
| NWS | New office; messages; messages not addressed to `*ALL*`; notify; notify line type; products; pop-up tracking window; show offices; NWS entry servers (three preconfigured: US, fire weather, Australia) |
| NWS shapes | Add shapefile, enabled, opacity, quality, show centre |
| Objects | Create (with count), **new FT8 / JS8Call / JT65 objects**, new weather object, show, max objects per group, object server (enabled, interval 10 min, max objects 5, range 20 mi, retry messages) |
| Overlays | Add GPX file, add POS file |
| Ports | New port; existing ports listed (APRS-IS, NMEA). Port types offered: KISS (already in KISS), KISS (with init), AGW, Yaesu FTM-350, Kenwood D700 (KISS and a second mode), Kenwood D710 (built-in APRS mode and packet mode), plain text TNC, CWOP |
| Save positions | Reload paths; save filter with count |
| Screen | Label options (overlap, callsign, footprint, geocache, max visible); altitude; battery; circle; crosshairs; date/time (none, local, UTC, GPS, performance; date only, default, time only, custom); grid square; lat/lon; orientation; prediction error dot; GPS satellites; speed font size; **symbol size**; track styles (followed, other); **wind barbs** |
| Packet list | Freeze on click, show IGate/digipeater, filter, hide unparsed, hide internal, hide mine (exact and all SSIDs), RF only, show all |
| Status report | Enabled, grid square, timestamp, DX, new text, none |

## Clear

Eavesdrops, CQ/all, group messages, my messages, announcement-server messages, NWS messages, Mic-E messages, paths,
stations, tracks. Accumulated: AltNets, bulletins, list column width, **tactical nicknames**, telemetry, weather, window
positions. Configured: auto-replies, beacon comments, Mic-E ignores, status reports, symbols, **tactical sources**.

## New findings versus the wiki

Frequency monitoring and frequency-object validation; RF path and network-link visualization; hashtag filters; Mic-E
status filtering and notification styles; platform (device) statistics; per-quantity units; retry profiles including an
HF-modem profile; RF double-ack; digital-mode (FT8/JS8Call/JT65) objects; Companions; tactical callsigns and their
sources; wind barbs; symbol size; auto-zoom; map dimming; window positions.

## Open items for the maintainer (observe in the running app)

1. What Companions are, and what "Save positions › save filter" does.
2. What FT8/JS8Call/JT65 objects carry (frequency/time objects for digital-mode activity?).
3. The frequency monitor: what it watches and what counts as a frequency object "with issues".
4. Tactical nicknames: whether the sources are a bulletin or message convention (the `TACTICAL` callsign scheme) or local
   only.
5. The second Kenwood D700 mode and what the FTM-350 port does differently.
6. The colour meanings in the left-hand heard list.

## Sources

Maintainer's running copy of APRSIS32 under Wine, observed via UI screenshots and synthetic input by Claude,
2026-10-13. Version string not captured (About was not opened).
