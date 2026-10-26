// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Tiles;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AprsMap.Server;

/// <summary>
/// What the desktop's web viewer is to listen on and show (docs/features/F1.35-desktop-web-viewer.md, behaviors 2–5).
/// </summary>
/// <param name="Port">The TCP port.</param>
/// <param name="LocalNetwork">
/// True to listen on every network the computer is on, so other devices can connect; false for this computer only.
/// </param>
/// <param name="WebRoot">The browser app's files, bundled with the desktop app.</param>
/// <param name="HomePrivacy">What viewers are shown of the station's own location.</param>
public sealed record WebViewerOptions(int Port, bool LocalNetwork, string WebRoot, HomePrivacy HomePrivacy);

/// <summary>
/// The desktop app's web viewer (docs/features/F1.35-desktop-web-viewer.md): the gateway's hub, tiles and browser app,
/// served from inside the desktop app around its own engine, view only. There is no sign-in: every browser is a
/// viewer, so nothing it sends changes the station or transmits (behavior 6), by the same rules the gateway applies to
/// viewers.
/// </summary>
public sealed class WebViewer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Viewers _viewers;

    private WebViewer(WebApplication app, IReadOnlyList<Uri> addresses)
    {
        _app = app;
        _viewers = app.Services.GetRequiredService<Viewers>();
        Addresses = addresses;
    }

    /// <summary>Where it listens, as the server bound it.</summary>
    public IReadOnlyList<Uri> Addresses { get; }

    /// <summary>How many browsers are connected now.</summary>
    public int ViewerCount => _viewers.Count;

    /// <summary>
    /// Starts the viewer around the desktop's engine and tiles, taken from <paramref name="host"/>; fails as Kestrel
    /// does when the port is taken (<see cref="IOException"/>).
    /// </summary>
    public static async Task<WebViewer> StartAsync(
        WebViewerOptions options,
        IServiceProvider host,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        var builder = WebApplication.CreateEmptyBuilder(
            new WebApplicationOptions
            {
                ApplicationName = typeof(WebViewer).Assembly.GetName().Name,
                ContentRootPath = options.WebRoot,
                WebRootPath = options.WebRoot,
            }
        );
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (options.LocalNetwork)
            {
                kestrel.ListenAnyIP(options.Port);
            }
            else
            {
                kestrel.ListenLocalhost(options.Port);
            }
        });
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);

        // The desktop's own engine, tiles, clock and logging: one station, shown to browsers.
        var services = builder.Services;
        services.AddSingleton(host.GetRequiredService<IEngineService>());
        services.AddSingleton(host.GetRequiredService<Core.Engine>());
        services.AddSingleton(host.GetRequiredService<IOptions<Core.EngineOptions>>()); // the desktop's: never Service
        services.AddSingleton(host.GetRequiredService<ITileService>());
        services.AddSingleton(host.GetService<TimeProvider>() ?? TimeProvider.System);
        services.AddSingleton(host.GetRequiredService<ILoggerFactory>());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddRouting();
        services.Configure<GatewayOptions>(o =>
        {
            o.AllowSettingsChanges = false; // view only (behavior 6)
            o.HomePrivacy = options.HomePrivacy;
            o.MaxViewers = 20;
        });

        // No sign-in (F1.36 is later): with no session nobody is an operator, so every call is a viewer's.
        services.AddSingleton(sp => new OperatorStore(
            Path.Combine(options.WebRoot, "no-operators"),
            sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>()
        ));
        services.AddSingleton<OperatorSessions>();
        services.AddSingleton<SignInAudit>();
        // Nor an API key (F1.38 is the gateway's): an empty folder, so no key is ever accepted.
        services.AddSingleton(sp => new ApiKeyStore(
            Path.Combine(options.WebRoot, "no-api-key"),
            sp.GetRequiredService<TimeProvider>()
        ));
        services.AddSingleton<SignInLimiter>();
        services.AddSingleton<ApiKeyGate>();
        services.AddSingleton<OperatorFilter>();
        services.AddSingleton<HomeShields>();
        services.AddSingleton<Viewers>();
        services.AddSingleton<ViewerFilter>();
        services
            .AddSignalR(o =>
            {
                o.AddFilter<ViewerFilter>();
                o.AddFilter<OperatorFilter>();
                o.MaximumReceiveMessageSize = 64 * 1024;
            })
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, EngineJsonContext.Default)
            );

        var app = builder.Build();
        app.UseGatewayFront(new GatewayOptions());
        app.UseViewerGate();
        app.UseBrowserApp();
        app.UseRouting();
        app.MapHub<EngineHubEndpoint>(EngineHub.Path);
        app.MapTiles();
        // AGPL-3.0 section 13: users interacting over a network are offered the corresponding source.
        app.MapGet("/source", () => Results.Redirect(Brand.SourceUrl));
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        return new WebViewer(app, AddressesFor(options));
    }

    /// <summary>
    /// The addresses to open (behavior 4): this computer's, and with the local network, each network address the
    /// computer has (IPv4, interfaces that are up and not loopback), as other devices reach it.
    /// </summary>
    internal static IReadOnlyList<Uri> AddressesFor(WebViewerOptions options)
    {
        var port = options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<Uri> addresses = [new($"http://localhost:{port}/")];
        if (options.LocalNetwork)
        {
            addresses.AddRange(
                System
                    .Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n =>
                        n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    )
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Distinct()
                    .Select(a => new Uri($"http://{a}:{port}/"))
            );
        }
        return addresses;
    }

    /// <summary>
    /// Stops listening; connected browsers are told the station went away and try again. It runs off the caller's
    /// context: the server's own stop resumes on it, and a desktop app closing calls this from a UI thread whose
    /// dispatcher has stopped, where it would wait for ever.
    /// </summary>
    public async ValueTask DisposeAsync() =>
        await Task.Run(async () =>
            {
                // Browsers still connected get a moment to close, then they're cut off: the server would otherwise wait
                // up to 30 seconds for them, and the app with it.
                using var grace = new CancellationTokenSource(StopGrace);
                await _app.StopAsync(grace.Token).ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
            })
            .ConfigureAwait(false);

    /// <summary>How long connected browsers get to close as the viewer stops.</summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);
}
