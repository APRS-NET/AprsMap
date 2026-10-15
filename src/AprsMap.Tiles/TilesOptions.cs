// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Tiles;

/// <summary>Tile settings, bound from the <c>Tiles</c> configuration section (docs/features/F1.4-map.md, behaviors 9–11).</summary>
public sealed class TilesOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Tiles";

    /// <summary>The providers, by key. With none configured, OpenStreetMap's standard tiles are offered as <c>osm</c>.</summary>
    public Dictionary<string, TileProviderOptions> Providers { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Where tiles are cached; empty means the user's cache folder (<see cref="TileLocation"/>).</summary>
    public string? CacheFolder { get; set; }

    /// <summary>The most each provider's cache may hold before the least recently used tiles go (behavior 9.6).</summary>
    public int MaxCacheMegabytes { get; set; } = 512;

    /// <summary>Upstream requests in flight at once, per provider (behavior 9.4).</summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>How long a tile stays fresh when the server sends no caching headers (the OSM policy's minimum).</summary>
    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The providers in effect: the configured ones, or OpenStreetMap when none are.</summary>
    public IReadOnlyDictionary<string, TileProviderOptions> EffectiveProviders =>
        Providers.Count > 0
            ? Providers
            : new Dictionary<string, TileProviderOptions> { [OpenStreetMap.Key] = OpenStreetMap.Options };

    /// <summary>OpenStreetMap's standard tiles, the default provider (behavior 8).</summary>
    public static class OpenStreetMap
    {
        /// <summary>Its key.</summary>
        public const string Key = "osm";

        /// <summary>Its settings.</summary>
        public static TileProviderOptions Options { get; } =
            new()
            {
                Name = "OpenStreetMap",
                UrlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
                Attribution = "© OpenStreetMap contributors",
                AttributionUrl = "https://www.openstreetmap.org/copyright",
                MinZoom = 0,
                MaxZoom = 19,
                ImageType = "image/png",
            };
    }
}

/// <summary>One tile provider's settings (behavior 11).</summary>
public sealed class TileProviderOptions
{
    /// <summary>What the viewer sees.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The tile URL with <c>{z}</c>, <c>{x}</c> and <c>{y}</c>, or <c>{-y}</c> for an inverted (TMS) row.</summary>
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>The attribution the provider requires.</summary>
    public string Attribution { get; set; } = string.Empty;

    /// <summary>Where the attribution links to.</summary>
    public string? AttributionUrl { get; set; }

    /// <summary>The lowest zoom level served.</summary>
    public int MinZoom { get; set; }

    /// <summary>The highest zoom level served.</summary>
    public int MaxZoom { get; set; } = 19;

    /// <summary>The tiles' media type.</summary>
    public string ImageType { get; set; } = "image/png";

    /// <summary>The public part, for the UI.</summary>
    public TileProviderInfo ToInfo(string key) =>
        new(key, Name, Attribution, AttributionUrl, MinZoom, MaxZoom, ImageType);

    /// <summary>The URL of one tile.</summary>
    public Uri TileUri(int z, int x, int y) =>
        new(
            UrlTemplate
                .Replace("{z}", z.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace(
                    "{-y}",
                    ((1 << z) - 1 - y).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal
                )
                .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        );
}

/// <summary>The user's cache folder for tiles on each operating system (behavior 10).</summary>
public static class TileLocation
{
    /// <summary>The current user's tile cache folder for the product <paramref name="slug"/>.</summary>
    public static string UserFolder(string slug)
    {
        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }
        return Path.Combine(root, "aprsnet", slug, "tiles");
    }
}
