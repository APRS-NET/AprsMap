# Functional inventory of the reference app

Capabilities found in APRSIS32's public documentation, paraphrased and grouped by what the user is trying to do (not
by the reference's menu structure). The last column maps each one to [scope.md](../scope.md). "—" means deliberately not
carried over, with the reason given.

Notes on reading this file:

- The reference grew on Windows Mobile and was later run on desktop Windows, so many items are workarounds for small
  screens and single-window UIs. [ux](../ux/README.md) decides how each need is met in a modern layout.
- Several wiki pages are stubs; gaps are marked "(docs thin)" and need maintainer observation before a spec is
  written.

## 1. First run and identity

| Capability | Scope |
| --- | --- |
| Minimal first run: enter callsign-SSID, then pan/zoom the map to your home and confirm it as your position | F0.5 |
| Passcode entry for APRS-IS transmit; without one, the client is receive-only | F2.1 |
| Station symbol picker, used locally and in transmitted positions | F2.1 |
| Beacon comment with saved alternatives to choose from | F2.2 |
| PHG (power, height, gain, directivity) entry | F2.2 |
| Aliases: other callsigns treated as "me" for messages | F2.4 |

## 2. Connections ("ports")

| Capability | Scope |
| --- | --- |
| Port types: APRS-IS client, KISS TNC, "simple KISS" (already in KISS, no init), text TNC via open/close command scripts, AGWPE over TCP, NMEA GPS, CWOP feed, IS-Server | F1.1, F3.1, F3.2, F3.6, F3.8, F3.9, F3.10 |
| Physical links per port: serial (baud, parity, data/stop bits), TCP/IP, Bluetooth serial | F3.1 |
| Per-port **quiet time**: close and reopen the port when nothing is heard for N seconds (APRS-IS default 60 s); 0 disables. Advice: 2–3× the channel's normal quiet gap | F1.1, F3.1 |
| Per-port switches: enabled, transmit, beacon, messages, bulletins, telemetry, RF→IS and IS→RF gating | F3.3, F3.4 |
| APRS-IS server choice: rotate pool on port 14580 by default, regional pools as alternatives | F1.1 |
| Global switches: all ports on, log all ports, receive-only for all ports | F3.3 |
| CWOP port: unfiltered worldwide weather feed (port 10152); filter requests are ignored by those servers | F3.9 |
| IS-Server port: re-serve the client's stream over TCP to other apps or instances (local or LAN), with per-type pass-through and direction controls; downstream clients log in with their own SSID and passcode | F3.10 |
| Master "Internet access" switch that gates everything online (APRS-IS, tile fetching, update checks) | F1.15 |

## 3. What the APRS-IS server sends (automatic filtering)

| Capability | Scope |
| --- | --- |
| Range filter around my position, from a configured default range (0 = no range part, useful for IGates) | F1.2 |
| When following a station, the range centres on that station (friend-range) instead | F1.2 |
| When the map is panned away, a range filter around the map centre is added | F1.2 |
| A visible filter circle on screen; larger circles enlarge the range part | F1.2 |
| Tracked stations in extra tracking windows are added as buddy (or friend-range) filters | F1.2, F1.16 |
| Remembered-but-unheard stations get station and object filters until heard | F1.2 |
| Special parts: entry filter for NWS alert sources; a universe filter for a selected AltNet | F4.3, F4.11 |
| User-supplied extra filter text appended to the automatic parts | F1.2 |

## 4. Map

| Capability | Scope |
| --- | --- |
| Slippy map with selectable tile sets; user-defined tile servers with URL templates (z/x/y order, inverted y, jpg or png), per-set cache folder, min/max zoom, retention days, purge on/off | F1.4, F4.5 |
| Tile prefetch for an area or along a route; purge policy for old tiles | F4.5 |
| Saved "preferred" view (centre and zoom) to return to | F1.4 |
| Crosshair modes (always, never, transient) | F1.4 |
| Range rings around the centre; GPS satellites drawn in the ring (docs thin) | F1.4, F3.6 |
| Labels: callsign, altitude, speed, weather, NWS calls, nicknames; overlap allowed or not; maximum visible labels | F1.4 |
| Ambiguity circles for imprecise positions | F1.4 |
| PHG coverage circles (fixed and mobile, adjustable transparency, directional) | F4.2 |
| Footprint circles for high-altitude stations (balloons) above a threshold | F4.2 |
| Direction-finding circles | F4.10 |
| Dead-reckoned positions for moving stations, with a freshness indicator | F1.17 |
| Tracks: history lines; selected station's actual transmit points highlighted; colour, thickness and length differ for followed vs other stations | F1.5 |
| Overlays: local GPX (tracks, routes, waypoints) and UI-View-style POS files, toggled individually, persistent; a waypoint can be turned into a transmitted object; "drive" a route to prefetch tiles | F4.12 |
| Grid square and lat/lon of my position shown on screen | F1.13 |

