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
public sealed class App : Application
{
    private readonly IEngineService? _engine;
    private readonly IDeviceLocation? _deviceLocation;

    /// <summary>For the XAML designer and previewer only.</summary>
    public App() { }

    /// <summary>Creates the app on top of an engine service, with the platform's device location where there is one.</summary>
    public App(IEngineService engine, IDeviceLocation? deviceLocation = null)
    {
        _engine = engine;
        _deviceLocation = deviceLocation;
    }

    /// <summary>Live appearance settings (theme, text size, corners…).</summary>
    public AppearanceService Appearance { get; } = new();

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>The app's icon, for windows (title bar, task bar, dock).</summary>
    internal static WindowIcon AppIcon() => new(AssetLoader.Open(new Uri("avares://AprsMap.UI/Assets/icon.png")));

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        Appearance.Attach(this);
        if (_engine is not null)
        {
            var viewModel = new MainViewModel(_engine, TimeProvider.System, deviceLocation: _deviceLocation);
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
