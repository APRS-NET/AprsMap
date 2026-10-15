// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.UI.Platform;

/// <summary>
/// Where the map's tiles come from (docs/features/F1.4-map.md, behavior 10): the desktop app's in-process cache, or the
/// gateway's <c>/tiles</c> in the browser. Tiles are HTTP images, so this is a platform service, not an engine member.
/// </summary>
public interface ITileSource
{
    /// <summary>The providers the viewer may choose from, first the default.</summary>
    Task<IReadOnlyList<TileProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>One tile's image, or null when there is none (unknown, off the grid, or not fetchable now).</summary>
    Task<byte[]?> GetTileAsync(string provider, int z, int x, int y, CancellationToken cancellationToken = default);
}
