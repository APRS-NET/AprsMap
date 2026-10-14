# Spike 1: Mapsui + Avalonia 12 in WebAssembly

Status: done, 2026-10-13 (tile-cost follow-up the same day). Code: `spikes/MapWasm` on branch `spike/map-wasm` (throwaway, never merged). Question from
[architecture.md](../architecture.md#spikes-before-phase-1-each-one-is-a-timeboxed-throwaway): can Mapsui 5.1 on
Avalonia 12 show OSM tiles and about 2,000 moving symbols in the browser with usable pan and zoom?

**Answer: yes, with changes.** Mapsui's core works in WebAssembly, but its Avalonia control does not work on
Avalonia 12. Its symbol layer is also the slowest part of drawing in the browser.

## What broke and how it was worked around

1. **Mapsui.Avalonia 5.1 is built against Avalonia 11.3 and fails on 12.** Every frame throws
   `TypeLoadException: Avalonia.Platform.OptionalFeatureProviderExtensions` inside the render loop, so the window stays
   empty (blank, nothing drawn, not even other controls). The type only moved namespace (`Avalonia.Platform` →
   `Avalonia`), so a Mapsui rebuild for Avalonia 12 would probably fix it. Until one ships, the spike hosts Mapsui's
   UI-agnostic core with its own Avalonia 12 control of about 150 lines (`SpikeMapControl`): a custom draw operation
   that leases the Skia canvas and calls `MapRenderer.Render`, plus drag-to-pan and wheel zoom.
2. **OSM tiles don't load in the browser.** BruTile refuses to request OSM tiles without a `User-Agent` ("Set a
   User-Agent header that is specific to your application"). But a browser fetch that carries one triggers a CORS
   preflight, and `tile.openstreetmap.org` only allows `X-Requested-With`, so every tile fails with `NetworkError`.
   Workaround: set the header for BruTile, then strip it in a `DelegatingHandler` before the browser sends the request.
   The browser's own User-Agent and Referer then identify the app, which the OSM tile policy accepts for web apps.
   Errors show up only on `TileLayer.DataChanged`; Mapsui logs nothing.
3. **Mapsui's fetch machine works in single-threaded WASM.** It queues jobs and runs them; no change was needed.

## Measurements

Scripted benchmark: the map pans in a 300 km circle while zooming between levels 5 and 8, with one viewport change per
frame, over the central US. Each scenario gets 4 s of warm-up (tiles cached), then 8 s of measurement. "Draw" is time
spent inside the map's Skia draw call per frame. A third of the stations move, and all positions update once per
second.

Machine: maintainer's Fedora 44 workstation, 12 cores. Browser: Firefox, WebGL, a 2256 × 1352 canvas at device
scale 1. Desktop: OpenGL, a 1280 × 800 window, Debug build.

### The GPU resource budget (the cause of slow browser tiles)

The first browser runs spent 15 ms per frame on tiles alone, against 0.7 ms on desktop. The cause is Avalonia's Skia
GPU resource cache, which is capped at **28 MB** by default. Once the tiles on screen no longer fit, Skia re-uploads
every tile texture to WebGL on every frame. Mapsui also draws tiles from neighbouring zoom levels while zooming, which
adds to the total. The cost is not linear in canvas size. Tiles only, in the browser:

| Canvas | GPU budget | Tiles draw ms | fps |
| --- | --- | --- | --- |
| 1280 × 800 | 28 MB | 1.6 | 60 |
| 2256 × 1352 | 28 MB | 14.5 | 52 |
| 2256 × 1352 | 64 MB | 3.0 | 59 |
| 2256 × 1352 | 96 MB | 2.6 | 60 |
| 2256 × 1352 | 128 MB | 2.6 | 60 |

The fix is one line: `AppBuilder.With(new SkiaOptions { MaxGpuResourceSizeBytes = … })`. Larger screens (4K, or high
device scale) need a proportionally larger budget, so size it from the screen and don't use a constant.

### All scenarios

Browser columns use the 96 MB budget unless marked. Interpreted and AOT are the two ways .NET can run in WebAssembly.

| Scenario | Desktop draw ms | Browser interpreted, 28 MB: fps / draw ms | Browser AOT, 96 MB: fps / draw ms |
| --- | --- | --- | --- |
| Tiles only | 0.7 | 48 / 15 | 60 / 2.5 |
| 2,000 symbols, Mapsui layer | 5.1 | 25 / 33 | 59 / 9.9 |
| 2,000 symbols + labels, Mapsui layer | 12.0 | 12 / 68 | 22 / 27 |
| 5,000 symbols, Mapsui layer | 9.2 | 13 / 66 | 31 / 22 |
| 2,000 symbols, direct Skia | 2.8 | — | 60 / 4.4 |
| 2,000 symbols + labels, direct Skia | 9.6 | — | 36 / 15 |
| 5,000 symbols, direct Skia | 4.8 | — | 58 / 6.3 |

Desktop holds 60 fps in every scenario. Updating 5,000 positions costs under 1 ms everywhere. The map only redraws when
something changes, so a still map with once-per-second updates costs one frame per second.

Reading the browser numbers:

- **With the right GPU budget, the browser keeps up with the desktop** for tiles and symbols drawn directly.
- **Drawing stations ourselves is much cheaper than Mapsui's symbol layer**: 2 ms against 7 ms for 2,000, and 4 ms
  against 20 ms for 5,000, on top of tiles. The overhead is Mapsui's per-feature pipeline (style resolution, drawable
  caches), not Skia.
