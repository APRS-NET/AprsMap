// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.UI;
using Avalonia;
using Avalonia.Browser;

namespace AprsMap.Browser;

internal static class Program
{
    /// <summary>main.js passes the page URL; the gateway that served the page is the one we talk to.</summary>
    private static async Task Main(string[] args)
    {
        var pageUri =
            args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out var uri)
                ? uri
                : new Uri("http://localhost:8080/");
        var engine = RemoteEngineService.Create(new Uri(pageUri.GetLeftPart(UriPartial.Authority)));
        await engine.StartAsync();
        await BuildAvaloniaApp(engine).StartBrowserAppAsync("out");
    }

    private static AppBuilder BuildAvaloniaApp(RemoteEngineService engine) =>
        AppBuilder.Configure(() => new App(engine)).LogToTrace();
}