## 5. Finding and following stations

| Capability | Scope |
| --- | --- |
| Find a station by name and go to it | F1.7 |
| Re-centre the map on whichever station was picked most recently (falls back to my own position) | F1.7 |
| Lock: keep the map centred on a moving station | F1.7 |
| Multitrack: extra map windows, each following one station | F1.16 |
| Set destination: distance and bearing to a chosen point | F1.18 |
| Station list beside the map; click for range and bearing | F1.7 |

## 6. Which stations are shown (view filtering)

| Capability | Scope |
| --- | --- |
| All / None, with individual selections remembered for quick recall | F1.9 |
| Category toggles: ambiguous, buddies, duplicates, invalids, message-capable, nicknamed, NWS objects, objects, telemetry, with tracks | F1.9 |
| By platform (device type), with counts | F1.9 |
| By RF reception: direct, local, third-party, all RF | F1.9 |
| By transport: RF only, RF and IS, IS only | F1.9 |
| By symbol | F1.9 |
| By "shriek" tags (`!tag!`-style markers in comments), built dynamically from what is heard | F1.9 |
| EchoLink and geocache layers (docs thin) | — (out of scope: non-APRS data sources) |

## 7. Station details

| Capability | Scope |
| --- | --- |
| Pop-up on click: callsign, comment, age of last position, how it was received | F1.6 |
| Actions: multitrack, follow, reverse-geocode address, set destination, save or clear track, who-is (registered home location lookup), send message, path details | F1.6, F4.6 |
| Nicknames: local alternate label and symbol for a station | F4.7 |

## 8. Raw packets ("scroller")

| Capability | Scope |
| --- | --- |
| Live packet list with source markers: heard on RF, third-party RF, digipeated, repeat count | F1.8 |
| Symbol per line, boxed when heard directly on RF | F1.8 |
| Text colour by distance (near to far); highlight for buddies | F1.8 |
| Background colour by anomaly: duplicate position, sub-second repeats, impossible speed (> 20,000 mph), restart, other users of the same client, invalid | F1.8 |
| Freeze while reviewing; click for station details | F1.8 |

## 9. Messaging

| Capability | Scope |
| --- | --- |
| Chat window per correspondent; one compose box takes up to 300 characters and the client sends it as several messages of at most 64 characters | F2.4 |
| Per chat: request acknowledgement on or off (off for satellite use); routing "best" (chosen from where the station was last heard), RF only, or IS only | F2.4 |
| Retry schedule: decaying intervals, 7 tries over about 6 minutes, avoiding retries under 30 s to APRS-IS (duplicate filter); a second round restarts when the recipient is heard again; then a "final" state until manually retriggered or an `?APRSM` query arrives | F2.4 (G10) |
| Pending (unacknowledged) messages list with highlighting | F2.4 |
| Read next unread (by priority), send to a callsign | F2.4 |
| My messages for any SSID of my call; eavesdrop on RF-heard messages; eavesdrop on everything received | F2.4 |
| Queries: auto-answered; optionally hidden or notified | F2.8 |
| New-message pop-ups (optional; noisy on unattended systems) | F2.4 |
| Email via APRS email gateway | F2.6 |
| Bulletins: view, create, notify on new | F2.5 |
| Announcement groups via ANSRVR (or alternative CQSRVR): list, describe, join, leave, keep-alive, join quietly, IS only, CQ text | F2.5 |
| Auto-reply with delay and frequency limits | F2.10 |
| Message-delivered items open their own tracking window (optional) | F1.16 |

## 10. Beaconing

