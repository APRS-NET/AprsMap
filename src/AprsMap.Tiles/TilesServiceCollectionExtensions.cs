// SPDX-License-Identifier: MIT
using System.Net;
using AprsMap.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AprsMap.Tiles;

/// <summary>Registers the tile service with a host.</summary>
public static class TilesServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="ITileService"/>, configured from the <c>Tiles</c> section (<see cref="TilesOptions"/>), with an
    /// HTTP client that identifies the app as tile policies require: its name, version and source link, never a library
    /// default (from branding, so a renamed product identifies itself correctly).
    /// </summary>
    public static IServiceCollection AddAprsMapTiles(this IServiceCollection services)
    {
        services.AddOptions<TilesOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services
            .AddHttpClient(
                TileService.HttpClientName,
                client =>
                {
                    client.DefaultRequestVersion = HttpVersion.Version20;
                    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                    client.Timeout = TimeSpan.FromSeconds(30);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent());
                }
            )
            .AddHttpMessageHandler(provider => new TileDownloadGate(provider.GetService<ITileDownloads>()));
        services.TryAddSingleton<ITileService, TileService>();
        return services;
    }

    /// <summary>For example <c>AprsMap/0.3.0 (+https://github.com/APRS-NET/AprsMap)</c>.</summary>
    internal static string UserAgent() =>
        $"{Product(Brand.DisplayName)}/{Product(Brand.Version.Split('+')[0])} (+{Brand.SourceUrl})";

    private static string Product(string value)
    {
        var token = string.Concat(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
        return token.Length == 0 ? "unknown" : token;
    }
}
