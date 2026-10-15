// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Platform;
using AprsMap.UI.Shell;
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
    private LayoutPreferences? _layout;

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

    /// <summary>The style class that gives every target a 44 px floor on touch screens (App.axaml).</summary>
    public const string TouchClass = "touch";

    /// <inheritdoc />
    public void Dispose()
    {
        _preferences?.Dispose();
        _layout?.Dispose();
    }

    /// <summary>Writes preferences still waiting for their save delay; the desktop app calls it on exit.</summary>
    public async Task SavePreferencesAsync()
    {
        if (_preferences is not null)
        {
            await _preferences.FlushAsync();
        }
        if (_layout is not null)
        {
            await _layout.FlushAsync();
        }
    }

    /// <summary>
    /// Reset the app's part on this device (F0.10): the default appearance and layout, then this viewer's stored
    /// preferences deleted, so nothing is written back.
    /// </summary>
    private async Task ClearLocalDataAsync(MainViewModel viewModel)
    {
        Appearance.Apply(new AppearanceSettings());
        viewModel.Shell.Reset();
        _preferences?.Discard();
        _layout?.Discard();
        if (_platform.Preferences is { } store)
        {
            await store.ClearAsync();
        }
    }

    /// <summary>
    /// Restores the saved layout before anything shows, and saves it after each change and on exit (F1.0 behaviors 8–15).
    /// </summary>
    private void StartLayout(MainViewModel viewModel, MainWindowLayout? main)
    {
        var screens = main?.Screens() ?? [];
        main?.Restore(screens);
        if (_platform.Preferences is not { } store)
        {
            viewModel.RestoreLayout(_platform.SavedLayout, screens);
            return;
        }
        _layout = new LayoutPreferences(store, TimeProvider.System);
        if (viewModel.RestoreLayout(_platform.SavedLayout, screens) == LayoutRestore.Unreadable)
        {
            _ = _layout.SetAsideAsync();
        }
        // Written on the UI thread: open floating windows are asked where they are.
        var closing = false;
        string Write() => Avalonia.Threading.Dispatcher.UIThread.Invoke(() => viewModel.Shell.Save(main?.Placement()));
        void Changed()
        {
            if (!closing)
            {
                _layout.Schedule(Write);
            }
        }
        viewModel.Shell.Changed += (_, _) => Changed();
        if (main is not null)
        {
            main.Changed += (_, _) => Changed();
            main.Window.Closing += (_, _) =>
            {
                // The layout as the window closes; what happens while it closes (windows going away) doesn't count.
                closing = true;
                var text = viewModel.Shell.Save(main.Placement());
                _layout.Schedule(() => text); // written by SavePreferencesAsync on exit
            };
            main.Window.Screens.Changed += (_, _) => viewModel.Shell.FitOpenWindows(main.Screens());
        }
    }

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
            // In the browser there are no OS windows: floating panels, drag previews and dock targets stay in the page.
            Dock.Settings.DockSettings.UseManagedWindows = _platform.ManagedWindows;
            MainViewModel? created = null;
            var viewModel = new MainViewModel(
                _engine,
                TimeProvider.System,
                deviceLocation: _platform.DeviceLocation,
                appearance: Appearance,
                showPseudoLanguage: _platform.ShowPseudoLanguage,
                managedWindows: _platform.ManagedWindows
            )
            {
                ClearLocalData = () => ClearLocalDataAsync(created!),
                SharedStation = _platform.SharedStation,
                Restart = _platform.Restart,
            };
            created = viewModel;
            Control view = new MainView { DataContext = viewModel };
            if (_platform.CoarsePointer)
            {
                view.Classes.Add(TouchClass);
            }
            switch (ApplicationLifetime)
            {
                case IClassicDesktopStyleApplicationLifetime desktop:
                    var window = new Window
                    {
                        Title = Brand.DisplayName,
                        Icon = AppIcon(),
                        Content = view,
                        Width = 1280,
                        Height = 800,
                    };
                    desktop.MainWindow = window;
                    StartLayout(viewModel, new MainWindowLayout(window, _platform.SavedLayout));
                    break;
                case ISingleViewApplicationLifetime single:
                    single.MainView = view;
                    StartLayout(viewModel, null);
                    break;
            }
            _ = viewModel.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
