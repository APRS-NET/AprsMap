// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AprsMap.Core;

/// <summary>Registers the engine with a host.</summary>
public static class EngineServiceCollectionExtensions
{
    /// <summary>
    /// Adds the engine, its <see cref="IEngineService"/> registration and the background service that runs it.
    /// Configure it with <c>services.Configure&lt;EngineOptions&gt;(...)</c> or the <c>Engine</c> config section, and
    /// address lookup with <see cref="GeocodingOptions"/> (the <c>Geocoding</c> section).
    /// </summary>
    public static IServiceCollection AddAprsMapEngine(this IServiceCollection services)
    {
        services.AddOptions<EngineOptions>();
        services.AddOptions<GeocodingOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(
            NominatimGeocoder.HttpClientName,
            (sp, client) => client.Timeout = sp.GetRequiredService<IOptions<GeocodingOptions>>().Value.Timeout
        );
        services.TryAddSingleton<IGeocoder, NominatimGeocoder>();
        services.TryAddSingleton<IPacketLineSourceFactory, AprsIsLineSourceFactory>();
        services.TryAddSingleton<ISettingsStore>(sp =>
            SettingsLocation.CreateStore(sp.GetRequiredService<IOptions<EngineOptions>>().Value)
        );
        services.TryAddSingleton<Engine>();
        services.TryAddSingleton<IEngineService>(sp => sp.GetRequiredService<Engine>());
        services.AddHostedService<EngineHostedService>();
        return services;
    }
}

/// <summary>Runs the engine for the lifetime of the host, unless <see cref="EngineOptions.Enabled"/> is off.</summary>
internal sealed class EngineHostedService(Engine engine, IOptions<EngineOptions> options) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        options.Value.Enabled ? engine.RunAsync(stoppingToken) : Task.CompletedTask;
}
