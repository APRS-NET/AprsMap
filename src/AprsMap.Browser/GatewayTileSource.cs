// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Net.Http.Json;
using AprsMap.Contracts;
using AprsMap.UI.Platform;

namespace AprsMap.Browser;

/// <summary>
/// The browser map's tiles: the gateway's same-origin <c>/tiles</c> (F1.4 behavior 10), so the browser never talks to a
/// tile server itself. The browser sends its own Referer, which the gateway passes on.
/// </summary>
internal sealed class GatewayTileSource(HttpClient http) : ITileSource
{
    public async Task<IReadOnlyList<TileProviderInfo>> GetProvidersAsync(
        CancellationToken cancellationToken = default
    ) =>
        await http.GetFromJsonAsync(
            new Uri("tiles/providers", UriKind.Relative),
            EngineJsonContext.Default.IReadOnlyListTileProviderInfo,
            cancellationToken
        ) ?? [];

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
