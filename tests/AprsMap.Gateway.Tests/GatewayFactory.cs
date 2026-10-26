// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.AspNetCore.Builder;
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

    /// <summary>Stands in for the tile server, so tests never fetch from OpenStreetMap.</summary>
    public FakeTileServer TileServer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .ConfigureTestServices(s =>
            {
                s.AddSingleton<IGeocoder>(Geocoder);
                s.AddHttpClient(Tiles.TileService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => TileServer);
            })
            .UseSetting("Tiles:CacheFolder", Path.Combine(SettingsFolder, "tiles"))
            .UseSetting("Geocoding:Endpoint", "https://geo.example/")
            .UseSetting("Engine:Enabled", "false")
            .UseSetting("Engine:SettingsFolder", SettingsFolder)
            .UseSetting("Engine:Callsign", Callsign)
            .UseSetting("Engine:Passcode", Passcode.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("Gateway:AllowSettingsChanges", AllowSettingsChanges ? "true" : "false")
            .UseEnvironment("Testing");

    // TestServer has no real sockets; long polling runs through its in-memory handler. JSON uses only the
    // source-generated metadata, as in the published browser app, so a missing hub type fails here and not in a browser.
    public RemoteEngineService CreateRemote(
        string? cookie = null,
        string? origin = null,
        int? localPort = null,
        string? apiKey = null,
        bool https = false
    )
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                RemoteEngineService.HubUri(https ? new Uri("https://localhost/") : Server.BaseAddress),
                o =>
                {
                    o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                    if (cookie is not null)
                    {
                        o.Headers["Cookie"] = cookie; // a signed-in operator's session (F2.9)
                    }
                    if (origin is not null)
                    {
                        o.Headers["Origin"] = origin;
                    }
                    if (apiKey is not null)
                    {
                        o.Headers["Authorization"] = "Bearer " + apiKey; // an app with the API key (F1.38)
                    }
                    if (localPort is { } port)
                    {
                        o.Headers[LocalPortFilter.Header] = port.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        );
                    }
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

/// <summary>A tile server that answers every request with the same small PNG, slowly, and records what it was sent.</summary>
public sealed class FakeTileServer : HttpMessageHandler
{
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    public System.Collections.Concurrent.ConcurrentQueue<string?> Referers { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _requests);
        Referers.Enqueue(request.Headers.Referrer?.ToString());
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, cancellationToken);
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]),
        };
        response.Headers.CacheControl = System.Net.Http.Headers.CacheControlHeaderValue.Parse("max-age=3600");
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"t1\"");
        return response;
    }
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
            ? Task.FromException<IReadOnlyList<GeocodeResult>>(
                EngineErrors.Create(EngineErrorCodes.GeocodingRefused, FailureMessage)
            )
            : Task.FromResult<IReadOnlyList<GeocodeResult>>([new($"{query}, Colorado", 40.015, -105.27)]);
    }
}

/// <summary>
/// The test server has no sockets, so no local port: a request's <see cref="Header"/> stands in for the port it
/// arrived on (F1.26's operator port). Test-only.
/// </summary>
public sealed class LocalPortFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public const string Header = "X-Test-Local-Port";

    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
        Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next
    ) =>
        app =>
        {
            app.Use(
                (context, then) =>
                {
                    if (int.TryParse(context.Request.Headers[Header], out var port))
                    {
                        context.Connection.LocalPort = port;
                    }
                    return then(context);
                }
            );
            next(app);
        };
}
