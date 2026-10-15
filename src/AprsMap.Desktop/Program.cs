// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Core;
using AprsMap.Hosting;
using AprsMap.UI;
using AprsMap.UI.Appearance;
using AprsMap.UI.Platform;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AprsMap.Desktop;

internal static class Program
{
    private static IHost? s_host;
    private static PlatformServices? s_platform;

    [STAThread]
    public static int Main(string[] args)
    {
        // appsettings.json next to the executable holds first-run defaults and logging; the user's settings live in
        // their own settings folder (SettingsLocation).
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory }
        );
        builder.AddAprsMapEngine().AddAprsMapTelemetry("desktop");
        s_host = builder.Build();
        s_host.Start();
        // The viewer's appearance, read before the first window so it opens in the right theme and language.
        var folder = SettingsLocation.Folder(s_host.Services.GetRequiredService<IOptions<EngineOptions>>().Value);
        var preferences = new FilePreferenceStore(folder);
        s_platform = new PlatformServices
        {
            Preferences = preferences,
            Appearance = new AppearancePreferences(preferences, TimeProvider.System)
                .LoadAsync()
                .GetAwaiter()
                .GetResult(),
            ShowPseudoLanguage = ShowPseudoLanguage(builder.Configuration),
        };
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
            if (Application.Current is App app)
            {
                app.SavePreferencesAsync().GetAwaiter().GetResult();
                app.Dispose();
            }
            // Graceful engine shutdown when the window closes; the UI thread has nothing left to do.
            s_host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            s_host.Dispose();
        }
    }

    /// <summary>The pseudo-language is for developers: debug builds, or <c>Ui:ShowPseudoLanguage</c> in configuration.</summary>
    private static bool ShowPseudoLanguage(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
#if DEBUG
        return true;
#else
        return string.Equals(configuration["Ui:ShowPseudoLanguage"], "true", StringComparison.OrdinalIgnoreCase);
#endif
    }

    /// <summary>Also used by the XAML previewer, which runs without a host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure(() =>
                s_host is null ? new App() : new App(s_host.Services.GetRequiredService<IEngineService>(), s_platform)
            )
            .UsePlatformDetect()
            .LogToTrace();
}