| Capability | Scope |
| --- | --- |
| Transmit now | F2.2 |
| Interlock: no beacons until the user transmits once manually (confirms position first) | F2.1 |
| Timestamp on positions (DHM zulu default, optional HMS) | F2.2 |
| Altitude, course/speed (not with compressed), compressed format | F2.2 |
| Precision choice: standard, enhanced (`!DAO!`), or deliberately ambiguous | F2.2 |
| Optional "why" (trigger reason) in the comment for diagnosis | F2.2 |
| Path per beacon (for example WIDE1-1,WIDE2-1) | F2.2 |
| Predictive beaconing: extrapolate where others think you are from your last beaconed course and speed; beacon when the error exceeds a set distance, but not before a minimum interval; an on-screen error indicator shows drift direction and size | F2.2, F2.3 (G11) |
| Status reports with saved alternatives | F2.11 |
| Telemetry transmit enable | F4.1 |
| Log beacon trigger locations to CSV | F4.13 |

## 11. Objects and items

| Capability | Scope |
| --- | --- |
| Create an object at the map centre; edit, move, kill | F2.7 |
| Multiline objects (shapes) | F2.7 |
| Weather objects fed from a local data file (auto-disabled when the file is unreadable) | F4.9 |
| Show local objects without transmitting | F2.7 |
| QRU-style object server: answers queries with nearby objects, with group limits, range limits and intervals | F2.6 |
| Frequency objects in the radio-tune format | F3.7 |

## 12. Weather, NWS, telemetry, DX, AltNet

| Capability | Scope |
| --- | --- |
| Weather station readings on labels and details | F1.10 |
| NWS: offices, products, message display and notification, alert areas from shapefiles (docs thin) | F4.3 |
| Telemetry display and transmit | F1.11, F4.1 |
| DX: track the furthest direct, zero-hop position report; thresholds, alert interval, expiry window, all-time record, ignore list; optional alert as DX-cluster or status packet | F4.11 |
| AltNet: monitor or ignore traffic sent to a chosen non-standard destination (for example TEST) | F4.11 |

## 13. Housekeeping

| Capability | Scope |
| --- | --- |
| Clear: eavesdropped, broadcast, own and NWS messages; stations; tracks; AltNets; bulletins; telemetry; saved comments, status texts, symbols, auto-replies | F1.19 |
| Feature toggles in one place: APRS-IS, GPS, beaconing, telemetry, sound, tile fetching, GPX auto-save, CSV log | F1.15 |
| Debug logging per subsystem, and a log viewer | F1.8 |
| Auto-save tracks as GPX | F4.13 |
| Units: metric or imperial | F0.4 |
| GPS status: satellites, fix, precision, altitude, speed, heading, odometer | F3.6 |
| Clock display (local, UTC, GPS) | — (OS provides it; we show times in UTC/local per preference) |
| Battery bar | — (OS provides it) |
| Windows Mobile soft keys, orientation modes | — (replaced by responsive layout, [ux](../ux/README.md)) |

## Sources

Facts only; no text copied (checked with `tools/overlap-check.py`: no shared run of 8+ words). The wiki is CC BY-SA 3.0.
All accessed 2026-10-13, aprsisce.wikidot.com: `/start`, `/menu:tree`, `/menus`, `/doc:documentation`,
`/menu:enables`, `/menu:screen`, `/menu:view`, `/menu:clear`, `/menu:configure`, `/menu:messages`,
`/menu:configure-general`, `/menu:configure-beacon`, `/en-geniusbeaconing`, `/port-configuration`, `/port-kiss`,
`/doc:automatic-filters`, `/doc:screen-elements`, `/doc:screen-layout`, `/doc:stationinfo`, `/chat`, `/message-retries`,
`/doc:digipeating`, `/menu:configure-map`, `/tile-sets`, `/menu:configure-objects`, `/port-aprs-is`, `/is-server-port`,
`/notifications`, `/menu:configure-screen`, `/scroller`, `/initial-startup`, `/menu:configure-nws`,
`/menu:configure-overlays`, `/menu:configure-dx`, `/menu:configure-altnet`, `/port:nema`, `/port:cwop`,
`/menu:configure-messages`. Also G4ILO, "Getting started with APRSISCE/32" (g4ilo.com/aprsisce_gs.html).
