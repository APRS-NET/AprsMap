// SPDX-License-Identifier: MIT
namespace AprsMap.Tiles;

/// <summary>
/// Whether new tiles may be downloaded now (docs/features/F1.15-quick-toggles.md: the Internet and Map tiles toggles).
/// Without one registered, downloads are always allowed. Cached tiles are served either way.
/// </summary>
public interface ITileDownloads
{
    /// <summary>Whether a request may go to the tile server.</summary>
    bool Allowed { get; }
}

/// <summary>Refuses tile requests while downloads are off, so the cache serves what it has (F1.15 behavior 1).</summary>
internal sealed class TileDownloadGate(ITileDownloads? downloads) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) =>
        downloads is null || downloads.Allowed
            ? base.SendAsync(request, cancellationToken)
            : throw new HttpRequestException("Tile downloads are turned off.");
}
