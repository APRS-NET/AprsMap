// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using AprsMap.UI.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace AprsMap.UI.Views;

/// <summary>
/// The view for each panel of the shell, picked by its kind. Panels without a view show a
/// short note instead, so the default layout already has its final shape.
/// </summary>
public sealed class PanelTemplate : IDataTemplate
{
    /// <inheritdoc />
    public bool Match(object? data) => data is PanelTool or MapDocument or DashboardDocument;

    /// <inheritdoc />
    public Control? Build(object? param) =>
        param switch
        {
            PanelTool { Kind: PanelKinds.Packets } panel => new PacketsPanel { DataContext = panel.Context },
            PanelTool { Kind: PanelKinds.Details } panel => new DetailsPanel { DataContext = panel.Context },
            PanelTool { Kind: PanelKinds.Stations } panel => new StationsPanel { DataContext = panel.Context },
            PanelTool { Kind: PanelKinds.Messages } panel => new MessagesPanel { DataContext = panel.Context },
            PanelTool { Kind: PanelKinds.Bulletins } panel => new BulletinsPanel { DataContext = panel.Context },
            PanelTool { Kind: PanelKinds.Console } panel => new SourceConsolePanel { DataContext = panel.Context },
            PanelTool { Kind: PanelKinds.Captured } panel => new CapturedPacketsPanel { DataContext = panel.Context },
            PanelTool panel => Placeholder(panel.Kind),
            MapDocument { Context: ViewModels.MainViewModel { Map: not null } context } => new Map.MapPanel
            {
                DataContext = context,
            },
            MapDocument => Placeholder(PanelKinds.Map),
            DashboardDocument { Context: ViewModels.MainViewModel context } => new Statistics.DashboardPanel
            {
                DataContext = context.Dashboard,
            },
            DashboardDocument => Placeholder(PanelKinds.Dashboard),
            _ => null,
        };

    private static Border Placeholder(string kind)
    {
        var note = new TextBlock
        {
            Name = "PanelPlaceholder",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16),
        };
        note.Bind(TextBlock.TextProperty, Localizer.Instance.Observe("Shell.NotYet." + kind));
        // Theme tokens, looked up like DynamicResource so they follow theme and palette changes.
        note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("Brush.TextMuted"));
        var border = new Border { Child = note };
        border.Bind(
            Border.BackgroundProperty,
            border.GetResourceObservable(kind == PanelKinds.Map ? "Brush.Ground" : "Brush.Surface")
        );
        return border;
    }
}
