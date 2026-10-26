// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Net.Http.Json;
using AprsMap.Contracts;

namespace AprsMap.UI.Platform;

/// <summary>
/// A gateway client's map tiles: the gateway's <c>/tiles</c> (F1.4 behavior 10), so the app never talks to a tile
/// server itself: the browser app, from its own origin with its own Referer, which the gateway passes on, and the
/// desktop app as the gateway's client (docs/features/F1.39-desktop-gateway-client.md).
/// </summary>
public sealed class GatewayTileSource(HttpClient http) : ITileSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TileProviderInfo>> GetProvidersAsync(
        CancellationToken cancellationToken = default
    ) =>
        await http.GetFromJsonAsync(
            new Uri("tiles/providers", UriKind.Relative),
            EngineJsonContext.Default.IReadOnlyListTileProviderInfo,
            cancellationToken
        ) ?? [];

    /// <inheritdoc />
    public async Task<byte[]?> GetTileAsync(
        string provider,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken = default
    )
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"tiles/{Uri.EscapeDataString(provider)}/{z}/{x}/{y}");
        try
        {
            using var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken) : null;
        }
        catch (HttpRequestException)
        {
            return null; // the area stays blank; the map keeps working (behavior 12)
        }
    }
}
