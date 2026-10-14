// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AprsMap.Gateway.Tests;

/// <summary>
/// Hosts the gateway in memory with the APRS-IS connection switched off, settings in a temporary folder, and a
/// first-run profile with a passcode (to check that browsers don't see it).
/// </summary>
public class GatewayFactory : WebApplicationFactory<Program>
{
    public const string Callsign = "N0GATE";
    public const int Passcode = 12345;

    public string SettingsFolder { get; } =
        Path.Combine(Path.GetTempPath(), "aprsmap-gateway-" + Guid.NewGuid().ToString("N"));

    protected virtual bool AllowSettingsChanges => false;

    /// <summary>Stands in for Nominatim, so tests never send lookups to the public server.</summary>
    public FakeGeocoder Geocoder { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .ConfigureTestServices(s => s.AddSingleton<IGeocoder>(Geocoder))
            .UseSetting("Geocoding:Endpoint", "https://geo.example/")
            .UseSetting("Engine:Enabled", "false")
            .UseSetting("Engine:SettingsFolder", SettingsFolder)
            .UseSetting("Engine:Callsign", Callsign)
            .UseSetting("Engine:Passcode", Passcode.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("Gateway:AllowSettingsChanges", AllowSettingsChanges ? "true" : "false")
            .UseEnvironment("Testing");

    // TestServer has no real sockets; long polling runs through its in-memory handler. JSON uses only the
    // source-generated metadata, as in the published browser app, so a missing hub type fails here and not in a browser.
    public RemoteEngineService CreateRemote()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(Server.BaseAddress, EngineHub.Path),
                o =>
                {
                    o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                }
            )
            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolver = EngineJsonContext.Default)
            .Build();
        return new RemoteEngineService(connection);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(SettingsFolder))
        {
            Directory.Delete(SettingsFolder, recursive: true);
        }
    }
}

/// <summary>A gateway on a trusted network, where browsers may change settings.</summary>
public sealed class TrustedGatewayFactory : GatewayFactory
{
    protected override bool AllowSettingsChanges => true;
}

/// <summary>Answers lookups from a script and records the queries.</summary>
public sealed class FakeGeocoder : IGeocoder
{
    public const string FailingQuery = "fail";
    public const string FailureMessage = "The address lookup server is refusing requests for now.";

    public GeocodingInfo Info { get; } = new(true, "geo.example", "https://geo.example/policy");

    public System.Collections.Concurrent.ConcurrentQueue<(string Query, string? Language)> Lookups { get; } = new();

    public Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(
        string query,
        string? language,
        CancellationToken cancellationToken = default
    )
    {
        Lookups.Enqueue((query, language));
        return query == FailingQuery
            ? Task.FromException<IReadOnlyList<GeocodeResult>>(new InvalidOperationException(FailureMessage))
            : Task.FromResult<IReadOnlyList<GeocodeResult>>([new($"{query}, Colorado", 40.015, -105.27)]);
    }
}
