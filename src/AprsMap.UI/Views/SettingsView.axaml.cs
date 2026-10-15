// SPDX-License-Identifier: AGPL-3.0-only
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

    /// <summary>Creates the view.</summary>
    public SettingsView() => AvaloniaXamlLoader.Load(this);

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
