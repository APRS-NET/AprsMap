// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Data.Converters;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace AprsMap.UI.Shell;

/// <summary>Converters for the shell's XAML.</summary>
public static class ShellConverters
{
    /// <summary>True unless the value equals the parameter: hides "Move to left" in the left dock.</summary>
    public static IValueConverter IsNot { get; } =
        new FuncValueConverter<string?, string?, bool>((value, parameter) => value != parameter);

    /// <summary>True when the dockable is in a floating window: shows "Dock beside the map" instead of "Float".</summary>
    public static IValueConverter IsFloating { get; } = new FuncValueConverter<IDockable?, bool>(InFloatingWindow);

    /// <summary>True for a panel (not the map or the dashboard): its tab beside the map offers Move to (F1.0 behavior 16).</summary>
    public static IValueConverter IsPanel { get; } =
        new FuncValueConverter<IDockable?, bool>(dockable => dockable is PanelTool);

    /// <summary>True for the dashboard, whose floating tab offers Dock beside the map.</summary>
    public static IValueConverter IsDashboard { get; } =
        new FuncValueConverter<IDockable?, bool>(dockable => dockable is DashboardDocument);

    /// <summary>True when the dockable is in the main window.</summary>
    public static IValueConverter IsDocked { get; } =
        new FuncValueConverter<IDockable?, bool>(dockable => dockable is not null && !InFloatingWindow(dockable));

    private static bool InFloatingWindow(IDockable? dockable)
    {
        for (var at = dockable; at is not null; at = at.Owner)
        {
            if (at is IRootDock root)
            {
                return root.Window is not null;
            }
        }
        return false;
    }
}
