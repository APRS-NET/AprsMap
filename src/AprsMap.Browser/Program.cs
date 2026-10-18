// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using AprsMap.UI;
using AprsMap.UI.Appearance;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Shell;
using Avalonia;
using Avalonia.Browser;

namespace AprsMap.Browser;

internal static class Program
{
    /// <summary>How long to wait for the gateway before saying it can't be reached.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How often a browser turned away by a full gateway tries again.</summary>
    private const int BusyRetrySeconds = 30;

    /// <summary>
    /// main.js passes the page URL; the gateway that served the page is the one we talk to, at the page's own path, so
    /// a gateway under a path base (<c>/aprs/</c>) needs no setting (F1.14 behavior 1).
    /// </summary>
    private static async Task Main(string[] args)
    {
        var pageUri =
            args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out var uri)
                ? uri
                : new Uri("http://localhost:8080/");
        var gateway = new Uri(pageUri, "./");
        // The viewer's appearance first, so even a startup error is in their chosen language (F0.6 behavior 19).
        var preferences = new BrowserPreferenceStore();
        var platform = new PlatformServices
        {
            DeviceLocation = new BrowserDeviceLocation(),
            Preferences = preferences,
            Appearance = await new AppearancePreferences(preferences, TimeProvider.System).LoadAsync(),
            SavedLayout = await new LayoutPreferences(preferences, TimeProvider.System).LoadAsync(),
            ManagedWindows = true,
            Tiles = new GatewayTileSource(new HttpClient { BaseAddress = gateway }),
            SharedStation = true,
            MenuStyle = UI.Shell.MenuStyle.Hamburger, // Alt is the browser's (F1.29 behavior 6)
            OperatorSession = new BrowserOperatorSession(new HttpClient { BaseAddress = gateway }),
            Restart = BrowserPreferenceStore.ReloadPage,
            SystemReducedMotion = BrowserPreferenceStore.PrefersReducedMotion(),
            CoarsePointer = BrowserPreferenceStore.IsCoarsePointer(),
#if DEBUG
            ShowPseudoLanguage = true,
#endif
        };
        var text = Localizer.Instance;
        text.SetLanguage(platform.Appearance.Language);
        var engine = RemoteEngineService.Create(gateway);

        using (var timeout = new CancellationTokenSource(ConnectTimeout))
        {
            try
            {
                await engine.StartAsync(timeout.Token);
            }
            catch (EngineAccessException ex) when (ex.EngineError.Code == EngineErrorCodes.GatewayOutdated)
            {
                // A cached page older than the gateway (F1.14 behavior 8): a reload fetches the new one.
                StartupPage.ShowError(
                    text["Startup.Outdated.Title"],
                    text.Format(ex.EngineError),
                    text["Shell.Reload"]
                );
                await engine.DisposeAsync();
                return;
            }
            catch (EngineAccessException ex) when (ex.EngineError.Code == EngineErrorCodes.GatewayBusy)
            {
                // Full (F1.14 behavior 3): say so, and try again every 30 seconds.
                StartupPage.ShowError(
                    text["Startup.Busy.Title"],
                    text.Format(ex.EngineError),
                    text["Startup.Retry"],
                    BusyRetrySeconds
                );
                await engine.DisposeAsync();
                return;
            }
            catch (Exception ex)
                when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                // No gateway here: the page was served by something else (a development server), or the gateway is
                // down. Say so instead of staying on "Loading…".
                StartupPage.ShowError(
                    text.Format(
                        "Startup.NoGateway.Title",
                        new Dictionary<string, object?> { ["product"] = Brand.DisplayName }
                    ),
                    text.Format(
                        "Startup.NoGateway.Detail",
                        new Dictionary<string, object?> { ["address"] = gateway.ToString() }
                    ),
                    text["Startup.Retry"]
                );
                await engine.DisposeAsync();
                return;
            }
        }

        try
        {
            await BuildAvaloniaApp(engine, platform).StartBrowserAppAsync("out");
            StartupPage.AppStarted();
        }
        catch (Exception ex)
            when (ex
                    is DllNotFoundException
                        or EntryPointNotFoundException
                        or TypeInitializationException
                        or InvalidOperationException
            )
        {
            // A build without the WebAssembly graphics libraries (a plain build rather than a publish) can't draw.
            StartupPage.ShowError(
                text["Startup.CantDraw.Title"],
                text["Startup.CantDraw.Detail"] + Environment.NewLine + ex.Message,
                text["Startup.Retry"]
            );
        }
    }

    private static AppBuilder BuildAvaloniaApp(RemoteEngineService engine, PlatformServices platform) =>
        AppBuilder
            .Configure(() => new App(engine, platform))
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = GpuBudget(StartupPage.ScreenPixels()) })
            .LogToTrace();

    /// <summary>
    /// Skia's GPU resource budget, sized from the screen: with Avalonia's 28 MB default, map tiles no longer fit on a
    /// large canvas and are uploaded again every frame (spike 1: 15 ms per frame on 2256 × 1352, 3 ms with 96 MB). About
    /// 32 bytes per screen pixel, between 64 and 512 MB.
    /// </summary>
    internal static long GpuBudget(double screenPixels) =>
        Math.Clamp((long)(screenPixels * 32), 64L * 1024 * 1024, 512L * 1024 * 1024);
}
