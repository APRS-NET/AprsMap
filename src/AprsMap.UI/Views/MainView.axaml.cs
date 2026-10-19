// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AprsMap.UI.Views;

/// <summary>The main screen.</summary>
public sealed partial class MainView : UserControl
{
    private KeyBinding? _settingsKey;
    private TopLevel? _topLevel;

    /// <summary>Creates the view.</summary>
    public MainView()
    {
        InitializeComponent();
        // Kept filled, never filled as it opens: on the desktop the menu is a window of its own, created as it opens,
        // and one emptied and refilled then showed nothing. It follows the layout and the language instead.
        Localization.Localizer.Instance.LanguageChanged += (_, _) =>
        {
            _menus = null; // the menus' words are worked out when they're built
            FillAppMenu();
        };
        HookMenus();
        // A toggle's marker opens the menu where it's changed (F1.15 behavior 4).
        ToggleMarkers.AddHandler(Button.ClickEvent, (_, _) => OpenConnectionMenu());
        // Dock doesn't report splitter drags or floating-window resizes; a released pointer may have done either.
        ShellDock.AddHandler(
            PointerReleasedEvent,
            (_, _) => (DataContext as MainViewModel)?.Shell.OnChanged(),
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true
        );
    }

    private MenuFlyout AppMenu => (MenuFlyout)AppMenuButton.Flyout!;

    /// <summary>Below this width the panels are fixed tabs (F1.0 behavior 7), as Settings' sections become tabs.</summary>
    public const double NarrowWidth = SettingsView.NarrowWidth;

