// SPDX-License-Identifier: AGPL-3.0-only
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
        // Dock doesn't report splitter drags or floating-window resizes; a released pointer may have done either.
        ShellDock.AddHandler(
            PointerReleasedEvent,
            (_, _) => (DataContext as MainViewModel)?.Shell.OnChanged(),
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true
        );
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