- **AOT roughly doubles the speed of managed drawing.** In the 28 MB runs, 2,000 Mapsui symbols went from 33 to 21 ms;
  tiles are native code and don't change. The cost: the download grows from 35 MB to 73 MB (uncompressed
  `_framework`), and the publish takes about 3 minutes instead of seconds.
- **Text labels are now the main cost** (about 10 ms per 2,000 even when drawn directly, plus GPU time: 36 fps). Labels
  need decluttering (only label what fits, and none when zoomed out) and/or cached label bitmaps in an atlas, not
  per-frame text shaping.

## Recommendations

1. **Use Mapsui for the base map only**: tiles, projection, navigation and tile fetching. Draw stations, tracks and
   labels in our own Skia layer. This is faster in both heads and fits the symbol atlas (spike 3: `SKCanvas.DrawAtlas`
   with one sprite sheet). It also keeps APRS-specific drawing (overlays, wind barbs, DF, area objects) out of Mapsui's
   style system.
2. **Own the Avalonia host control** (a small `MapView` in `AprsMap.UI`) instead of depending on Mapsui.Avalonia. If
   Mapsui later ships an Avalonia 12 build, we can reconsider. Mapsui is MIT, which the AGPL UI may use.
3. **Turn on AOT for the browser build in Release** (CI and container). Keep plain Debug builds fast.
4. **Fetch browser tiles through the gateway** rather than from OSM directly. A gateway tile proxy with a disk cache
   sends a proper User-Agent, honours cache headers, removes the CORS and BruTile conflict, and gives all viewers one
   shared cache. Fold this into spike 4 (tile cache).
5. **Raise Avalonia's GPU resource budget** (`SkiaOptions.MaxGpuResourceSizeBytes`) in the browser head, sized from
   the screen area; 96 MB covers a 2256 × 1352 canvas. Check the desktop head's budget on 4K screens too.
6. **Design labels for cost**: declutter, and cache rendered labels. Treat label density as a performance setting.

None of this changes the `IEngineService` shape. The engine sends station positions; the UI owns how they are drawn.

## Not covered

- Real APRS symbols (spike 3) and SignalR throughput (spike 2).
- 4K and high-DPI screens (budget sizing), and whether the desktop head hits the same 28 MB limit there.
- Chromium and Safari; touch input; low-end hardware.
- Feel of hand panning. The spike leaves an interactive map with 2,000 moving stations after the benchmark, so this can
  be tried by hand.
