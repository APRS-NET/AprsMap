// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using AprsMap.UI;
using AprsMap.UI.Localization;
using Avalonia;
using Avalonia.Browser;

namespace AprsMap.Browser;

internal static class Program
{
    /// <summary>How long to wait for the gateway before saying it can't be reached.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>main.js passes the page URL; the gateway that served the page is the one we talk to.</summary>
    private static async Task Main(string[] args)
    {
        var pageUri =
            args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out var uri)
                ? uri
                : new Uri("http://localhost:8080/");
        var gateway = new Uri(pageUri.GetLeftPart(UriPartial.Authority));
        var text = Localizer.Instance;
        var engine = RemoteEngineService.Create(gateway);

        using (var timeout = new CancellationTokenSource(ConnectTimeout))
        {
            try
            {
                await engine.StartAsync(timeout.Token);
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
            await BuildAvaloniaApp(engine).StartBrowserAppAsync("out");
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

    private static AppBuilder BuildAvaloniaApp(RemoteEngineService engine) =>
        AppBuilder.Configure(() => new App(engine)).LogToTrace();
}
