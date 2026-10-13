// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Hosting;
using AprsMap.UI;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AprsMap.Desktop;

internal static class Program
{
    private static IHost? s_host;

    [STAThread]
    public static int Main(string[] args)
    {
        // Settings live next to the executable, wherever it was started from.
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory }
        );
        builder.AddAprsMapEngine().AddAprsMapTelemetry("desktop");
        s_host = builder.Build();
        s_host.Start();
        // Ctrl+C or SIGTERM stops the host; close the window too rather than leave a UI with no engine behind it.
        s_host
            .Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(() =>
                Dispatcher.UIThread.Post(() =>
                    (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()
                )
            );
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Graceful engine shutdown when the window closes; the UI thread has nothing left to do.
            s_host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            s_host.Dispose();
        }
    }

    /// <summary>Also used by the XAML previewer, which runs without a host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure(() => s_host is null ? new App() : new App(s_host.Services.GetRequiredService<IEngineService>()))
            .UsePlatformDetect()
            .LogToTrace();
}
