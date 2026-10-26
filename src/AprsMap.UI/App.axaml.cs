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
    private Map.MapViewModel? _map;

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
        if (_map is not null)
        {
            await _map.FlushAsync();
        }
    }

    /// <summary>The desktop main window's screen, for Gather windows (F1.0 behavior 14); null elsewhere.</summary>
    private ScreenArea? MainScreen() =>
        ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
        && window.Screens.ScreenFromWindow(window) is { } screen
            ? new ScreenArea(screen.Bounds, screen.WorkingArea, screen.Scaling)
            : null;

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

    /// <summary>Loads the saved layouts, then asks if one was used with the monitors there are now (F1.23 behavior 3).</summary>
    private static async Task LoadLayoutsAsync(MainViewModel viewModel)
    {
        await viewModel.Layouts.LoadAsync();
        viewModel.Layouts.ScreensChanged();
    }

    /// <summary>
    /// Restores the saved layout before anything shows, and saves it after each change and on exit (F1.0 behaviors 8–15).
    /// </summary>
    private void StartLayout(MainViewModel viewModel, MainWindowLayout? main)
    {
        // Any case, so a typed or bookmarked address works as well as Open in a new tab's.
        if (
            _platform.PopOutPanel is { } asked
            && Shell.PanelKinds.All.FirstOrDefault(p => p.Kind.Equals(asked, StringComparison.OrdinalIgnoreCase)).Kind
                is { } popOut
        )
        {
            // A panel popped out of another tab (F1.24 behavior 2): it alone, and the layout left to that tab.
            viewModel.Shell.ShowOnly(popOut);
            viewModel.PopOutTitle = Localization.Localizer.Instance[Shell.PanelKinds.TitleKey(popOut)];
            return;
        }
        var screens = main?.Screens() ?? [];
        main?.Restore(screens);
        // Saved layouts (F1.23): kept beside the layout, and offered when the monitors are a setup one was used with.
        viewModel.Layouts.Attach(
            _platform.Preferences,
            () => main?.Screens() ?? [],
            () => main?.Placement(),
            main is null ? null : main.Apply
        );
        _ = LoadLayoutsAsync(viewModel);
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
            main.Window.Screens.Changed += (_, _) =>
            {
                viewModel.Shell.FitOpenWindows(main.Screens());
                viewModel.Layouts.ScreensChanged();
            };
        }
    }

    /// <summary>The app's icon, for windows (title bar, task bar, dock).</summary>
    internal static WindowIcon AppIcon() => new(AssetLoader.Open(new Uri("avares://AprsMap.UI/Assets/icon.png")));

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        Appearance.Apply(_platform.Appearance);
        Appearance.Attach(this);
        DockStrings.Attach(Resources, Localization.Localizer.Instance);
        // Ctrl+Tab switches between panels (F1.0 behavior 4); the map is the only document, so Dock's document
        // selector gets no key of its own.
        Dock.Settings.DockSettings.ToolSelectorKeyGesture = new Avalonia.Input.KeyGesture(
            Avalonia.Input.Key.Tab,
            Avalonia.Input.KeyModifiers.Control
        );
        Dock.Settings.DockSettings.DocumentSelectorKeyGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.None);
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
                Map = new Map.MapViewModel(_platform.Tiles, _platform.Preferences, TimeProvider.System),
                Preferences = _platform.Preferences,
                MainScreen = MainScreen,
                SharedStation = _platform.SharedStation,
                OperatorSession = _platform.OperatorSession,
                Installer = _platform.Installer,
                FolderOpener = _platform.FolderOpener,
                WebViewer = _platform.WebViewer,
                UpdateEngine = _platform.UpdateEngine, // F1.39 behavior 5
                GatewayClient = _platform.GatewayClient,
                MenuStyle = _platform.MenuStyle,
                SetPageTitle = _platform.SetTitle,
                Restart = _platform.Restart,
                SelectionChannel = _platform.SelectionChannel, // F1.24 behavior 3
            };
            viewModel.Shell.Factory.PopOutOpener = _platform.OpenPopOut; // F1.24 behavior 1
            created = viewModel;
            _map = viewModel.Map;
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
