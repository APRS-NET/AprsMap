// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Tiles;
using Microsoft.Net.Http.Headers;

namespace AprsMap.Gateway;

/// <summary>
/// The browser's map tiles (docs/features/F1.4-map.md, behavior 10): same origin, so no CORS, and one cache shared by
/// every viewer. Only configured providers and coordinates on their grid are served, so it isn't an open proxy. The
/// viewer's Referer goes upstream unchanged; the gateway's own User-Agent identifies the app.
/// </summary>
internal static class TileEndpoints
{
    /// <summary>The providers' path.</summary>
    public const string ProvidersPath = "/tiles/providers";

    public static void MapTiles(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            ProvidersPath,
            (ITileService tiles) =>
                Results.Json(tiles.Providers, EngineJsonContext.Default.IReadOnlyListTileProviderInfo)
        );
        app.MapGet("/tiles/{provider}/{z:int}/{x:int}/{y:int}", GetTileAsync);
    }

    private static async Task<IResult> GetTileAsync(
        string provider,
        int z,
        int x,
        int y,
        HttpContext context,
        ITileService tiles,
        TimeProvider time
    )
    {
        var referer = context.Request.Headers.Referer.FirstOrDefault();
        var tile = await tiles.GetTileAsync(provider, z, x, y, referer, context.RequestAborted).ConfigureAwait(false);
        if (tile is null)
        {
            return Results.NotFound();
        }
        if (tile.Data is null)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        var headers = context.Response.GetTypedHeaders();
        var remaining = tile.Expires - time.GetUtcNow();
        headers.CacheControl = new CacheControlHeaderValue
        {
            Public = true,
            MaxAge = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
        };
        if (tile.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            headers.ETag = tag;
            if (context.Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Compare(tag, useStrongComparison: false)))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
        }
        var type = tiles.Providers.First(p => p.Key == provider).ImageType;
        return Results.Bytes(tile.Data, type);
    }
}
