# Spike 4: tile cache and gateway tile proxy

Status: done, 2026-10-14. Code: `spikes/TileCache` on branch `spike/tile-cache` (throwaway, never merged; it reuses the spike 1 map).
Question from [architecture.md](../architecture.md#spikes-before-phase-1-each-one-is-a-timeboxed-throwaway): a desktop
disk cache honouring the OSM tile usage policy, and (from spikes 1 and 2) a gateway tile proxy so browsers stop
fetching OSM directly.

**Answer: one small cache library serves both.** The desktop app uses it in-process; the gateway puts it behind a
same-origin `/tiles/osm/{z}/{x}/{y}.png` endpoint. It follows the policy. With it, a warm map makes almost no upstream
requests, revalidating expired tiles moves 83 % fewer bytes, concurrent viewers share downloads, and the map stays
complete offline. Browser panning held 60 fps in every phase.

## The OSM tile usage policy

From <https://operations.osmfoundation.org/policies/tiles/> (accessed 2026-10-14; the page shows no date):

- Send a clear, unique User-Agent that names the app, optionally with a contact URL. Never use a library default, and
  never impersonate a browser.
- Browsers send a valid Referer. Proxies must not strip or blank it.
- Honour Cache-Control, Expires and ETag. If the cache can't read them, cache each tile for at least 7 days. Never
  send no-cache headers by default.
- Revalidate expired tiles with If-None-Match and If-Modified-Since.
- No bulk downloading: only tiles a user is actively viewing. No pre-seeding areas or zoom levels, and no
  "download for offline".
- Show "© OpenStreetMap contributors" visibly.
- HTTPS only; HTTP/2 or HTTP/3 recommended.

What `tile.openstreetmap.org` actually sent (2026-10-14):

- an ETag on every tile, and no Last-Modified;
- `Cache-Control: max-age=…` with an `Age` header (freshness left ranged from hours to days);
- `stale-while-revalidate=604800` and `stale-if-error=604800`, so the server explicitly allows serving a stale tile
  for up to 7 days while revalidating, or when it is unreachable.

## Design under test

`TileCache` (`spikes/TileCache/TileSpike.Cache`):

- **Disk layout:** `{root}/{z}/{x}/{y}.png`, plus a small JSON sidecar holding expiry and ETag.
- **Freshness:** expiry = `max-age − Age`, else `Expires`, else 7 days.
- **Expired tiles:** sent upstream as a conditional request. A 304 renews the expiry and keeps the bytes; a 200
  replaces the tile.
- **Network failures:** an expired tile is served stale; a missing one fails.
- **Merging:** concurrent requests for one tile share a single download.
- **Upstream limits:** at most 4 requests in flight, HTTP/2, the app's own User-Agent, and the caller's Referer passed
  through unchanged.
- **No prefetching:** only tiles the map asks for.

Two ways to use it:

- **Desktop:** in-process, through a BruTile `ILocalTileSource`.
- **Browser:** through the gateway endpoint. It's same-origin, so the browser needs no CORS and no User-Agent of its
  own. The response carries the remaining freshness as `Cache-Control: public, max-age` plus the ETag, and the
  endpoint answers the browser's own conditional requests with 304.

## Results: browser through the gateway proxy

Spike 1's 16 s pan-and-zoom path over the central US, 1280 × 800. Each phase used a fresh headless Firefox (WebGL,
WebDriver BiDi), so the browser's own cache was empty. "Tile ms" is per request as the map's HTTP client sees it.

| Phase | fps | Tile requests | Tile ms p50 / p95 / max | Cache outcomes | Upstream requests / KB |
| --- | --- | --- | --- | --- | --- |
| Cold (empty cache) | 60 | 292 | 16 / 26 / 174 | 292 miss | 288 / 5,350 |
| Warm | 60 | 290 | 14 / 25 / 111 | 282 hit, 5 revalidated, 3 miss | 8 / 8 |
| Revalidate (all 295 tiles marked expired) | 60 | 292 | 16 / 30 / 113 | 248 → 304, 40 replaced, 4 miss | 292 / **901** |
| Offline (expired, upstream failing) | 60 | 287 | 14 / 20 / 55 | 287 stale | 0 / 0 |

