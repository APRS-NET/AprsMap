// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Packets.Export;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AprsMap.UI.Views;

/// <summary>
/// The menus (docs/features/F1.29-menus.md): the menu bar, hidden until Alt or F10 on Windows and Linux (behavior 4), the
/// same menus under ☰, and what they ask of the window (quit, full screen, saving, opening a page).
/// </summary>
public sealed partial class MainView : IMenuActions
{
    /// <summary>The narrowest window that shows an always-shown menu bar in the web app (behavior 7).</summary>
    private const double WideForMenuBar = 900;

    /// <summary>The main view model's properties the menus show; any other change leaves them alone.</summary>
    private static readonly HashSet<string> MenuProperties =
    [
        nameof(MainViewModel.SelectedProfile),
        nameof(MainViewModel.CanSwitchProfiles),
        nameof(MainViewModel.InternetOn),
        nameof(MainViewModel.AprsIsOn),
        nameof(MainViewModel.TilesOn),
        nameof(MainViewModel.TransmitOn),
        nameof(MainViewModel.CanTransmit),
        nameof(MainViewModel.CanToggle),
        nameof(MainViewModel.CanSendPosition),
        nameof(MainViewModel.CanSendPositionOnAprsIs),
        nameof(MainViewModel.IsAprsIsTransmitEnabled),
        nameof(MainViewModel.Auth),
        nameof(MainViewModel.CanClearData),
        nameof(MainViewModel.AlwaysShowMenuBar),
    ];

    private AppMenus? _menus;
    private bool _menusStale;
    private bool _altAlone;

    /// <summary>Refreshes the menus soon, once, after whatever changed.</summary>
    private void MenusChanged()
    {
        if (!_menusStale)
        {
            _menusStale = true;
            Dispatcher.UIThread.Post(FillAppMenu, DispatcherPriority.Background);
        }
    }

    private void HookMenus()
    {
        MenuBar.Closed += (_, _) =>
        {
            if (DataContext is MainViewModel vm && vm.MenuBarRevealed)
            {
                vm.MenuBarRevealed = false;
            }
            ShowMenuBar();
            if (_menusStale)
            {
                FillAppMenu();
            }
        };
        MenuBar.Opened += (_, _) =>
        {
            if (DataContext is not MainViewModel vm)
            {
                return;
            }
            if (vm.MenuStyle != MenuStyle.InWindow && !MenuBar.IsHitTestVisible)
            {
                // Alt opened it, but here Alt is the browser's or the menus are the system's (behaviors 5, 6).
                MenuBar.Close();
                return;
            }
            vm.MenuBarRevealed = true;
            ShowMenuBar();
        };
        // Alt + a menu's letter opens that menu without opening the bar as a whole: show the bar for it too.
        MenuBar.AddHandler(
            MenuItem.SubmenuOpenedEvent,
            (_, _) =>
            {
                if (DataContext is MainViewModel { MenuStyle: MenuStyle.InWindow } vm && !vm.MenuBarRevealed)
                {
                    vm.MenuBarRevealed = true;
                    ShowMenuBar();
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );
        AppMenu.Closed += (_, _) =>
        {
            if (_menusStale)
            {
                FillAppMenu();
            }
        };
    }

    /// <summary>
    /// Shows the bar while it's always shown or a menu is open, and keeps it at no height otherwise, still in the window so
    /// Alt and Alt + a menu's letter reach it (behavior 4). On macOS the system menu bar holds the menus instead.
    /// </summary>
    private void ShowMenuBar()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }
        var shown = vm.MenuStyle switch
        {
            MenuStyle.InWindow => vm.AlwaysShowMenuBar || vm.MenuBarRevealed || MenuBar.IsOpen,
            MenuStyle.Hamburger => vm.AlwaysShowMenuBar && Bounds.Width >= WideForMenuBar,
            _ => false,
        };
        MenuBar.IsVisible = vm.MenuStyle != MenuStyle.System && (shown || vm.MenuStyle == MenuStyle.InWindow);
        MenuBar.MaxHeight = shown ? double.PositiveInfinity : 0;
        MenuBar.IsHitTestVisible = shown;
    }

    /// <summary>F10 shows the menu bar with File open, as Alt does (behavior 4.1).</summary>
    private void OnMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel { MenuStyle: MenuStyle.InWindow } vm)
        {
            return;
        }
        _altAlone = e.Key is Key.LeftAlt or Key.RightAlt && e.KeyModifiers == KeyModifiers.Alt;
        if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.None)
        {
            OpenMenuBar(vm);
            e.Handled = true;
        }
    }

    /// <summary>Alt pressed and released alone shows the menu bar with File open (behavior 4.1).</summary>
    private void OnMenuKeyUp(object? sender, KeyEventArgs e)
    {
        if (
            DataContext is MainViewModel { MenuStyle: MenuStyle.InWindow } vm
            && _altAlone
            && e.Key is Key.LeftAlt or Key.RightAlt
            && !MenuBar.IsOpen
        )
        {
            OpenMenuBar(vm);
            e.Handled = true;
        }
        _altAlone = false;
    }

    private void OpenMenuBar(MainViewModel vm)
    {
        vm.MenuBarRevealed = true;
        ShowMenuBar();
        if (MenuBar.Items.Count > 0 && MenuBar.Items[0] is MenuItem first)
        {
            first.Focus(NavigationMethod.Directional);
            first.Open();
        }
    }

    // IMenuActions: what only the window can do.

    bool IMenuActions.CanQuit => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    bool IMenuActions.CanFullScreen => TopLevel.GetTopLevel(this) is Window;

    bool IMenuActions.IsFullScreen => TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.FullScreen };

    void IMenuActions.Quit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Close(); // closing saves the layout, as the window's ×
        }
    }

    void IMenuActions.ToggleFullScreen()
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.WindowState =
                window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        }
    }

    Task IMenuActions.SavePacketsAsync(PacketExporter exporter) =>
        DataContext is MainViewModel vm && TopLevel.GetTopLevel(this) is { } topLevel
            ? PacketsPanel.SaveAsync(topLevel, vm.PacketList, exporter)
            : Task.CompletedTask;

    async Task IMenuActions.OpenUriAsync(Uri uri)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>Ctrl+F, F11 and Ctrl+Q, with Settings' Ctrl+, (behavior 17).</summary>
    private void BindMenuKeys(TopLevel topLevel, MainViewModel vm)
    {
        topLevel.AddHandler(KeyDownEvent, OnMenuKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(KeyUpEvent, OnMenuKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        _menuKeys =
        [
            new KeyBinding
            {
                Gesture = AppMenus.FindGesture,
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(vm.FindStation),
            },
            new KeyBinding
            {
                Gesture = AppMenus.FullScreenGesture,
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(((IMenuActions)this).ToggleFullScreen),
            },
        ];
        if (((IMenuActions)this).CanQuit)
        {
            _menuKeys.Add(
                new KeyBinding
                {
                    Gesture = AppMenus.QuitGesture,
                    Command = new CommunityToolkit.Mvvm.Input.RelayCommand(((IMenuActions)this).Quit),
                }
            );
        }
        foreach (var key in _menuKeys)
        {
            topLevel.KeyBindings.Add(key);
        }
    }

    private void UnbindMenuKeys(TopLevel topLevel)
    {
        topLevel.RemoveHandler(KeyDownEvent, OnMenuKeyDown);
        topLevel.RemoveHandler(KeyUpEvent, OnMenuKeyUp);
        foreach (var key in _menuKeys)
        {
            topLevel.KeyBindings.Remove(key);
        }
        _menuKeys = [];
    }

    private List<KeyBinding> _menuKeys = [];
}
