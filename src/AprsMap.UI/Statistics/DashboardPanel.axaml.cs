// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia;
using Avalonia.Controls;

namespace AprsMap.UI.Statistics;

/// <summary>
/// The dashboard tab's view (docs/features/F1.22-statistics.md). It asks for numbers only while it's on screen: a
/// tab behind the map, or a closed one, costs nothing (behavior 2).
/// </summary>
public sealed partial class DashboardPanel : UserControl
{
    /// <summary>Creates the view.</summary>
    public DashboardPanel() => InitializeComponent();

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
