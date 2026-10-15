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
        AppMenu.Opening += (_, _) => FillAppMenu();
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
        string[] kinds = [PanelKinds.Map, .. PanelKinds.All.Select(p => p.Kind)];
        foreach (var kind in kinds)
        {
            object panel;
            if (kind == PanelKinds.Map)
            {
                panel = ShellFactory.CreateMap();
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

    /// <summary>
    /// The app menu's items, built as it opens because the closed panels change (F1.0 behaviors 4, 10, 14): Show for each
    /// closed panel, Reset layout, and on the desktop Gather floating windows.
    /// </summary>
    private void FillAppMenu()
    {
        AppMenu.Items.Clear();
        if (DataContext is not MainViewModel vm)
        {
            return;
        }
        var text = Localization.Localizer.Instance;
        foreach (var (kind, title) in vm.ClosedPanels)
        {
            AppMenu.Items.Add(
                new MenuItem
                {
                    Header = text.Format("Shell.Menu.Show", new Dictionary<string, object?> { ["panel"] = title }),
                    Command = vm.ShowPanelCommand,
                    CommandParameter = kind,
                }
            );
        }
        if (AppMenu.Items.Count > 0)
        {
            AppMenu.Items.Add(new Separator());
        }
        AppMenu.Items.Add(new MenuItem { Header = text["Shell.Menu.ResetLayout"], Command = vm.ResetLayoutCommand });
        if (vm.CanGatherWindows)
        {
            AppMenu.Items.Add(new MenuItem { Header = text["Shell.Menu.Gather"], Command = vm.GatherWindowsCommand });
        }
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
    }

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
            var command =
                Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
            _settingsKey = new KeyBinding
            {
                Gesture = new KeyGesture(Key.OemComma, command),
                Command = vm.OpenSettingsCommand,
            };
            _topLevel.KeyBindings.Add(_settingsKey);
        }
    }

    private void UnbindSettingsKey()
    {
        if (_topLevel is not null && _settingsKey is not null)
        {
            _topLevel.KeyBindings.Remove(_settingsKey);
        }
        _settingsKey = null;
    }
}
