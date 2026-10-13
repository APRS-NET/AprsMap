# APRS.NET gaps

Capabilities AprsMap needs that belong in [APRS.NET](https://github.com/APRS-NET/APRS.NET) under the
[ownership rule](../architecture.md#ownership-rule). Each section is written to become one GitHub issue (title = heading).
API sketches are proposals that follow APRS.NET's existing conventions: parsing never throws, input is bytes and strings
are Latin-1, `TimeProvider` for time, async-first I/O, every public member tracked in `PublicAPI.Unshipped.txt`, and
`AprsNet.Packets` stays I/O-free with BCL-only dependencies.

Baseline: APRS.NET `0.1.0-alpha.8` (2026-10-13).

| ID | Title | Package | AprsMap phase | Issue |
| --- | --- | --- | --- | --- |
| G03 | Resilient APRS-IS connection (reconnect, server rotation) | AprsIsTcpClient | 1 | — |
| G04 | Typed APRS-IS server filter builder | AprsIsTcpClient | 1 | — |
| G05 | Duplicate packet detection | Packets | 1 | — |
| G06 | Third-party (`}`) payload decoding | Packets | 1 | — |
| G07 | Device identification from the aprs-deviceid list | new: AprsNet.DeviceId | 1 | — |
| G08 | Geodesy and Maidenhead helpers | Packets (or new AprsNet.Geo) | 1 | — |
| G09 | Telemetry definitions applied to reports | Packets | 1 | — |
| G20 | Path analysis: RF vs IS origin, heard direct, digipeaters used, q-constructs | Packets | 1 | — |
| G01 | `Callsign.Parse` and a packet encoder | Packets | 2 | — |
| G02 | Transmit through APRS-IS, and passcode generation | AprsIsTcpClient | 2 | — |
| G10 | Messaging session engine (ack, retry, reply-ack, dupes) | new: AprsNet.Messaging | 2 | — |
| G11 | Beacon scheduling algorithms | new: AprsNet.Beaconing | 2 | — |
| G19 | Query and response handling | Packets + Messaging | 2 | — |
| G12 | AX.25 UI frame codec | new: AprsNet.Ax25 | 3 | — |
| G13 | KISS framing and transports (serial, TCP) | new: AprsNet.Kiss | 3 | — |
| G14 | Text-mode and Kenwood TNC support | new: AprsNet.Tnc | 3 | — |
| G15 | Digipeater engine | new: AprsNet.Digipeater | 3 | — |
| G16 | IGate engine | new: AprsNet.IGate | 3 | — |
| G17 | NMEA sentence parsing and `$` packets | Packets | 3 | — |
| G18 | Raw weather station data and storm objects | Packets | 4 | — |
| G21 | APRS-IS server side: client logins, passcode check, filter evaluation | AprsIsTcpClient (or new AprsNet.AprsIsServer) | 3 | — |
| G22 | AGWPE TCP transport | new: AprsNet.Agw | 3 | — |
| G24 | Station track anomaly analysis | Packets (or new AprsNet.Analysis) | 1 | — |

---

## G03: Resilient APRS-IS connection (reconnect, server rotation)

**Need.** AprsMap F1.1. `AprsIsClient` is single-use ("Create a new client to reconnect"). Every long-running consumer
would write the same supervisor loop.

**Scope.** A supervisor (for example `AprsIsConnection`) that owns successive `AprsIsClient` instances: exponential
backoff with jitter, rotation across the resolved addresses of `rotate.aprs2.net` or a list of endpoints, re-applying
the current filter after reconnecting, and a connection state stream (`Connecting`, `LoggedIn(server, verified)`,
`Backoff(until)`, `Faulted`). It should expose one continuous `IAsyncEnumerable<AprsPacket>` across reconnects.

**Acceptance.** Fake transport tests with `FakeTimeProvider`: idle timeout triggers a reconnect, backoff is bounded,
the filter persists across reconnects, and disposal stops the loop promptly.

## G04: Typed APRS-IS server filter builder

**Need.** AprsMap F1.2 builds filters from the map viewport and tracked stations. `SetFilterAsync(string)` takes raw
text.

**Scope.** An immutable `AprsIsFilter` composed of typed parts: range `r/lat/lon/km`, my-range `m/km`, friend range
`f/call/km`, area `a/latN/lonW/latS/lonE`, prefix `p/`, budlist `b/` (with wildcards), object `o/`, type `t/poimqstuw`,
symbol `s/`, digipeater `d/`, entry `e/`, group message `g/`, unproto `u/`, q-construct `q/`, and exclusion `-` for each.
`ToString()` renders the server syntax with invariant culture and sensible coordinate precision. A `TryParse` for
round-tripping user-typed filters is a bonus.

**Spec refs.** aprs-is.net server-side filter documentation.

**Acceptance.** Golden strings for every part; culture-invariance test (for example `de-DE`); exclusions combine.

## G05: Duplicate packet detection

**Need.** AprsMap F1.3; also required by G15 and G16. The same packet arrives via several IGates, and via RF and IS.

**Scope.** A packet identity per the APRS-IS rule (source, destination, information field; path ignored) and a
time-windowed `DuplicateFilter` (default 30 s, `TimeProvider`-driven, bounded memory). Mic-E needs care because the
destination carries data, so it is part of the identity.

**Acceptance.** Same packet with different paths → duplicate; outside the window → not; memory bound holds under a flood
test.

## G06: Third-party (`}`) payload decoding

**Need.** AprsMap F1.3 and F1.6. IS→RF gated traffic and some IGates' traffic shows up as `}`. The design note already
plans an inner `AprsPacket` parsed once without recursion.

**Acceptance.** APRS101 ch. 17 vectors; nesting is limited to one level; the corpus round-trips.

## G07: Device identification from the aprs-deviceid list

**Need.** AprsMap F1.6 shows "what sent this". The Mic-E notes already defer newer Yaesu codes "until the maintained
`aprs-deviceid` list is adopted".

**Scope.** A lookup from TOCALL (`APxxxx` with wildcards) and Mic-E device codes to vendor, model, class (tracker, rig,
software, digi, dstar…), based on the aprs-deviceid YAML, generated into code at build time (no runtime file I/O). Record
the list's version/commit in the package.

**Acceptance.** Generator test against a pinned YAML; wildcard precedence; `MicEReport.Device` uses the same table.

## G08: Geodesy and Maidenhead helpers

**Need.** Distance and bearing (F1.6, F1.7), track smoothing (F1.5), coverage circles (F4.2), filters in km (G04).

**Scope.** Great-circle distance and initial bearing (haversine, plus Vincenty or a documented accuracy limit),
destination point from a bearing and distance, Maidenhead to and from lat/lon (4/6/8/10 characters), the ambiguity
bounding box for an `AprsPosition`, compass point names, and unit conversions (knots, mph, km/h; feet, metres). APRS
radio horizon `sqrt(2*h)` and the PHG range formula from APRS101 p.28.

**Acceptance.** Known reference distances; antimeridian and pole cases; Maidenhead round-trip.

## G09: Telemetry definitions applied to reports

**Need.** AprsMap F1.11 and F4.1. The design note leaves EQNS application "a job for the caller", but every caller needs
the same per-station state.

**Scope.** A `TelemetryProfile` built from the latest `PARM.`/`UNIT.`/`EQNS.`/`BITS.` for a station (including definitions
sent from another callsign addressed to the station), and `Apply(TelemetryReport)` → named, scaled channels with units,
bit names and sense. Pure functions, no storage.

**Acceptance.** APRS101 ch. 13 examples; missing definitions fall back to raw values; partial EQNS.

## G20: Path analysis: RF vs IS origin, heard direct, digipeaters used, q-constructs

**Need.** AprsMap F1.6, F1.9 (RF-only view), F3.3, and the IGate (G16).

**Scope.** From an `AprsPacket` header: the q-construct and the IGate or server that injected it (`qAR`, `qAO`, `qAS`,
`qAC`, `qAX`, `qAU`, `qAo`, `qAI`), whether it was heard on RF by an IGate, the used (`*`) digipeaters in order, whether
the IGate heard it directly (no used hops), remaining WIDEn-N hops, and trace path entries. Return a small
`PathAnalysis` record; never throw on odd paths.

**Spec refs.** aprs-is.net q-construct documentation; APRS 1.1 new-n paradigm.

**Acceptance.** A test per q-construct, from corpus lines.

## G01: `Callsign.Parse` and a packet encoder

**Need.** All transmit features (F2.x, F3.x). Answers the design note's open question about `Callsign.Parse`.

**Scope.** `Callsign.Parse`/`TryParse` (AX.25-valid and APRS-IS forms), plus builders that produce an `AprsPacket` (so
`Raw` stays the source of truth) for: position (uncompressed, compressed, with timestamp/messaging/course-speed/PHG/
altitude/DAO), status, message/ack/rej with message ID and reply-ack, bulletins, objects and items (live and kill),
telemetry and its definitions, weather. Mic-E encoding can come later.

**Acceptance.** Every builder output parses back to an equal payload (property tests); APRS101 vectors encode
byte-for-byte; validation errors are reported rather than producing invalid packets.

## G02: Transmit through APRS-IS, and passcode generation

**Need.** F2.x. `AprsIsClient` has no public send.

**Scope.** `SendAsync(AprsPacket)` that refuses when the login is unverified or `IsReadOnly`; respects the q-construct
rules for client-originated packets; the standard passcode hash as `AprsIsPasscode.Compute(callsign)` (it is public,
widely documented, and needed for setup UX).

**Acceptance.** Unverified login cannot transmit; written lines are exactly the packet bytes plus CRLF.

## G10: Messaging session engine (ack, retry, reply-ack, dupes)

**Need.** F2.4–F2.6, and IGate message gating (G16).

**Scope.** Transport-agnostic: the engine takes incoming `AprsPacket`s and emits packets to send plus events. Outgoing:
message ID allocation (per correspondent, persistent across restarts via a caller-supplied store), retry with a configurable decaying
schedule (the default should avoid resending to APRS-IS inside its 30 s duplicate window, and allow a second round when
the recipient is heard again), stop on ack/rej, timeout to failed, APRS 1.1 reply-ack. Incoming: ack generation, duplicate suppression of
retries, rejects for unsupported content. Bulletins: track by ID and sender, replace on update.

**Acceptance.** `FakeTimeProvider` scenarios: ack after 2 retries, reply-ack piggyback, duplicate retries delivered
once, restart keeps message ID sequence.

## G11: Beacon scheduling algorithms

**Need.** F2.2, F2.3, F2.7.

**Scope.** Pure schedulers that answer "when is the next beacon, and why": fixed interval; corner-pegging speed-based
(SmartBeaconing-style parameters: fast/slow speed and rate, turn angle, turn slope, minimum turn time); decay
(doubling) for objects and new information; dead-reckoning extrapolation from last sent position, course and speed,
and the error between extrapolated and actual position. That last part is what our predictive indicator (F2.3) uses.

**Acceptance.** Simulated GPS tracks with expected beacon times.

## G19: Query and response handling

**Need.** F2.8, and a good-citizen station.

**Scope.** Recognize general (`?APRS?`, `?IGATE?`, `?WX?`) and directed (`?APRSP`, `?APRSS`, `?APRSD`, `?APRSH`,
`?APRST`/`?PING?`, `?APRSM`, `?APRSO`) queries, and helpers that build standard responses from caller-supplied state,
including the random response delay rules for general queries.

**Acceptance.** APRS101 ch. 15 vectors.

## G12: AX.25 UI frame codec

**Need.** F3.1. Already planned in the packet design note ("`AprsNet.Ax25` … `Ax25Frame` … converts to an `AprsPacket`
by rendering the TNC2 line").

**Scope.** Decode and encode UI frames: shifted address fields, SSID, H bit (has-been-repeated), C/R bits, control
0x03, PID 0xF0, up to 8 digipeaters; render to TNC2 and build from an `AprsPacket`. Reject non-UI frames with a reason
(not an exception).

**Acceptance.** Round-trip frame ↔ TNC2 ↔ frame; H bits map to `*`; fuzzing does not throw.

## G13: KISS framing and transports (serial, TCP)

**Need.** F3.1.

**Scope.** KISS FEND/FESC encoding and decoding, port nibble (multi-port TNCs), commands (TXDELAY, P, SlotTime,
TXtail, FullDuplex, SetHardware, Return), and transports behind one interface: serial (System.IO.Ports, which also covers
Bluetooth SPP), TCP KISS (Direwolf, soundmodem; default 8001), with reconnect. AGWPE could be a later separate
transport.

**Acceptance.** Escaping edge cases; split frames across reads; loopback transport tests; Direwolf interop smoke test
(manual).

## G14: Text-mode and Kenwood TNC support

**Need.** F3.2.

**Scope.** Drive TNCs in text (monitor) mode: configurable open and close command scripts with prompt waits, parse
monitor lines (TNC2 and common variants such as Kenwood's) into `AprsPacket`, transmit via the TNC's unproto/beacon
commands, and enter/exit KISS mode sequences for radios such as the TM-D710 and TH-D72/D74.

**Acceptance.** Recorded transcripts for at least two TNC families.

## G15: Digipeater engine

**Need.** F3.5.

**Scope.** Given a received frame, my callsign and aliases, and configuration: decide whether and how to digipeat
(WIDEn-N decrement and own-call insertion, fill-in WIDE1-1 only, own-call and alias digipeating, preemptive digipeat
optional, hop limits/path traps), with duplicate suppression (G05) and per-port viability. Output a transformed frame,
or a reason for not digipeating.

**Spec refs.** APRS 1.1 new-n paradigm; APRS.NET `docs/spec/aprs12/digipeating-and-network.md`.

**Acceptance.** Table-driven tests for each path form.

## G16: IGate engine

**Need.** F3.4.

**Scope.** RF→IS: what not to gate (`TCPIP`, `TCPXX`, `NOGATE`, `RFONLY`, third-party already gated, generic queries),
add `qAR,<igate>`, dupes. IS→RF: gate messages to stations heard directly on RF recently (configurable window and hop
count), along with the sender's position once, wrapped as third-party; rate limiting. Station heard-list API.

**Spec refs.** aprs-is.net IGate documentation; APRS 1.1 "Receive-only IGates break messaging" context.

**Acceptance.** Table tests for each rule; rate limiter under `FakeTimeProvider`.

## G17: NMEA sentence parsing and `$` packets

**Need.** F3.6 (GPS input) and raw NMEA APRS packets (features `nmea.gga`, `nmea.rmc`… currently undecoded).

**Scope.** Checksum validation and parsing of GGA, RMC, GLL, VTG, GSA (and GNSS talker IDs GN/GL/GA/GB), plus
`$`-type packets decoded as a `PositionedPayload` subtype or `NmeaReport`. Reading from devices stays in the app.

**Acceptance.** APRS101 p.34 vectors; bad checksums give a warning, not an exception.

## G18: Raw weather station data and storm objects

**Need.** F4.3, F4.9.

**Scope.** Peet Bros U-II (`#`, `*`) and Ultimeter (`!!`, `$ULTW`) raw formats into `WeatherData`, and storm data on
`\@` objects (type, sustained and peak wind, pressure, radii).

**Acceptance.** Vectors from APRS101 ch. 12 and manufacturer documentation.

## G21: APRS-IS server side: client logins, passcode check, filter evaluation

**Need.** AprsMap F3.10 (re-serve our stream to other apps and instances) and F3.9 (CWOP feeds ignore filters, so we
filter locally). The gateway could also use filter evaluation to give each browser its own view of one shared stream.

**Scope.** (1) **Filter evaluation**: `AprsIsFilter.Matches(AprsPacket, context)`, which needs G04's typed filter and
station-position context for `m/`, `f/` and `b/`. Pure and testable. (2) A minimal **server listener**: accept TCP
clients, parse the `user … pass … vers … filter …` login, verify the passcode (G02), send `# logresp`, apply each
client's filter to the outgoing stream, accept client-originated packets with the right q-construct (`qAC`/`qAX`
rules), and send server keepalive comments.

**Spec refs.** aprs-is.net login, q-construct and filter documentation.

**Acceptance.** A filter test per part type against corpus packets; interop: `AprsIsClient` connects to the listener and
receives a filtered stream.

## G22: AGWPE TCP transport

**Need.** AprsMap F3.8. AGWPE-protocol TCP (default port 8000) is still offered by soundmodem software and Direwolf.

**Scope.** AGW frame header codec, registration, monitoring of UI frames (`U`/`K` frames, raw AX.25 via `k`), sending
unproto/raw frames, multi-port enumeration (`G`). It feeds G12 `Ax25Frame` so packets join the same pipeline as KISS.

**Acceptance.** Recorded sessions; Direwolf AGW interop smoke test (manual).

## G24: Station track anomaly analysis

**Need.** AprsMap F1.20 and the packet-list anomaly flags; useful to any tracker or map.

**Scope.** Given a station's previous position reports and a new one: duplicate position (same position resent after the
dupe window), impossible implied speed (configurable; default far above aircraft speed), sub-second repeats, time going
backwards, and position jumps on ambiguity changes. It returns flags and does not decide what to display. It builds on G05
and G08.

**Acceptance.** Synthetic tracks plus corpus-derived cases (for example digipeated delayed duplicates).
