# Spike 3: APRS symbols as a Skia atlas

Status: done, 2026-10-14. Code: `spikes/Symbols` on branch `spike/symbols` (throwaway, never merged; it reuses the spike 1 map). Question
from [architecture.md](../architecture.md#spikes-before-phase-1-each-one-is-a-timeboxed-throwaway): can the
`AprsNet.Symbols` SVGs (both styles, with overlays) be turned into a cached Skia atlas fast enough, and drawn cheaply
enough, on desktop and in the browser?

**Answer: yes, after one fix.** Out of the box, Svg.Skia took 47 ms to convert each symbol on Linux, so 9 s for the
base set, nearly all of it spent in system font lookups. With a cached typeface provider it takes 0.2–0.8 ms. An atlas
drawn with `SKCanvas.DrawAtlas` costs under 1 ms per frame for 2,000 symbols in the browser, which is cheaper than
spike 1's plain circles.

## Setup

- **Symbols:** `AprsSymbolImages.GetSvg(symbol, style)` (AprsNet.Symbols 0.1.0-alpha.9), converted to a Skia picture
  with Svg.Skia 3.2.1 (already a Mapsui dependency), then drawn into one raster image: 32 columns, 1 px padding, at
  the screen's symbol size in device pixels.
- **Symbol mix:** a census of the spike 2 recording (2 minutes of the full APRS-IS feed), kept as aggregate counts in
  `spikes/Symbols/symbol-census.tsv`. It found 5,896 stations with positions and **216 distinct symbols, 89 of them
  with overlays**. The top five: weather station, repeater, car, gateway with a D overlay, and digipeater. Simulated
  stations get symbols weighted by this mix.
- **Benchmark:** spike 1's pan-and-zoom script, 24 DIP symbols, 1280 × 800.
  - Desktop: OpenGL, Release build.
  - Browser: Firefox headless with WebGL, driven through WebDriver BiDi; interpreted .NET; 96 MB GPU budget (from
    spike 1).
- **Layer ms:** the CPU time to issue the station layer's draw calls each frame, measured inside the layer.

**Desktop frame rates were not measurable this run**: the workstation's screen was locked, and the compositor held
frames to 1 per second. Desktop draw costs below come from those (few) frames; browser numbers are from 480 frames per
scenario at a steady 60 fps.

## The font lookup problem

Profiling (dotnet-trace) showed 97 % of the conversion time in `SkiaModel.ToSKTypeface` →
`FontManagerTypefaceProvider.FromFamilyName` → `SKFontManager.GetFontStyles` and `MatchFamily`. Svg.Skia resolves a
typeface through the system font manager (fontconfig on Linux) for every paint, even in SVGs with no text. Parsing the
SVG itself takes 0.05 ms per symbol. The symbols draw overlay characters as outlines (the library's own stroke font),
so no real font is needed.

The fix: clear `SKSvg.Settings.TypefaceProviders` and add a provider that caches `SKTypeface.FromFamilyName` per
family, weight, width and slant. Conversion drops from 48 ms to 0.82 ms per symbol (desktop, cold), about 60× faster.

## Atlas build time

| Set | Symbols | Desktop ms | Browser ms (interpreted) | Atlas KB (24 px / 48 px) |
| --- | --- | --- | --- | --- |
| Base, conventional (first build, includes warm-up) | 188 | 161 | 606 | 507 / 1,875 |
| Base, conventional or modern (warm) | 188 | 40–43 | 240–320 | 507 / 1,875 |
| Overlays seen in the census | 83 | 17 | 129 | 253 at 24 px |
| Every overlay character on every overlay-capable code | 576 | 122 | 873 | 1,521 at 24 px |

Before the fix, the same base set took 8.2–9.3 s on desktop, and every overlay took 30 s.

## Drawing cost

| Scenario | Desktop layer ms | Browser fps | Browser layer ms (mean / p95) |
| --- | --- | --- | --- |
| 2,000 circles (spike 1 baseline) | 1.0 | 60 | 1.4 / 2 |
| 2,000 symbols, `DrawImage` per symbol | 2.2 | 60 | 3.2 / 5 |
| 2,000 symbols, one `DrawAtlas` call | 0.4–1.0 | 60 | **0.8 / 2** |
| 2,000 symbols, vector pictures (no atlas) | 15.8 | **16** | 13.3 / 21 |
| 2,000 symbols, `DrawAtlas`, modern style | 0.4 | 60 | 0.9 / 2 |
| 5,000 symbols, `DrawImage` per symbol | 5.0 | 60 | 6.1 / 8 |
| 5,000 symbols, one `DrawAtlas` call | 0.4 | 60 | **1.9 / 3** |

- **Draw from an atlas, never from vectors per frame.** Vector pictures cost 15–17× more and drop the browser to
  16 fps.
- **`DrawAtlas` scales best.** At 5,000 symbols it costs a third of per-symbol `DrawImage`, because it's one call.
  Both styles cost the same.
- **The visual check passed.** Contact sheets of both styles at 48 px (base set plus census overlays) and the browser
  screenshot show every symbol and overlay character drawn correctly by Svg.Skia. Unassigned codes show the library's
  shared "not" sign, as documented.

## Recommendations

1. **Build the atlas at runtime from AprsNet.Symbols, with the cached typeface provider.** Don't ship pre-rendered
   images: a build costs 40 ms on desktop and about 0.3 s in the browser. It follows the user's symbol size, screen
   scale and style with no asset pipeline, and keeps the drawings in one place (APRS.NET).
2. **Base set up front, overlays on demand.** The 188 base symbols are built at startup (or the first time the map
   shows). An overlay symbol is added the first time a packet uses one (about 1.5 ms each in the browser). The atlas
   can grow by appending cells and taking a new snapshot.
3. **Rebuild the atlas when the symbol size, screen scale or style changes.** It's cheap enough that there's nothing
   to cache across sizes.
4. **Draw stations with one `DrawAtlas` call per frame** in the map's own Skia layer (as spike 1 recommended), sorted
   so selected or tracked stations draw last.
5. **Upstream note for Svg.Skia (optional):** the per-paint system font lookup is a large, easy win for that project.
   It isn't an APRS.NET gap.

## Not covered

- Rotation of directional symbols (cars, aircraft along their course): `SKRotationScaleMatrix` supports it per sprite
  at no extra cost, but it wasn't exercised.
- Labels (spike 1 found them the main remaining cost) and selection highlights.
- AOT browser builds, Chromium and Safari, high-DPI screens (atlas cells scale with the screen; 48 px cells were built
  and timed, but not drawn on a high-DPI screen).
- Desktop frame rates (screen locked during the run).
