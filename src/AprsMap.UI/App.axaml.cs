// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Platform;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace AprsMap.UI;

/// <summary>
/// The shared application. Each head (desktop, browser) supplies the <see cref="IEngineService"/>: in-process on the
/// desktop, SignalR to the gateway in the browser. Nothing else differs.
/// </summary>
public sealed class App : Application, IDisposable
{
    private readonly IEngineService? _engine;
    private readonly PlatformServices _platform = new();
    private AppearancePreferences? _preferences;

    /// <summary>For the XAML designer and previewer only.</summary>
    public App() { }

    /// <summary>Creates the app on top of an engine service, with what the platform supplies.</summary>
    public App(IEngineService engine, PlatformServices? platform = null)
    {
        _engine = engine;
        _platform = platform ?? new PlatformServices();
    }

    /// <summary>Live appearance settings (theme, text size, corners…).</summary>
    public AppearanceService Appearance { get; } = new();

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public void Dispose() => _preferences?.Dispose();

    /// <summary>Writes preferences still waiting for their save delay; the desktop app calls it on exit.</summary>
    public Task SavePreferencesAsync() => _preferences?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>The app's icon, for windows (title bar, task bar, dock).</summary>
    internal static WindowIcon AppIcon() => new(AssetLoader.Open(new Uri("avares://AprsMap.UI/Assets/icon.png")));

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        Appearance.Apply(_platform.Appearance);
        Appearance.Attach(this);
        if (_platform.SystemReducedMotion)
        {
            Appearance.SetSystemReducedMotion(true);
        }
        if (_platform.Preferences is { } store)
        {
            // Subscribed after the first apply, so starting up doesn't write the file it just read.
            _preferences = new AppearancePreferences(store, TimeProvider.System);
            Appearance.Changed += (_, settings) => _preferences.Schedule(settings);
        }
        if (_engine is not null)
        {
            var viewModel = new MainViewModel(
                _engine,
                TimeProvider.System,
                deviceLocation: _platform.DeviceLocation,
                appearance: Appearance,
                showPseudoLanguage: _platform.ShowPseudoLanguage
            );
            Control view = new MainView { DataContext = viewModel };
            switch (ApplicationLifetime)
            {
                case IClassicDesktopStyleApplicationLifetime desktop:
                    desktop.MainWindow = new Window
                    {
                        Title = Brand.DisplayName,
                        Icon = AppIcon(),
                        Content = view,
                        Width = 1280,
                        Height = 800,
                    };
                    break;
                case ISingleViewApplicationLifetime single:
                    single.MainView = view;
                    break;
            }
            _ = viewModel.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