    /// <summary>Whether the narrow tabs are showing instead of the docked layout.</summary>
    public bool IsNarrow => NarrowShell.IsVisible;

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < NarrowWidth;
        if (narrow == NarrowShell.IsVisible)
        {
            return;
        }
        if (narrow && NarrowShell.ItemCount == 0)
        {
            FillNarrowShell();
        }
        NarrowShell.IsVisible = narrow;
        ShellDock.IsVisible = !narrow;
    }

    /// <summary>One tab per panel, map first, each with its own view (Dock's views stay with the docked layout).</summary>
    private void FillNarrowShell()
    {
        var template = new PanelTemplate();
        var text = Localization.Localizer.Instance;
        string[] kinds = [PanelKinds.Map, PanelKinds.Dashboard, .. PanelKinds.All.Select(p => p.Kind)];
        foreach (var kind in kinds)
        {
            object panel;
            if (kind == PanelKinds.Map)
            {
                panel = ShellFactory.CreateMap();
            }
            else if (kind == PanelKinds.Dashboard)
            {
                var dashboard = ShellFactory.CreateDashboard();
                dashboard.Context = DataContext;
                panel = dashboard;
            }
            else
            {
                var tool = ShellFactory.CreatePanel(kind);
                tool.Context = DataContext;
                panel = tool;
            }
            var tab = new TabItem { Content = template.Build(panel) };
            tab.Bind(TabItem.HeaderProperty, text.Observe(PanelKinds.TitleKey(kind)));
            NarrowShell.Items.Add(tab);
        }
    }

    /// <summary>A saved layout added, renamed or deleted: the app menu lists them (F1.23 behavior 1).</summary>
    private void OnSavedLayoutsChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e
    ) => MenusChanged();

    /// <summary>
    /// Refreshes the menus (docs/features/F1.29-menus.md): the menu bar and the ☰ menu, each its own items from the
    /// same <see cref="AppMenus"/>. An open menu isn't touched; it's refreshed when it closes.
    /// </summary>
    private void FillAppMenu()
    {
        if (DataContext is not MainViewModel vm)
        {
            AppMenu.Items.Clear();
            MenuBar.ItemsSource = null;
            return;
        }
        if (MenuBar.IsOpen || AppMenu.IsOpen)
        {
            _menusStale = true;
            return;
        }
        _menusStale = false;
        _menus ??= new AppMenus(vm, Localization.Localizer.Instance, this);
        var menus = _menus.Menus;
        AppMenu.Items.Clear();
        foreach (var menu in menus)
        {
            AppMenu.Items.Add(MenuBuilder.Top(menu));
        }
        if (vm.MenuStyle == MenuStyle.System)
        {
            if (TopLevel.GetTopLevel(this) is Window window)
            {
                NativeMenu.SetMenu(window, MenuBuilder.Native(menus));
            }
        }
        else
        {
            MenuBar.ItemsSource = menus.Select(MenuBuilder.Top).ToList();
        }
        ShowMenuBar();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindSettingsKey();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnbindSettingsKey();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindSettingsKey();
        if (_shell is not null)
        {
            _shell.Changed -= OnShellChanged;
        }
        if (_main is not null)
        {
            _main.PropertyChanged -= OnMainChanged;
            _main.Layouts.Layouts.CollectionChanged -= OnSavedLayoutsChanged;
            _main.SourcePills.CollectionChanged -= OnSavedLayoutsChanged;
            _main.Profiles.CollectionChanged -= OnSavedLayoutsChanged;
            _main.Destination.PropertyChanged -= OnDestinationChanged;
        }
        _menus = null;
        _main = DataContext as MainViewModel;
        _shell = _main?.Shell;
        if (_shell is not null)
        {
            _shell.Changed += OnShellChanged;
        }
        if (_main is not null)
        {
            _main.PropertyChanged += OnMainChanged;
            _main.OpenUri = ((IMenuActions)this).OpenUriAsync;
            _main.Layouts.Layouts.CollectionChanged += OnSavedLayoutsChanged;
            _main.SourcePills.CollectionChanged += OnSavedLayoutsChanged; // the lists the menus hold
            _main.Profiles.CollectionChanged += OnSavedLayoutsChanged;
            _main.Destination.PropertyChanged += OnDestinationChanged;
        }
        FillAppMenu();
    }

    private Shell.ShellViewModel? _shell;
    private MainViewModel? _main;

    /// <summary>Clear data is offered once the settings say this viewer may change the station.</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && MenuProperties.Contains(name))
        {
            MenusChanged();
        }
        if (e.PropertyName is nameof(MainViewModel.MenuBarRevealed) or nameof(MainViewModel.AlwaysShowMenuBar))
        {
            ShowMenuBar();
        }
    }

    /// <summary>A panel closed or came back: the menu's Show items follow, on the UI thread.</summary>
    private void OnShellChanged(object? sender, EventArgs e) => MenusChanged();

    private void OnDestinationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        MenusChanged();

    /// <summary>
    /// Ctrl+, opens Settings (Cmd+, on macOS: the platform's command key; F0.6 behavior 1). Bound on the window, so it
    /// works wherever focus is, including before anything has focus.
    /// </summary>
    private void BindSettingsKey()
    {
        UnbindSettingsKey();
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null && DataContext is MainViewModel vm)
        {
            BindMenuKeys(_topLevel, vm);
            var command =
                Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
            _settingsKey = new KeyBinding
            {
                Gesture = new KeyGesture(Key.OemComma, command),
                Command = vm.OpenSettingsCommand,
            };
            _topLevel.KeyBindings.Add(_settingsKey);
            // Ctrl+T opens the connection menu with the quick toggles (F1.15 behavior 1).
            _togglesKey = new KeyBinding
            {
                Gesture = new KeyGesture(Key.T, command),
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OpenConnectionMenu),
            };
            _topLevel.KeyBindings.Add(_togglesKey);
        }
    }

    private KeyBinding? _togglesKey;

    /// <summary>The connection menu, from Ctrl+T or a toggle's marker.</summary>
    /// <summary>The connection menu at the APRS-IS pill, or at the status bar's edge while that pill is hidden.</summary>
    private void OpenConnectionMenu() =>
        ConnectionButton.Flyout?.ShowAt(ConnectionPill.IsVisible ? ConnectionButton : AppMenuButton);

    private void UnbindSettingsKey()
    {
        if (_topLevel is not null)
        {
            UnbindMenuKeys(_topLevel);
        }
        if (_topLevel is not null && _settingsKey is not null)
        {
            _topLevel.KeyBindings.Remove(_settingsKey);
        }
        if (_topLevel is not null && _togglesKey is not null)
        {
            _topLevel.KeyBindings.Remove(_togglesKey);
        }
        _settingsKey = null;
        _togglesKey = null;
    }
}