- **Cold:** 4 of the 292 requests overlapped with an identical one in flight and were merged. OSM answered in 7 ms
  (median) over HTTP/2, and never more than 4 upstream requests were in flight at once.
- **Warm:** the 5 revalidations were tiles OSM had sent with only a short freshness left.
- **Revalidate:** 85 % of tiles came back 304, and bytes fell from 5.3 MB to 0.9 MB. The 40 replaced tiles had been
  re-rendered upstream.
- **Offline:** the map was complete from stale tiles (screenshot checked), with the attribution showing.
- **Latency:** with OSM this fast, hits and misses look alike from the browser. The proxy's value is upstream load,
  sharing and offline behaviour, not speed. On a slow or metered link it would also be speed.

## Results: many viewers

10 concurrent clients each requested the same 25 uncached zoom-10 tiles through the proxy: **250 requests, 25 upstream
fetches**, with a client median of 9 ms. One gateway serving a club adds no OSM load per extra viewer.

## Results: desktop, in-process

On a copy of the gateway's cache. The screen was locked during the run, so the map rendered about once per second.
Frame rates aren't meaningful; tile latency is.

| Phase | Tile requests | Tile ms p50 / p95 | Outcomes | Upstream |
| --- | --- | --- | --- | --- |
| Revalidate (copied cache was expired) | 162 | 6 / 38 | 124 → 304, 38 replaced | 158 requests, 808 KB |
| Warm | 161 | <1 / 2 | 157 hit | 0 |
| Offline (expired, upstream failing) | 157 | <1 / 1 | 157 stale | 0 |

The disk cache for the whole benchmark path (zoom 5–8 around 300 km, plus the 25-tile test) is 324 tiles, 7.7 MB.

## Problems found

1. **BruTile refuses any HTTP tile request without a User-Agent**, not only OSM's. Even a same-origin proxy URL failed
   until the spike set one and stripped it again (as spike 1 did). In the app, the browser should use its own small
   tile source (an `ILocalTileSource` over `HttpClient`) for the proxy, instead of BruTile's `HttpTileSource`.
2. **zsh doesn't word-split unquoted variables.** A tooling note: the first desktop offline run silently ran online.

## Recommendations

1. **Put the cache in a new MIT project, `AprsMap.Tiles`** (HTTP and disk only; no UI, no BruTile). Use it in-process
   on the desktop and behind the gateway's tile endpoint. Per the repo's rules, use `IHttpClientFactory` and
   `TimeProvider` (the spike used `new HttpClient` and the system clock). The BruTile adapter lives in `AprsMap.UI`.
2. **Gateway: `/tiles/{provider}/{z}/{x}/{y}.png`, same origin.** It replaces spike 1's strip-the-User-Agent hack and
   gives all viewers one cache. Pass the Referer through, keep the app's User-Agent, cap concurrency, merge
   duplicate downloads, and bound the coordinates (as the spike does) so the endpoint isn't an open proxy.
3. **Honour `stale-while-revalidate` and `stale-if-error`.** Serving an expired tile immediately while revalidating in
   the background removes upstream latency from panning. Serving stale on errors should be limited to the window the
   server grants (7 days for OSM). The spike serves stale indefinitely.
4. **Add a size cap with least-recently-used purging** (scope F4.5's "purge policy"). The spike never deletes
   anything.
5. **Keep "offline" honest.** The policy forbids pre-seeding from OSM. Offline use means whatever the user has
   already viewed, within the cache window. Region preloading (F4.5) must use a provider that permits it, with that
   provider's own rules.
6. **Make the User-Agent come from branding** (`Brand.DisplayName`, version, `Brand.SourceUrl`), so a renamed product
   identifies itself correctly.

## Not covered

- Other tile providers and their policies, API keys, and HTTP/3.
- Real slow links (the gateway and OSM's CDN were both fast here), and cache behaviour across process restarts beyond
  what the copied cache showed.
- Purging, size limits, and concurrent writers to one cache folder (two app instances).
