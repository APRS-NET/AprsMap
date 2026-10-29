// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using BruTile;
using BruTile.Predefined;

namespace AprsMap.UI.Map;

/// <summary>
/// One provider's tiles for Mapsui's tile layer, read through the app's tile source (the in-process cache
/// or the gateway; <see cref="Platform.ITileSource"/>), never straight from the provider: so the browser makes no cross-origin requests and every tile goes
/// through the cache that follows the provider's policy (spike 4).
/// </summary>
internal sealed class ProviderTileSource(Platform.ITileSource tiles, TileProviderInfo provider) : ILocalTileSource
{
    public ITileSchema Schema { get; } = new GlobalSphericalMercator(provider.MinZoom, provider.MaxZoom);

    public string Name => provider.Name;

    /// <summary>
    /// None for Mapsui to draw: the map panel shows the provider's attribution in its own bar (F1.4), and Mapsui's copy
    /// showed through it in the browser, a second time in another font.
    /// </summary>
    public Attribution Attribution { get; } = new(string.Empty, string.Empty);

    public Task<byte[]?> GetTileAsync(TileInfo tileInfo)
    {
        ArgumentNullException.ThrowIfNull(tileInfo);
        var index = tileInfo.Index;
        return tiles.GetTileAsync(provider.Key, index.Level, index.Col, index.Row);
    }
}
