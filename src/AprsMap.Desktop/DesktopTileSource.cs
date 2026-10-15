// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Tiles;
using AprsMap.UI.Platform;

namespace AprsMap.Desktop;

/// <summary>The desktop map's tiles: the tile cache in process (F1.4 behavior 10).</summary>
internal sealed class DesktopTileSource(ITileService tiles) : ITileSource
{
    public Task<IReadOnlyList<TileProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tiles.Providers);

    public async Task<byte[]?> GetTileAsync(
        string provider,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken = default
    ) =>
        (await tiles.GetTileAsync(provider, z, x, y, cancellationToken: cancellationToken).ConfigureAwait(false))?.Data;
}
