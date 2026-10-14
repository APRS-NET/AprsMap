# Spike 2: SignalR streaming into the WebAssembly app

Status: done, 2026-10-14. Code: `spikes/SignalRStream` on branch `spike/signalr-stream` (throwaway, never merged; the browser client reuses
the spike 1 map). Question from [architecture.md](../architecture.md#spikes-before-phase-1-each-one-is-a-timeboxed-throwaway):
can the gateway stream a busy feed into the browser app, and what shape should the stream take?

**Answer: yes, easily.** The browser keeps up with 20× the full worldwide APRS-IS feed while the map pans, and the
gateway serves 100 viewers on a fraction of one core. The problem found: **JSON over SignalR fails in a published
WebAssembly build** unless the wire types have source-generated metadata, which the real app lacked (fixed in PR #4).

## Setup

- **Feed:** two minutes of the full APRS-IS feed (port 10152, receive-only login), recorded 2026-10-14. Rate: **88
  packets/s average, 152/s peak**, 8.7 KB/s of raw text, 6,569 distinct stations. The recording is not committed (it
  contains third parties' traffic). The spike gateway replays it at 1×, 5× or 20× through the real `AprsMap.Core`
  `Engine` (`Accept` and per-client `WatchPacketsAsync` fan-out), so parsing and fan-out are the app's own code.
- **Stream shapes:**
  - *packets*: one `PacketView` per message, the hub's shape today;
  - *batches*: `PacketView[]` every 250 ms;
  - *stations*: a compact `StationUpdate` (call, lat, lon, symbol, time) every 250 ms, with only the latest update
    per station.
- **Wire formats:** SignalR JSON and MessagePack.
- **Browser client:** spike 1's map (OSM tiles, stations drawn directly with Skia, 96 MB GPU budget), interpreted
  (not AOT), Firefox, same machine as the gateway. It pans continuously while measuring, as in spike 1.
- **Load client:** a console app with N concurrent viewers, to measure the gateway's fan-out cost.

## Results: one browser viewer

8 s windows after 3 s of warm-up. Latency runs from the engine receiving the packet to the browser handling it.
"Loss" compares packets received with packets the engine accepted in the window. Its ±1.5 % is window-alignment noise;
the engine dropped nothing at these rates.

| Shape | Format | Rate | Packets/s | Latency p50/p95 ms | fps | Wire KB/s | Gateway CPU % (1 core) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| packets | JSON | 1× | 95 | 4 / 11 | 60 | 36 | 4 |
| packets | JSON | 20× | 1,742 | 27 / 54 | 52 | 664 | 14 |
| packets | MessagePack | 1× | 97 | 3 / 11 | 60 | 28 | 4 |
| packets | MessagePack | 20× | 1,757 | 38 / 60 | 52 | 512 | 11 |
| batches | JSON | 20× | 1,748 | 170 / 280 | 56 | 597 | 9 |
| batches | MessagePack | 20× | 1,762 | 145 / 256 | 56 | 495 | 10 |
| stations | JSON | 1× | 66 updates | 153 / 274 | 60 | 9 | 4 |
| stations | MessagePack | 1× | 57 updates | 162 / 296 | 57 | **5** | 3 |
| stations | MessagePack | 20× | 1,236 updates | 149 / 267 | 56 | 109 | 9 |

At 5×, every shape holds 57–60 fps.

- **Throughput is not a constraint.** The real full feed is about 90 packets/s; the browser handles 20 times that.
- **Per-packet messages cost frame rate at high rates** (52 fps at 20×). Batching recovers some of it (56 fps) and
  adds about 150 ms of latency, which a map can't show anyway.
- **The station shape is the cheapest by far:** at real rate, 5 KB/s against 28–36 KB/s per packet. `PacketView`
  carries the raw packet and summary text, about 4× the raw feed. Coalescing also cuts updates at 20× by 30 %.
- **MessagePack is 20–40 % smaller than JSON and works in WASM** (interpreted; AOT not tested).

## Results: late-joiner snapshot

`GetSnapshot` returns N stations as `StationUpdate[]` (real stations padded with jittered copies).

| Stations | JSON: round trip ms / KB | MessagePack: round trip ms / KB | Apply to map ms |
| --- | --- | --- | --- |
| 5,000 | 260 / 702 | 172 / 449 | 2–10 |
| 20,000 | 424 / 2,939 | 127 / 1,835 | 13–25 |
| 40,000 | 711 / 5,922 | 224 / 3,688 | 28–35 |

A gateway that has heard the whole world for hours (tens of thousands of stations) can catch a new browser up in well
under a second. A viewport-limited snapshot would be far smaller.

## Results: many viewers (gateway fan-out)

Gateway CPU as % of one core, including the replay and parsing. The load client ran on the same machine.

| Viewers | packets, JSON 1× | packets, JSON 5× | stations, MessagePack 1× | stations, MessagePack 5× |
| --- | --- | --- | --- | --- |
| 1 | 4 | 8 | 5 | 7 |
| 10 | 6 | 11 | 5 | 8 |
| 50 | 8 | 25 | 7 | 16 |
| 100 | 11 | 37 | 10 | 28 |

Allocation stays under 6 MB/s. One gateway serving a club or a family is far below any limit.

## Problems found

1. **SignalR's JSON protocol fails in a published WebAssembly build.** Trimmed browser builds turn off reflection-based
   System.Text.Json, so the first hub call throws `InvalidOperationException: JsonSerializerIsReflectionDisabled`. The
   fix is a source-generated `JsonSerializerContext` listing every type that crosses the hub (including hub arguments'
   runtime types, such as `int` and `double`), inserted with `AddJsonProtocol(o =>
   o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, Context.Default))`. The real web app had the same bug
   (stuck at "Starting…"). **Fixed in PR #4**: `EngineJsonContext` in `AprsMap.Contracts`, used by the gateway and
   `RemoteEngineService`, with tests that fail when a hub type lacks metadata and a gateway test whose client runs
   with reflection off.
2. **The replay has to read the recording as Latin-1.** Reading it as UTF-8 made `AprsPacket.Parse` throw on a
   non-Latin-1 character, as APRS.NET's contract says it will. The live APRS.NET client already reads bytes, so this
   affects only test tooling. But note that `Engine.Accept` lets that exception escape, which would stop a host that
   feeds it lines from elsewhere.

## Recommendations

1. **Fix the web app's JSON first** (a small, separate change, not part of this spike). Done in PR #4.
2. **Stream station changes in batches, not raw packets, to the map.** Add `WatchStationsAsync(viewport)` to
   `IEngineService`, yielding coalesced, compact changes every ~250 ms. This matches the architecture draft's
   `StationChange` stream and late-join snapshot. Keep the per-packet stream (with raw text) for the raw packet scroller
   only, where it's opt-in.
3. **Use MessagePack between gateway and browser.** It's smaller and faster to decode, and it worked unchanged here.
   Keep JSON available for debugging. *Reversed in F0.2:* the app's trimmed browser build treats trim warnings as
   errors, and SignalR's MessagePack protocol is marked as unsupported for trimming and native AOT. The spike's build
   didn't check. The app stays on source-generated JSON ([architecture.md](../architecture.md)).
4. **Viewport-limit the station stream and snapshot later, if needed.** The numbers say it isn't needed for
   throughput; it is for privacy and tidiness on a public gateway.
5. **Make `Engine.Accept` survive bad input** (catch and count per line) before anything other than the APRS.NET client
   feeds it.

None of this needs APRS.NET changes.

## Not covered

- AOT browser builds with MessagePack (AOT can't use runtime code generation; MessagePack may need its source
  generator there).
- Real network links (latency, a phone on mobile data), Chromium and Safari.
- Slow viewers. The engine's per-client channel drops the oldest packets when full. That's harmless for the stations
  shape (the next update corrects it) but loses lines in a raw-packet view.
