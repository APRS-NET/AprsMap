// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AprsMap.Tiles;

/// <summary>Tiles from the configured providers, each through its own cache (docs/features/F1.4-map.md, behaviors 9–11).</summary>
public interface ITileService
{
    /// <summary>The providers the viewer may choose from.</summary>
    IReadOnlyList<TileProviderInfo> Providers { get; }

    /// <summary>
    /// Gets one tile. Returns null when the provider isn't configured or the coordinates are outside its zoom range or
    /// the tile grid, so callers (the gateway endpoint) can't be used as an open proxy.
    /// </summary>
    /// <param name="provider">The provider's key.</param>
    /// <param name="z">Zoom level.</param>
    /// <param name="x">Column, from the west.</param>
    /// <param name="y">Row, from the north.</param>
    /// <param name="referer">The viewer's Referer, passed upstream unchanged (the browser's, through the gateway).</param>
    /// <param name="cancellationToken">Stops waiting; a download shared with other callers carries on.</param>
    Task<TileResult?> GetTileAsync(
        string provider,
        int z,
        int x,
        int y,
        string? referer = null,
        CancellationToken cancellationToken = default
    );
}

/// <inheritdoc cref="ITileService" />
public sealed class TileService : ITileService, IDisposable
{
    /// <summary>The <see cref="IHttpClientFactory"/> client name, with the branded User-Agent.</summary>
    public const string HttpClientName = "AprsMap.Tiles";

    private readonly Dictionary<string, (TileProviderOptions Options, TileCache Cache)> _providers;

    /// <summary>Creates the service from configuration.</summary>
    public TileService(
        IOptions<TilesOptions> options,
        IHttpClientFactory http,
        TimeProvider time,
        ILogger<TileService> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        var settings = options.Value;
        var folder = string.IsNullOrWhiteSpace(settings.CacheFolder)
            ? TileLocation.UserFolder(Brand.Slug)
            : settings.CacheFolder;
        _providers = settings.EffectiveProviders.ToDictionary(
            p => p.Key,
            p =>
                (
                    p.Value,
                    new TileCache(
                        p.Value,
                        Path.Combine(folder, p.Key),
                        (long)settings.MaxCacheMegabytes * 1024 * 1024,
                        settings.MaxConcurrentRequests,
                        settings.DefaultLifetime,
                        () => http.CreateClient(HttpClientName),
                        time,
                        logger
                    )
                ),
            StringComparer.Ordinal
        );
        Providers = [.. _providers.Select(p => p.Value.Options.ToInfo(p.Key))];
    }

    /// <inheritdoc />
    public IReadOnlyList<TileProviderInfo> Providers { get; }

    /// <inheritdoc />
    public async Task<TileResult?> GetTileAsync(
        string provider,
        int z,
        int x,
        int y,
        string? referer = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (
            !_providers.TryGetValue(provider, out var entry)
            || z < entry.Options.MinZoom
            || z > entry.Options.MaxZoom
            || x < 0
            || y < 0
            || x >= 1 << z
            || y >= 1 << z
        )
        {
            return null;
        }
        return await entry.Cache.GetAsync(z, x, y, referer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (_, cache) in _providers.Values)
        {
            cache.Dispose();
        }
    }
}
