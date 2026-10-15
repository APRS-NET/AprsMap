// SPDX-License-Identifier: AGPL-3.0-only
using System.ComponentModel;
using AprsMap.UI.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AprsMap.UI.Views;

/// <summary>
/// The Settings page. Code-behind only lays out for the window's width (sections down the side from 720 px, tabs
/// across the top below it; behavior 3) and puts focus on the first option when the page opens (behavior 20).
/// </summary>
public sealed partial class SettingsView : UserControl
{
    /// <summary>Below this width the sections become tabs across the top.</summary>
    public const double NarrowWidth = 720;

    private SettingsViewModel? _viewModel;

    /// <summary>Creates the view.</summary>
    public SettingsView() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    /// <summary>When Undo goes away (used, or timed out) with focus on it, focus goes back to Reset, not nowhere.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName == nameof(SettingsViewModel.CanUndoReset)
            && _viewModel?.CanUndoReset == false
            && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Button { Name: "UndoButton" } or null
        )
        {
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("ResetButton")?.Focus(), DispatcherPriority.Loaded);
        }
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < NarrowWidth;
        this.FindControl<ListBox>("SectionList")?.Classes.Set("narrow", narrow);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            FocusFirstOption();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusFirstOption();
    }

    private void FocusFirstOption() =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsEffectivelyVisible && DataContext is not null)
                {
                    var first = this.FindControl<ItemsControl>("AppearanceOptions")
                        ?.GetLogicalDescendants()
                        .OfType<ComboBox>()
                        .FirstOrDefault();
                    (first ?? (Control?)this.FindControl<Button>("SettingsBackButton"))?.Focus();
                }
            },
            DispatcherPriority.Loaded
        );
}
