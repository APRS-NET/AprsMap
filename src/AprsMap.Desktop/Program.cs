// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using AprsMap.Core;
using AprsMap.Hosting;
using AprsMap.UI;
using AprsMap.UI.Appearance;
using AprsMap.UI.Platform;
using AprsMap.UI.Shell;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;

namespace AprsMap.Desktop;

internal static class Program
{
    private static IHost? s_host;
    private static PlatformServices? s_platform;
    private static IEngineService? s_engine;

    [STAThread]
    public static int Main(string[] args)
    {
        // First, before anything else: lets Velopack's installer and updater run their hooks (F4.15 behavior 11).
        VelopackApp.Build().Run();
        var installer = VelopackInstaller.IsInstalled ? new VelopackInstaller() : null;
        if (installer is not null)
        {
            LinuxDesktopEntry.Refresh();
        }
        // appsettings.json next to the executable holds first-run defaults and logging; the user's settings live in
        // their own settings folder (SettingsLocation).
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory }
        );
        AddMachineDefaults(builder.Configuration);
        // The defaults for every user, for Help › About (F1.31 behavior 1.5): read only, the first place looked.
        builder.Services.AddSingleton<AprsMap.Core.IStorageFolderSource>(
            new AprsMap.Core.StorageFolderSource(() =>
                [
                    AprsMap.Core.StorageFolderSource.At(
                        Contracts.StorageFolderKind.Machine,
                        AppFolders.For(Brand.Slug).Machine[0],
                        false
                    ),
                ]
            )
        );
        builder.AddAprsMapEngine().AddAprsMapTiles().AddAprsMapTelemetry("desktop");
        // An installed build is ready to update when its feed is attached, an archive build when its archive is (F4.15
        // behavior 4).
        builder.Services.PostConfigure<AprsMap.Core.EngineOptions>(o =>
        {
            o.Updates.Kind = installer is null ? AprsMap.Core.InstallKind.Archive : AprsMap.Core.InstallKind.Installed;
            o.Updates.RuntimeId ??= System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            o.DeviceList.UrlIsSetting = true; // the address under Settings › Data › Advanced (F4.16 behavior 10)
        });
        s_host = builder.Build();
        var engineOptions = s_host.Services.GetRequiredService<IOptions<EngineOptions>>().Value;
        // The user's own folders (F4.15 behavior 13): created, with an earlier version's files moved in, before anything
        // reads them. A configured folder is left as it is.
        if (string.IsNullOrWhiteSpace(engineOptions.SettingsFolder))
        {
            SettingsLocation.PrepareUserFolders();
        }
        var folder = SettingsLocation.Folder(engineOptions);
        var preferences = new FilePreferenceStore(folder);
        // --reset-all starts over (F0.10): every profile, setting and preference deleted before the engine reads them.
        if (args.Contains("--reset-all", StringComparer.OrdinalIgnoreCase))
        {
            new FileSettingsStore(folder).DeleteAsync(CancellationToken.None).GetAwaiter().GetResult();
            preferences.ClearAsync().GetAwaiter().GetResult();
        }
        // A gateway's client (F1.39): the gateway's station, and this computer's engine only for its own update checks.
        // The options are read once and shared, so this is in force before the engine starts.
        var gatewayClient = DesktopGatewayClient.Load(folder, args);
        var client = gatewayClient.Connected is { } gateway
            ? new GatewayConnection(gateway, gatewayClient.ApiKey)
            : null;
        if (client is not null)
        {
            engineOptions.UpdatesOnly = true;
        }
        s_host.Start();
        client?.Start(s_host.Services.GetRequiredService<ILogger<GatewayConnection>>());
        // The web viewer (F1.35): the browser app beside the program, or where configured for a development build.
        // Off as a gateway's client, which serves browsers itself (F1.39 behavior 5).
        var webViewer = client is null
            ? new DesktopWebViewer(
                s_host.Services,
                preferences,
                builder.Configuration["WebViewer:WebRoot"] is { Length: > 0 } configured
                    ? configured
                    : Path.Combine(AppContext.BaseDirectory, "browser"),
                s_host.Services.GetRequiredService<ILogger<DesktopWebViewer>>()
            )
            : null;
        if (webViewer is not null)
        {
            _ = webViewer.StartAsync(CancellationToken.None); // shares again if it was on; a problem shows in Settings
        }
        // The viewer's appearance, read before the first window so it opens in the right theme and language.
        // --reset-layout starts with the default layout (F1.0 behavior 10); the next save replaces the stored one.
        var resetLayout = args.Contains("--reset-layout", StringComparer.OrdinalIgnoreCase);
        s_platform = new PlatformServices
        {
            Preferences = preferences,
            Tiles =
                client?.Tiles
                ?? new DesktopTileSource(s_host.Services.GetRequiredService<AprsMap.Tiles.ITileService>()),
            SavedLayout = resetLayout
                ? null
                : new LayoutPreferences(preferences, TimeProvider.System).LoadAsync().GetAwaiter().GetResult(),
            Appearance = new AppearancePreferences(preferences, TimeProvider.System)
                .LoadAsync()
                .GetAwaiter()
                .GetResult(),
            ShowPseudoLanguage = ShowPseudoLanguage(builder.Configuration["Ui:ShowPseudoLanguage"]),
            // The macOS menu bar holds the menus there; elsewhere a bar in the window, hidden until Alt (F1.29).
            MenuStyle = OperatingSystem.IsMacOS() ? UI.Shell.MenuStyle.System : UI.Shell.MenuStyle.InWindow,
            Installer = installer,
            // Open folder in Help › About (F1.31); a gateway's folders are on another computer.
            FolderOpener = client is null ? new DesktopFolderOpener() : null,
            WebViewer = webViewer,
            GatewayClient = gatewayClient,
            SharedStation = client is not null,
            OperatorSession = client?.OperatorSession,
            UpdateEngine = client is null ? null : s_host.Services.GetRequiredService<IEngineService>(),
        };
        s_engine = client?.Engine ?? s_host.Services.GetRequiredService<IEngineService>();
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
            // The UI thread's context outlives its dispatcher: anything that resumed on it would never finish, and the
            // update after Restart now would never start.
            SynchronizationContext.SetSynchronizationContext(null);
            if (Application.Current is App app)
            {
                // Off the UI thread: its dispatcher has stopped, so a save that resumed on it would never finish.
                Task.Run(app.SavePreferencesAsync).GetAwaiter().GetResult();
                app.Dispose();
            }
            // Browsers first, then the engine they were watching.
            webViewer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            // Graceful engine shutdown when the window closes; the UI thread has nothing left to do.
            s_host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            s_host.Dispose();
            // Last: a downloaded update goes in once this process has ended, reopening it after Restart now.
            installer?.Finish();
            gatewayClient.Finish(); // after Apply and restart in Settings › Connection (F1.39 behavior 1)
        }
    }

    /// <summary>
    /// Defaults an administrator set for every user of the machine (F4.15 behavior 13), from <c>appsettings.json</c> in
    /// the machine folders: above the program's own file, which an update replaces, and below environment variables
    /// and the command line.
    /// </summary>
    private static void AddMachineDefaults(ConfigurationManager configuration)
    {
        var sources = configuration.Sources;
        var at = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                at = i + 1;
            }
        }
        // Least important first, so the first folder wins.
        foreach (var folder in AppFolders.For(Brand.Slug).Machine.Reverse())
        {
            var file = Path.Combine(folder, "appsettings.json");
            if (File.Exists(file))
            {
                var source = new JsonConfigurationSource { Path = file, Optional = true };
                source.ResolveFileProvider();
                sources.Insert(at++, source);
            }
        }
    }

    /// <summary>The pseudo-language is for developers: debug builds, or <c>Ui:ShowPseudoLanguage</c> in configuration.</summary>
    private static bool ShowPseudoLanguage(string? configured)
    {
#if DEBUG
        _ = configured;
        return true;
#else
        return string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase);
#endif
    }

    /// <summary>Also used by the XAML previewer, which runs without a host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure(() => s_engine is null ? new App() : new App(s_engine, s_platform))
            .UsePlatformDetect()
            .LogToTrace();
}
