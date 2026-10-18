// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace AprsMap.UI.Statistics;

/// <summary>
/// The dashboard tab's view (docs/features/F1.22-statistics.md). It asks for numbers only while it's on screen: a
/// tab behind the map, or a closed one, costs nothing (behavior 2).
/// </summary>
public sealed partial class DashboardPanel : UserControl
{
    /// <summary>Creates the view.</summary>
    public DashboardPanel()
    {
        InitializeComponent();
        // Each table's Copy button: the table as tab-separated text (behavior 14).
        AddHandler(Button.ClickEvent, OnClick);
    }

    private void OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (
            e.Source is Button { Classes: var classes, DataContext: StatTable table }
            && classes.Contains("copy-table")
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
        )
        {
            _ = clipboard.SetTextAsync(table.CopyText());
            e.Handled = true;
        }
        // Each tile's Copy: its number and series (behavior 8).
        else if (
            e.Source is Button { Classes: var tileClasses, DataContext: Tile tile }
            && tileClasses.Contains("copy-tile")
            && TopLevel.GetTopLevel(this)?.Clipboard is { } tileClipboard
        )
        {
            _ = tileClipboard.SetTextAsync(tile.CopyText());
            e.Handled = true;
        }
    }

    private DashboardViewModel? Model => DataContext as DashboardViewModel;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Model?.Start();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Model?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null)
        {
            Model?.Start();
        }
    }
}
