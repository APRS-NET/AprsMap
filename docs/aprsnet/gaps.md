# APRS.NET gaps

Capabilities AprsMap needs that belong in [APRS.NET](https://github.com/APRS-NET/APRS.NET) under the
[ownership rule](../architecture.md#ownership-rule). Each section is written to become one GitHub issue (title = heading).
API sketches are proposals that follow APRS.NET's existing conventions: parsing never throws, input is bytes and strings
are Latin-1, `TimeProvider` for time, async-first I/O, every public member tracked in `PublicAPI.Unshipped.txt`, and
`AprsNet.Packets` stays I/O-free with BCL-only dependencies.

Baseline: APRS.NET `0.1.0-alpha.8` (2026-10-13). AprsMap uses `0.2.0-alpha.2` (2026-10-15); what has shipped since the baseline is noted
under **Delivered** in each section. `AprsNet.Filters` and `AprsNet.DeviceId` are on NuGet from `0.2.0-alpha.2`;
`AprsNet.Ax25`, `AprsNet.Kiss`, `AprsNet.Gateway` and `AprsNet.Extensions.DependencyInjection` are in source but not on
NuGet yet (2026-10-15).

Gaps an AprsMap milestone needs become APRS.NET issues, as sub-issues of a `capability` issue in APRS.NET's matching
milestone ([APRS.NET#116](https://github.com/APRS-NET/APRS.NET/issues/116) for AprsMap 0.3.0), labelled
`blocks: AprsMap`, and block the AprsMap feature issues that need them. Phases 2 and 3 are planned (2026-10-16):
AprsMap 0.5.0–0.7.0 match APRS.NET's milestones of the same numbers, and the Issue column below names each gap's
capability issue. G14, G18, G21, G22 and G25 get an issue when their features are planned.

| ID | Title | Package | AprsMap phase | Issue |
| --- | --- | --- | --- | --- |
| G03 | Resilient APRS-IS connection (reconnect, server rotation) (delivered) | AprsIsTcpClient | 1 | — |
| G04 | Typed APRS-IS server filter builder (delivered) | Filters | 1 | [APRS.NET#117](https://github.com/APRS-NET/APRS.NET/issues/117) |
| G05 | Duplicate packet detection (delivered) | Packets | 1 | [APRS.NET#118](https://github.com/APRS-NET/APRS.NET/issues/118) |
| G06 | Third-party (`}`) payload decoding (delivered) | Packets | 1 | — |
| G07 | Device identification from the aprs-deviceid list (delivered) | AprsNet.DeviceId | 1 | [APRS.NET#119](https://github.com/APRS-NET/APRS.NET/issues/119) |
| G08 | Geodesy and Maidenhead helpers (delivered) | Packets | 1 | [APRS.NET#120](https://github.com/APRS-NET/APRS.NET/issues/120) |
| G09 | Telemetry definitions applied to reports (delivered, `0.3.0-alpha.2`: `TelemetryProfile`) | Packets | 1 | [APRS.NET#153](https://github.com/APRS-NET/APRS.NET/issues/153) |
| G20 | Path analysis: RF vs IS origin, heard direct, digipeaters used, q-constructs (delivered) | Packets | 1 | [APRS.NET#121](https://github.com/APRS-NET/APRS.NET/issues/121) |
| G01 | `Callsign.Parse` and a packet encoder (partly delivered) | Packets | 2 | — |
| G02 | Transmit through APRS-IS, and passcode generation (delivered); listen-only mode with a generated listener ID (delivered, `0.4.0-alpha.3`) | AprsIsTcpClient | 2 | [APRS.NET#227](https://github.com/APRS-NET/APRS.NET/issues/227) |
| G10 | Messaging session engine (ack, retry, reply-ack, dupes) (delivered, `0.4.0-alpha.2`; retries reported in `0.4.0-alpha.3`) | new: AprsNet.Messaging | 2 | [APRS.NET#189](https://github.com/APRS-NET/APRS.NET/issues/189) |
| G11 | Beacon scheduling algorithms (fixed and decay delivered, `0.4.0-alpha.3`) | AprsNet.Beacons | 2 | [APRS.NET#195](https://github.com/APRS-NET/APRS.NET/issues/195) |
| G19 | Query and response handling | Packets + Messaging | 2 | [APRS.NET#200](https://github.com/APRS-NET/APRS.NET/issues/200) |
| G12 | AX.25 UI frame codec (delivered) | new: AprsNet.Ax25 | 3 | — |
| G13 | KISS framing and transports (serial, TCP) (TCP delivered; serial published in `0.3.0-alpha.2` as `AprsNet.Kiss.Serial`, unplug and warnings in `0.4.0-alpha.4`, issue open for hardware testing) | new: AprsNet.Kiss | 3 | [APRS.NET#126](https://github.com/APRS-NET/APRS.NET/issues/126), [APRS.NET#160](https://github.com/APRS-NET/APRS.NET/issues/160) |
| G14 | Radios' built-in TNCs: KISS setup (`0.4.0-alpha.6`), typing over an open connection and the APRS-mode reader (`0.4.0-alpha.7`): delivered | Kiss.Serial | 2 | [APRS.NET#249](https://github.com/APRS-NET/APRS.NET/issues/249) |
| G15 | Digipeater engine | new: AprsNet.Digipeater | 3 | [APRS.NET#209](https://github.com/APRS-NET/APRS.NET/issues/209) |
| G16 | IGate engine (RF to APRS-IS delivered, `0.5.0-alpha.1`; APRS-IS to RF open) | new: AprsNet.IGate | 3 | [APRS.NET#206](https://github.com/APRS-NET/APRS.NET/issues/206), [APRS.NET#212](https://github.com/APRS-NET/APRS.NET/issues/212) |
| G17 | NMEA sentence parsing and `$` packets (delivered; GSV, GST and fix quality for F3.12 in `0.5.0-alpha.2`) | Packets | 3 | [APRS.NET#203](https://github.com/APRS-NET/APRS.NET/issues/203) |
| G18 | Raw weather station data and storm objects | Packets | 4 | — |
| G21 | APRS-IS server side: client logins, passcode check, filter evaluation (filters delivered; server in source, not on NuGet) | AprsIsTcpClient (or new AprsNet.AprsIsServer) | 3 | — |
| G22 | AGWPE TCP transport | new: AprsNet.Agw | 3 | — |
| G24 | Station track anomaly analysis (delivered, `0.3.0-alpha.2`: `TrackAnalysis`) | Packets | 1 | [APRS.NET#154](https://github.com/APRS-NET/APRS.NET/issues/154) |
| G25 | Frequency specification parsing and validation | Packets | 3 | — |
| G26 | APRS-IS server presets a UI can list (delivered) | AprsIsTcpClient | 0 | — |
| G27 | Validation errors with stable codes (delivered) | AprsIsTcpClient, Packets | 0 | — |
| G29 | Source merge: which source a packet came from, and duplicates reported with their source (delivered) | Packets | 1 | [APRS.NET#125](https://github.com/APRS-NET/APRS.NET/issues/125) |
| G30 | gpsd client (delivered; SKY reports and error estimates for F3.12 in `0.5.0-alpha.2`) | new: AprsNet.Gps (with G17) | 2 | [APRS.NET#127](https://github.com/APRS-NET/APRS.NET/issues/127), [APRS.NET#203](https://github.com/APRS-NET/APRS.NET/issues/203) |
| G31 | Corpus capture helpers: the anonymizer as a library, the corpus line format, unrecognised parts of a packet (delivered, `0.4.0-alpha.5`: `AprsNet.Corpus`, `AprsPacket.Unrecognized`) | Packets (or a small new package) | 2 | [APRS.NET#236](https://github.com/APRS-NET/APRS.NET/issues/236) |
| G32 | A typed reason when an APRS-IS login reaches a KISS TNC (delivered, `0.4.0-alpha.5`: `KissTncLoginException`) | AprsIsTcpClient | 3 | [APRS.NET#246](https://github.com/APRS-NET/APRS.NET/issues/246) |
| G33 | A traffic event on KISS and APRS-IS connections, for a troubleshooting console (delivered: KISS `0.4.0-alpha.6`, APRS-IS `0.4.0-alpha.7`) | Kiss, AprsIsTcpClient | 2 | [APRS.NET#262](https://github.com/APRS-NET/APRS.NET/issues/262) |
| G34 | Bulletin repeat schedule, and ANSRVR group helpers (delivered: `BulletinSchedule`, `AnsrvrCommand` in `0.4.0-alpha.9`; `AnsrvrMessage`, recognising relays and notices, in `0.7.0-alpha.3`) | Messaging | 2 | [APRS.NET#272](https://github.com/APRS-NET/APRS.NET/issues/272), [APRS.NET#281](https://github.com/APRS-NET/APRS.NET/issues/281) (both closed) |
| G35 | The device list as a loadable object, and a fetcher with an overridable default address (delivered, `0.5.0-alpha.2`) | DeviceId | 2 | [APRS.NET#280](https://github.com/APRS-NET/APRS.NET/issues/280) |
| G36 | Multiline objects: lines and polygons in an object's comment (WXSVR multiline protocol), decode and encode | Packets | 4 | — (not filed) |

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

**Delivered** (`0.1.0-alpha.17`): `AprsIsConnection`: reconnect on close, failure, idle timeout or packet silence, with
backoff and ±20 % jitter (`ReconnectDelay`, `MaxReconnectDelay`); one `ReadLinesAsync` stream across reconnects; the
filter resent on every login (`SetFilterAsync`); `SendAsync` waiting through a reconnect; `StateChanged` events. Rotation
through an endpoint list isn't included, and AprsMap doesn't need it (F1.1 behavior 10). AprsMap's engine runs on it,
reading with `ReadPacketsAsync` (`0.2.0-alpha.1`), and its `// SHIM: G03` loop is gone.

## G04: Typed APRS-IS server filter builder

**Need.** AprsMap F1.2 builds filters from the map viewport and tracked stations. `SetFilterAsync(string)` takes raw
text. AprsMap writes one filter itself, `r/lat/lon/km` or `m/km`, for profiles without a filter
(`AprsIsPacketSourceFactory.EffectiveFilter`).

**Scope.** An immutable `AprsIsFilter` composed of typed parts: range `r/lat/lon/km`, my-range `m/km`, friend range
`f/call/km`, area `a/latN/lonW/latS/lonE`, prefix `p/`, budlist `b/` (with wildcards), object `o/`, type `t/poimqstuw`,
symbol `s/`, digipeater `d/`, entry `e/`, group message `g/`, unproto `u/`, q-construct `q/`, and exclusion `-` for each.
`ToString()` renders the server syntax with invariant culture and sensible coordinate precision. A `TryParse` for
round-tripping user-typed filters is a bonus.

**Spec refs.** aprs-is.net server-side filter documentation.

**Acceptance.** Golden strings for every part; culture-invariance test (for example `de-DE`); exclusions combine.

**Delivered** (`0.2.0-alpha.2`, package `AprsNet.Filters`): `AprsFilterTerm` with a factory per term (`Range`,
`MyRange`, `FriendRange`, `Area`, `Prefix`, `Buddies`, `Objects`, `Types`, `Symbols`, `Digipeaters`, `EntryStations`,
`MessagesTo`, `Destinations`, `QConstructs`) and `Exclude()`, combined by `AprsFilter.Create(terms)`;
`AprsFilter.CheckServerLimits()` reports a filter APRS-IS would refuse (`ServerTermLimit`, 9 range terms). AprsMap's
stand-in filter now uses it.

## G05: Duplicate packet detection

**Need.** AprsMap F1.3; also required by G15 and G16. The same packet arrives via several IGates, and via RF and IS.

**Scope.** A packet identity per the APRS-IS rule (source, destination, information field; path ignored) and a
time-windowed `DuplicateFilter` (default 30 s, `TimeProvider`-driven, bounded memory). Mic-E needs care because the
destination carries data, so it is part of the identity.

**Acceptance.** Same packet with different paths → duplicate; outside the window → not; memory bound holds under a flood
test.

**Delivered** (`0.2.0-alpha.2`): `PacketIdentity.Of(packet)` and `DuplicateFilter` (30 s window from the first copy,
bounded `Capacity`, `TimeProvider`, thread-safe) with `TryAccept`, `IsDuplicate` and `Check(packet, source)`, which
returns a `DuplicateCheck` naming the first copy's source and time.

## G06: Third-party (`}`) payload decoding

**Need.** AprsMap F1.3 and F1.6. IS→RF gated traffic and some IGates' traffic shows up as `}`. The design note already
plans an inner `AprsPacket` parsed once without recursion.

**Acceptance.** APRS101 ch. 17 vectors; nesting is limited to one level; the corpus round-trips.

**Delivered** (`0.2.0-alpha.1`): `ThirdPartyPayload.Packet`, the carried packet parsed as a line of its own (header
with the gateway's path, and information field), with nesting limited by `AprsPacket.MaxThirdPartyDepth` and the
issue codes `InvalidThirdParty` and `ThirdPartyTooDeep`; `AprsEncoder.ThirdParty` builds one.

## G07: Device identification from the aprs-deviceid list

**Need.** AprsMap F1.6 shows "what sent this". The Mic-E notes already defer newer Yaesu codes "until the maintained
`aprs-deviceid` list is adopted".

**Scope.** A lookup from TOCALL (`APxxxx` with wildcards) and Mic-E device codes to vendor, model, class (tracker, rig,
software, digi, dstar…), based on the aprs-deviceid YAML, generated into code at build time (no runtime file I/O). Record
the list's version/commit in the package.

**Acceptance.** Generator test against a pinned YAML; wildcard precedence; `MicEReport.Device` uses the same table.

**Delivered** (`0.2.0-alpha.2`, package `AprsNet.DeviceId`): `AprsDevices.Identify(packet)` and `FromTocall(destination)`
return an `AprsDevice` (vendor, model, class, operating system, features), with the list's `ListCommit` and `ListDate`.
Breaking: `MicEReport.Device` is now an `AprsDevice` rather than a model name (AprsMap didn't use it).

## G08: Geodesy and Maidenhead helpers

**Need.** Maidenhead locator entry in the first-run wizard (F0.5), distance and bearing (F1.6, F1.7), track smoothing
(F1.5), weather units (F1.10), coverage circles (F4.2), filters in km (G04).

**Scope.** Great-circle distance and initial bearing (haversine, plus Vincenty or a documented accuracy limit),
destination point from a bearing and distance, Maidenhead to and from lat/lon (4/6/8/10 characters), the ambiguity
bounding box for an `AprsPosition`, compass point names, and unit conversions (speed: knots, mph, km/h, m/s; length:
feet, metres, miles; temperature: °F, °C; rain: inches, mm; pressure: mbar/hPa, inHg, mmHg). APRS radio horizon
`sqrt(2*h)` and the PHG range formula from APRS101 p.28.

**Acceptance.** Known reference distances; antimeridian and pole cases; Maidenhead round-trip.

**Order.** The Maidenhead part (locator ↔ latitude/longitude, with validation) is needed first, by the F0.5 first-run
wizard, and can ship ahead of the rest of G08.

**Delivered** (`0.1.0-alpha.12`): the Maidenhead part, as `MaidenheadLocator` (`Parse`/`TryParse` with a
`MaidenheadParseError` that names the wrong length or the bad character and its part, `FromPosition`, `Center`,
`SouthWest`/`NorthEast`). The rest (`0.2.0-alpha.2`): `Geodesy` (`DistanceKm`, `InitialBearing`, `Destination`,
`Interpolate`, `PointsAlong`, `CompassPoint`, `RadioHorizonMiles`), `AprsPosition.GetAmbiguityBounds()` returning
`AprsBounds`, and `AprsUnits.Convert` for length, speed, temperature and pressure units.

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

**Delivered** (`0.2.0-alpha.2`): `PathAnalysis.FromPacket`/`FromPath` with `QConstruct`, `EntryStation`,
`IsGatedFromRadio`, `IsFromInternet`, `IsHeardDirectly`, `IsNoGate`, `Digipeaters` and `UsedHops` (in order),
`RemainingHopCount` and the `AprsIsPath` after the q-construct.

## G01: `Callsign.Parse` and a packet encoder

**Need.** All transmit features (F2.x, F3.x). Answers the design note's open question about `Callsign.Parse`.

**Scope.** `Callsign.Parse`/`TryParse` (AX.25-valid and APRS-IS forms), plus builders that produce an `AprsPacket` (so
`Raw` stays the source of truth) for: position (uncompressed, compressed, with timestamp/messaging/course-speed/PHG/
altitude/DAO), status, message/ack/rej with message ID and reply-ack, bulletins, objects and items (live and kill),
telemetry and its definitions, weather. Mic-E encoding (with a chosen Mic-E status code) is needed by AprsMap F2.12
and can follow the first release of the encoder.

**Acceptance.** Every builder output parses back to an equal payload (property tests); APRS101 vectors encode
byte-for-byte; validation errors are reported rather than producing invalid packets.

**Delivered** (by `0.1.0-alpha.12`): `Callsign.Parse`/`TryParse`, and `AprsPacket.Create` with builders for position,
status and message payloads. Still open: the other payload builders and Mic-E encoding.

**Next: an address for outgoing packets.** An app transmits as one station: the same source, destination (its TOCALL)
and usually the same path on every packet, so every `AprsPacket.Create` call repeats them. Add a small immutable record
that holds the TNC2 address part (source `Callsign`, destination `Callsign`, default path) and has
`Create(AprsPayload)` and `Create(AprsPayload, path)`, both calling `AprsPacket.Create`. It is not a fluent builder and
adds no second model: payloads stay the parser's records, so round-trip tests and `with` edits keep working. It is
validated when it is made (a source that can transmit, a destination that is a valid TOCALL, valid path entries), and
`with { Path = … }` gives an RF or an APRS-IS variant. Whatever transmits (G02 send, the G13 KISS transports, G11
beacons, G10 messaging) takes the packets it makes. Name it from APRS.NET's own conventions, not after Packet.NET's API
([architecture.md, alternatives considered](../architecture.md#alternatives-considered)). Mic-E puts the latitude in
the destination, so for a Mic-E payload `Create` replaces the TOCALL; that lands with Mic-E encoding.

**Delivered** (`0.1.0-alpha.13`): `PacketAddress` (`Source`, `Destination`, `Path`, `Create(payload)`,
`Create(payload, path)`, `Parse`). `0.1.0-alpha.14` added builders for objects, items and Mic-E reports.

**Acceptance (address).** `Create(payload)` equals `AprsPacket.Create` with the same parts; an invalid source, TOCALL
or path entry is refused when the record is made, not on each packet; a path given to `Create` replaces the default for
that packet only.

## G02: Transmit through APRS-IS, and passcode generation

**Need.** F2.x. `AprsIsClient` has no public send.

**Scope.** `SendAsync(AprsPacket)` that refuses when the login is unverified or `IsReadOnly`; respects the q-construct
rules for client-originated packets; the standard passcode hash as `AprsIsPasscode.Compute(callsign)` (it is public,
widely documented, and needed for setup UX).

**Acceptance.** Unverified login cannot transmit; written lines are exactly the packet bytes plus CRLF.

**Delivered**: `AprsIsPasscode.Compute(callsign)` (`0.1.0-alpha.10`), and sending on a verified login,
`AprsIsClient.SendAsync` (`0.1.0-alpha.16`).

**Delivered** (`0.4.0-alpha.3`, APRS.NET#242): `AprsIsLogin.NewListenerId`, `AprsIsLogin.IsListenerId` and
`AprsIsClientOptions.ListenOnly`, which AprsMap uses; the shim is gone. Asked for (2026-10-17): a **listen-only mode** for unlicensed users (F2.1 behavior 4): a generated listener ID, `SWL` and
six random digits (nine characters, never an amateur callsign), with passcode -1 forced, `SendAsync` refusing locally
with a clear reason, and a way to tell a listener ID from a callsign ([APRS.NET#227](https://github.com/APRS-NET/APRS.NET/issues/227)); until it
shipped, AprsMap made the ID itself behind `// SHIM: G02`.

## G10: Messaging session engine (ack, retry, reply-ack, dupes)

**Need.** F2.4–F2.6, and IGate message gating (G16).

**Scope.** Transport-agnostic: the engine takes incoming `AprsPacket`s and emits packets to send plus events. Outgoing:
message ID allocation (per correspondent, persistent across restarts via a caller-supplied store), retry with a configurable decaying
schedule (the default should avoid resending to APRS-IS inside its 30 s duplicate window, and allow a second round when
the recipient is heard again), stop on ack/rej, timeout to failed, APRS 1.1 reply-ack. Incoming: ack generation, duplicate suppression of
retries, rejects for unsupported content. Bulletins: track by ID and sender, replace on update.

**Acceptance.** `FakeTimeProvider` scenarios: ack after 2 retries, reply-ack piggyback, duplicate retries delivered
once, restart keeps message ID sequence.

**Delivered** (`0.4.0-alpha.2`, package `AprsNet.Messaging`, APRS.NET#189): `MessagingStation` with outgoing retries,
acks, rejects, reply-acks and duplicate suppression; `0.4.0-alpha.3` adds `OutgoingMessage.Attempted` for each retry.
AprsMap's messaging (F2.4) is built on it; bulletins (F2.5), the email and QRU helpers (F2.6) and auto-reply (F2.10) are no longer blocked.

## G11: Beacon scheduling algorithms

**Need.** F2.2, F2.3, F2.7.

**Scope.** Pure schedulers that answer "when is the next beacon, and why": fixed interval; corner-pegging speed-based
(SmartBeaconing-style parameters: fast/slow speed and rate, turn angle, turn slope, minimum turn time); decay
(doubling) for objects and new information; dead-reckoning extrapolation from last sent position, course and speed,
and the error between extrapolated and actual position. That last part is what our predictive indicator (F2.3) uses.

**Acceptance.** Simulated GPS tracks with expected beacon times.

**Delivered** (`0.4.0-alpha.3`, package `AprsNet.Beacons`): `FixedBeaconSchedule` (with a random first wait) and
`DecayBeaconSchedule`, each answering `Next` (when and why), `Sent()` and `Changed()`; F2.2 uses both. Corner pegging
(APRS.NET#197) and dead reckoning are still to come.

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

**Delivered** (`0.2.0-alpha.1` in source, on NuGet as `AprsNet.Ax25` from `0.2.0-alpha.3`): decode and encode AX.25 UI
frames.

## G13: KISS framing and transports (serial, TCP)

**Need.** F3.1.

**Scope.** KISS FEND/FESC encoding and decoding, port nibble (multi-port TNCs), commands (TXDELAY, P, SlotTime,
TXtail, FullDuplex, SetHardware, Return), and transports behind one interface: serial (System.IO.Ports, which also covers
Bluetooth SPP), TCP KISS (Direwolf, soundmodem; default 8001), with reconnect. AGWPE could be a later separate
transport.

**Acceptance.** Escaping edge cases; split frames across reads; loopback transport tests; Direwolf interop smoke test
(manual).

**Delivered** (`0.2.0-alpha.1` in source, on NuGet as `AprsNet.Kiss` from `0.2.0-alpha.3`): KISS framing, KISS over
any stream (serial and Bluetooth), and a TCP TNC connection that reconnects, configured with `KissTcpOptions`, that
reports `StateChanged` and metrics as APRS-IS does, and limits the transmit rate. `KissTcpServer` shares one TNC with
several KISS apps. Still open, needed by AprsMap 0.4.0 (F1.25):
a serial connection that reopens the port after a USB unplug and replug (as `KissTcpConnection` reconnects), with the
connection states F1.1 uses, and a list of the serial ports present. ([APRS.NET#126](https://github.com/APRS-NET/APRS.NET/issues/126))
`0.4.0-alpha.4` takes an unplugged serial TNC for a lost link (tested with a TH-D72A on USB serial) and adds
`KissConnection.Warning`: a TNC sending text instead of KISS frames, and, with `QuietWarning` set, one that has sent
nothing for that long. AprsMap shows both on the source's pill (F3.1 behavior 13).

## G14: Radios' built-in TNCs: KISS setup, and reading a radio in APRS mode

**Need.** F3.2 (AprsMap 0.5.0, [#242](https://github.com/APRS-NET/AprsMap/issues/242)): a radio's built-in TNC as a
source, starting with the Kenwood TH-D72A.

**Scope** ([APRS.NET#249](https://github.com/APRS-NET/APRS.NET/issues/249), rescoped 2026-10-18, part of #160):

1. **KISS mode** (APRS.NET#257, #258, merged): `TncScript`, `TncSetup` with `TncSetup.KenwoodThD72` built in, and
   `KissSerialOptions.Setup`. The connection switches the radio into KISS each time it opens the port, again when the
   radio sends text after a power cut, and out of KISS when it closes. A failed setup reconnects with an
   `IOException` carrying a hint ("Switch the radio's TNC to packet mode (PACKET12) first"). `TncSetup.Known` and
   `TncSetup.Find` feed a TNC type picker; a custom TNC is a `TncSetup` with its own scripts. `TncTerminal` is a console
   for setting a TNC up by hand, opened only while no connection holds the port.
2. **A radio in APRS mode, receive only**: a serial reader for the TNC-2 text lines the radio prints, with no setup; the
   radio keeps working as an APRS station. NMEA sentences and damaged lines are skipped and counted. Same reconnects,
   port handling, DTR/RTS and quiet warning as KISS over serial. Nothing can be sent this way.

3. **Typing to the TNC over an open connection** (depends on #262's unframed traffic): `BeginManual()` returns an
   `IDisposable`; inside it, `SendRawAsync` and `SendLineAsync` write to the open port under the frame lock (they throw
   outside it), the automatic setup re-run is held off (the `TextNotKiss` warning still fires), and `RunScriptAsync`
   runs a script without a reopen, its expects matching the unframed bytes. At a TNC-2 prompt some commands transmit
   (`ID`, `CONV`, `K`), and in KISS mode a raw data frame goes on the air, so AprsMap's console says so. A raw line on
   APRS-IS (`#filter`) is lower priority.

Not planned: switching the radio's TNC mode from the computer, sending through a TNC's own commands (sending uses KISS),
and a second TNC family's transcript (no other radio on hand).

**Acceptance.** Checked on the maintainer's TH-D72A: KISS 1.7 s after opening, back in KISS after a power cycle with no
one stepping in, out of KISS on close. A recorded APRS-mode transcript, GPS sentences replaced, parses into the expected
packets with the cut lines dropped.

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

Added 2026-10-20 for AprsMap F3.12 (GPS status): **GSV** (satellites in view: PRN, elevation, azimuth, SNR, across a
multi-sentence group, per talker so GPS, GLONASS, Galileo and BeiDou stay apart, with the NMEA 4.1 signal ID where
sent), **GST** (estimated position error in metres, where the receiver sends it), and the DOPs and satellites used from
GGA and GSA, gathered into one fix-quality value next to the position fix shared with G30. **Delivered** in `0.5.0-alpha.2`
(`GpsQuality`, `GpsSatellite`, `GnssSystem` from `NmeaFixTracker`).

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

**Delivered**: local evaluation of filter expressions (`AprsFilter`, package `AprsNet.Filters`, on NuGet from
`0.2.0-alpha.2`). In source but not on NuGet yet: a gateway (`AprsNet.Gateway`) that serves packets to APRS-IS clients with their own filters,
de-duplicates, replays recent history on login and tracks where stations were heard for `m/` and `f/`.

## G22: AGWPE TCP transport

**Need.** AprsMap F3.8. AGWPE-protocol TCP (default port 8000) is still offered by soundmodem software and Direwolf.

**Scope.** AGW frame header codec, registration, monitoring of UI frames (`U`/`K` frames, raw AX.25 via `k`), sending
unproto/raw frames, multi-port enumeration (`G`). It feeds G12 `Ax25Frame` so packets join the same pipeline as KISS.

**Acceptance.** Recorded sessions; Direwolf AGW interop smoke test (manual).

Tracked as [APRS.NET#224](https://github.com/APRS-NET/APRS.NET/issues/224) (the connection is #225), milestone 0.8.0-alpha.1;
AprsMap's side is [#384](https://github.com/APRS-NET/AprsMap/issues/384).

## G24: Station track anomaly analysis

**Need.** AprsMap F1.20 and the packet-list anomaly flags; useful to any tracker or map.

**Scope.** Given a station's previous position reports and a new one: duplicate position (same position resent after the
dupe window), impossible implied speed (configurable; default far above aircraft speed), sub-second repeats, time going
backwards, and position jumps on ambiguity changes. It returns flags and does not decide what to display. It builds on G05
and G08.

**Acceptance.** Synthetic tracks plus corpus-derived cases (for example digipeated delayed duplicates).

## G25: Frequency specification parsing and validation

**Need.** AprsMap F3.11 (frequency monitor) and F3.7 (frequency objects). The reference app counts frequency objects
and flags "frequency objects with issues", so validation is part of the need, not only decoding.

**Scope.** Decode the APRS 1.2 frequency spec in comments and object names: `FFF.FFFMHz`, tone `Tnnn`/`tnnn`, DCS
`Dnnn`, offset `+nnn`/`-nnn`, range `Rnnm`/`Rnnk`, the frequency-object naming convention (object name is the
frequency), and the AFRS conventions. Return a `FrequencySpec` with typed parts. Malformed input gets specific
`ParseIssue` warnings (bad case, offset not in 10 kHz units, unknown tone) rather than being silently dropped.

**Spec refs.** APRS.NET `docs/spec/aprs12/frequency-afrs.md`; APRS 1.2 frequency spec.

**Acceptance.** Vectors from the spec; corpus repeater objects; each warning code has a test.

## G26: APRS-IS server presets a UI can list

**Need.** AprsMap F0.5 (first-run wizard, server step) and the connection settings: offer the worldwide and regional
aprs2.net rotates by name instead of hard-coding host names in the app.

**Scope.** `AprsIsEndpoint` already has the presets (`Rotate`, `NorthAmerica`, `SouthAmerica`, `Europe`, `Asia`,
`Oceania`). Add an enumerable list of them, each with a short English region name (for example "Worldwide", "North
America", "Europe and Africa") and a stable key, in a fixed order with the worldwide rotate first. Optionally, a lookup
from a host string back to its preset.

**Spec refs.** <https://www.aprs2.net/> (regional rotate names; port 14580 for user-defined filters).

**Acceptance.** The list matches the existing preset properties one to one; keys are stable; worldwide first.

**Delivered** (`0.1.0-alpha.12`): `AprsIsServerPreset.All` (`Key`, `Name`, `Endpoint`) and `TryFind` by key, host string
or endpoint.

## G27: Validation errors with stable codes

**Need.** AprsMap F0.9 (ready for translation). The app shows APRS.NET's validation errors to users in their own
language, and on the web the error is raised on the gateway, far from the viewer. An English message can't be
translated reliably, and matching on its text breaks whenever the wording changes.

**Scope.** Wherever APRS.NET throws `ArgumentException` for input a user typed, also expose a stable code and the
offending value: `AprsIsClientOptions.Validate` (callsign, software name and version, filter), `AprsIsPasscode.Compute`
(base callsign), `Callsign.Parse`, `PathEntry.Parse` and `AprsIsEndpoint` parsing (host, port). For example, an
`AprsNetArgumentException : ArgumentException` with `Code` (a string or enum such as `CallsignInvalidLength`,
`CallsignInvalidCharacter`, `FilterControlCharacter`, `ServerPortOutOfRange`), plus `ParamName` and the offending
value or position where useful. The English message stays as it is. Alternatively, `TryValidate` methods could return
an error object, like `MaidenheadLocator.TryParse` does with `MaidenheadParseError`. Codes are documented and never
renamed.

**Acceptance.** Every user-input `ArgumentException` the listed members throw has a code; a test lists them and checks
that each code is unique and documented; existing catch blocks for `ArgumentException` keep working.

**Delivered** (`0.1.0-alpha.15`): `AprsValidationException : ArgumentException` with `Code`
(`AprsValidationErrorCode`, 14 codes), `Value`, `Index` and `Character`. Thrown by `Callsign.Parse`, `PathEntry.Parse`,
`PacketAddress.Parse`, `AprsIsClientOptions.Validate`, `SetFilterAsync`, `AprsIsPasscode.Compute` and the
`AprsIsEndpoint` constructor, and by the new `AprsIsEndpoint.Parse(string)`.

## G29: Source merge with provenance

**Need.** AprsMap F1.25: packets come from several sources (APRS-IS, KISS TNCs). Each packet records its source, and a
packet heard again on another source within the duplicate window shows once with a **Seen on** list. APRS.NET plans a
source abstraction inside `AprsNet.Gateway` (APRS.NET #106) that de-duplicates and counts per source; AprsMap needs the
same, public and outside the gateway.

**Scope.** A source id carried with each packet from any reader (`AprsIsConnection`, `KissTcpConnection`, the serial
connection), and G05's `DuplicateFilter` returning, for a duplicate, the first packet's identity and the new source,
rather than only dropping it. A small merge type that reads any number of sources and yields `(packet, source,
isDuplicateOf)` would serve both AprsMap and `AprsNet.Gateway`.

**Acceptance.** One fake APRS-IS and one fake KISS source: a packet on both comes out once as new and once as a
duplicate naming the second source; outside the window both are new.

**Delivered** (`0.2.0-alpha.2`): `PacketMerger` takes any number of named sources (`Add(source, packets)`,
`RemoveAsync`) and yields `MergedPacket(Packet, Source, ReceivedAt, DuplicateOf)`, where `DuplicateOf` names the first
copy's source and time.

## G30: gpsd client

**Need.** AprsMap F1.25 behavior 12 (GPS sources, planned with beaconing, F2.2): a GPS shared by gpsd, locally or on
another machine.

**Scope.** Connect to gpsd over TCP (default port 2947), send `?WATCH={"enable":true,"json":true}`, and read TPV
reports (time, position, speed, course, altitude, fix mode) into a position fix type shared with G17's NMEA parser;
reconnect like the other connections. No device access in the library beyond the socket.

Added 2026-10-20 for AprsMap F3.12 (GPS status): TPV's error estimates (`eph`, `epv`, `epx`, `epy`, `eps`), and
**SKY** reports (the satellites in view with PRN, elevation, azimuth, signal strength, used or not and constellation
(`gnssid`), and the DOPs) into the same satellite and fix-quality values as G17's GSV and GSA. The raw lines read, for
an app's console. **Delivered** in `0.5.0-alpha.2` (`GpsdConnection.Quality` and `Satellites`).

**Acceptance.** Recorded gpsd JSON sessions; no fix, 2D and 3D fixes; reconnect after the daemon restarts.

## G31: Corpus capture helpers

**Need.** AprsMap F1.27 (0.5.0): the app records packets it couldn't make sense of and exports them for
`tests/corpus`, so what the corpus accepts has to be produced by the library, not re-implemented in the app.

**Scope.**

1. **The anonymizer as an API**: what `tools/corpus/Anonymize.cs` does today (same-length Q-prefixed callsigns,
   X-names, masked e-mail addresses; positions, TOCALLs, Mic-E destinations, generic path aliases and server names kept
   byte for byte; every line the same length), over lines in memory, with the pseudonym map passed in and returned so
   later exports reuse it. The script becomes a thin wrapper over it.
2. **The corpus line format**: write and read `<UTC timestamp> <raw line>` in Latin-1, so a caller can't get the
   timestamp format or the encoding wrong.
3. **Unrecognised parts of a decoded packet**: a list on `AprsPacket` of what parsed but wasn't understood (an unknown
   data extension, an unknown path element, data left after the known comment fields, a `{` that isn't a known
   user-defined format), each with a stable code and its span, next to `Issues`. Parsing still never throws.

**Acceptance.** The anonymizer API's output for the existing captures matches the script's byte for byte; a Mic-E line
that isn't valid UTF-8 writes and reads back unchanged; each unrecognised-part code has a corpus line that shows it.

**Delivered** (`0.4.0-alpha.5`, APRS.NET#250, #251): `AprsNet.Corpus` (`CorpusLine`, `CorpusFile`, `CorpusWriter`,
`CorpusAnonymizer`, `CorpusPseudonyms`) and `AprsPacket.Unrecognized` with four `UnrecognizedPartCode`s. AprsMap's
packet capture (F1.27) is built on them.

## G32: A typed reason when an APRS-IS login reaches a KISS TNC

**Need.** AprsMap F1.1: since `0.4.0-alpha.4` a login that gets KISS frames back (an APRS-IS source given Dire Wolf's
KISS port by mistake) fails at once with a `LoginException`, instead of waiting out the login timeout. The app can only
tell that case from any other unreachable server by the exception's English message, so it says "Server unreachable"
rather than "That's a KISS TNC: add it as a TNC source".

**Scope.** A way to tell the case apart without reading the message: a `LoginException` subclass (say
`KissTncLoginException`) or a reason property on `LoginException`.

**Acceptance.** The existing test for a login that reaches a KISS TNC asserts the type or the reason, not the message.

**Filed** as [APRS.NET#246](https://github.com/APRS-NET/APRS.NET/issues/246) for `0.5.0-alpha.1`: a `LoginException`
subclass, as `LoginRejectedException` is. AprsMap waits for it rather than matching the message.

## G33: A traffic event for a troubleshooting console

**Need.** AprsMap F1.28 Source console (0.5.0, [#246](https://github.com/APRS-NET/AprsMap/issues/246)): a source's live
traffic, mostly what never becomes a packet. `ReadPacketsAsync` and `ReadFramesAsync` each have one reader and yield only
good frames, so the app can't watch them without taking packets away, and what's dropped is only counted. Why something
was dropped is protocol knowledge, so it belongs in the library.

**Scope** ([APRS.NET#262](https://github.com/APRS-NET/APRS.NET/issues/262), two PRs, KISS first):

- **KISS:** `event EventHandler<KissTrafficEventArgs>? Traffic` on `KissStreamClient`, `KissTcpClient` and
  `KissConnection` (kept across reconnects). Each entry has `At`, `Direction`, `Kind`, a copy of the `Bytes`, `Port`,
  `Command`, and `Packet` when it decoded as APRS. `Kind` is a stable enum with `Describe()`: `Frame`, `NotAprs`,
  `OtherPort`, `Command`, `DroppedFrame` (with why), `Unframed` (lines split at CR/LF, with `LooksLikeText`), `Sent`,
  and `SetupSent`, `SetupReceived`, `SetupFailed` for a `TncSetup` script's steps.
- **APRS-IS:** its own `AprsIsTrafficEventArgs` on `AprsIsClient` and `AprsIsConnection`, one entry per line in or out
  (login with the passcode masked by the library, login response, server comments and keepalives, packets, `#filter`
  commands, sent packets).
- **Behaviour:** no work without a subscriber; entries come on the reading, sending or opening thread, so a UI must
  marshal; a handler that throws doesn't break reading; no history, so the engine subscribes when a source starts and
  keeps its own ring buffer.

**Acceptance.** A fake TNC raises one entry of each kind, text as lines; a sent packet raises `Sent`; setup steps raise
`Setup*`, a failed expect `SetupFailed`; the APRS-IS login never carries the passcode; nothing extra is allocated with
no subscriber; a throwing handler doesn't stop reading.

## G34: Bulletin repeat schedule and ANSRVR group helpers

**Need.** AprsMap F2.5 (0.5.0, [#193](https://github.com/APRS-NET/AprsMap/issues/193)): posting bulletins and
announcements, and joining announcement groups through ANSRVR.

**Scope** ([APRS.NET#272](https://github.com/APRS-NET/APRS.NET/issues/272), APRS.NET 0.5.0).

1. **A bulletin repeat schedule**: when to send a bulletin or announcement again (often at first, then less often) and
   for how long, as `AprsNet.Beacons` answers for beacons, following APRS101's guidance on bulletin repetition.
2. **ANSRVR helpers**: the join, leave, post and keep-alive messages to ANSRVR for a group, and recognising a message
   ANSRVR relays from a group (with the group and the original sender), so apps don't hard-code its forms.

**Acceptance.** A schedule's send times over a bulletin's life; each ANSRVR message form round-trips; a relayed group
message from a recorded transcript is recognised with its group and sender.

## G35: A device list that can be loaded and fetched

**Need.** AprsMap F4.16 (0.6.0): an install that isn't updated should still learn new devices. The aprs-deviceid list
reaches APRS.NET within a day (the nightly `deviceid` workflow), but only as each release's built-in table, so an app
can't use a newer list without copying the generator's format and rules.

**Scope** ([APRS.NET#280](https://github.com/APRS-NET/APRS.NET/issues/280), APRS.NET 0.6.0).

1. **`AprsDeviceList`**: the built-in list (`AprsDeviceList.BuiltIn`), or one read from the published JSON
   (`Parse(Stream)` and `TryParse`, with the reasons it can't, under the generator's rules: classes it knows, TOCALL
   patterns, Mic-E codes), with `GeneratedAt` (`meta.generation_time`), `Commit` when known, and the lookups
   `Identify(AprsPacket)` and `FromTocall(string)`. `AprsDevices` keeps working on `BuiltIn`.
2. **`AprsDeviceListSource`**: fetches the list from an address, `DefaultUrl` being the APRS Foundation's
   (`https://aprs-deviceid.aprsfoundation.org/tocalls.pretty.json`) and any other a mirror's, with an injected
   `HttpClient`; sends `If-None-Match` and answers unchanged, a new list with its `ETag`, or a typed failure.
3. **`IsNewerThan(other)`** by `GeneratedAt`, so every app decides "newer" the same way.

The schedule, where a downloaded copy is kept and which list an app uses stay with the app. Agreed with the maintainer (2026-10-19): a list object apps pass around, with
`AprsDevices` left on the built-in list, not a static switched for the whole process.

**Acceptance.** `BuiltIn` answers as `AprsDevices` does; the published file round-trips to the same lookups as the
generated table; a file with an unknown class, a bad pattern or no TOCALLs is refused with a reason; against a fake
server, a `304` is unchanged, a `200` gives a list and its `ETag`, and a mirror's address is used when given.

## G36: Multiline objects

**Need.** AprsMap F2.7 (objects and items: multiline objects, deferred) and showing received ones on the map.

**Scope.** Decoding and encoding the multiline extension an object's comment can carry: a line or closed polygon of up
to several vertices as offsets from the object's position, with its colour and line style. It's specified outside
APRS101 (the WXSVR multiline protocol, referenced by the APRS 1.1 errata A11-ERR-26 and not mirrored in APRS.NET's
spec folder), so the first step is getting that page into `docs/spec` with its source.

**Acceptance.** Round trips of lines and polygons; received examples (WXSVR warning areas) decode to their vertices.


## G37: GPS quality metrics

**Need.** AprsMap F3.14 (GPS statistics on the dashboard): the maintainer wants a GPS's health watched from a
monitoring system over OTLP, as `aprsnet.kiss.nmea.*` already does for NMEA on a TNC port. The numbers belong to each
GPS connection, so any app using the library gets them; AprsMap keeps only what's its own (the GPS *in use* among
several, its dashboard history and wander).

**Scope.** Metrics on the GPS meter, per connection (`GpsdConnection`, and serial or TCP NMEA read through
`NmeaFixTracker` where the library owns the connection), from the fix and quality the library already works out
(G17, G30):

- the time in each fix state (counter, seconds; 3D, 2D, no fix) and fix losses (counter);
- satellites used and in view (gauges), tagged by constellation;
- the dilutions (gauge), tagged PDOP, HDOP, VDOP, TDOP, GDOP;
- the estimated errors (gauge, metres), tagged horizontal, vertical, latitude, longitude, with the confidence the source
  gives (gpsd 95%, GST one sigma) said in the description or a tag;
- the average signal of the satellites used (gauge, dB-Hz).

Tags identify the connection the way the library's other metrics do (no positions). Names and units are the library's
choice; the above is what AprsMap would read.

Tracked as [APRS.NET#328](https://github.com/APRS-NET/APRS.NET/issues/328).

**Acceptance.** A `MeterListener` over recorded gpsd and NMEA sessions sees each metric, including a fix lost and
regained, and a constellation with satellites in view but none used.
