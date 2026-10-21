// SPDX-License-Identifier: AGPL-3.0-only
using Dock.Model.Mvvm.Controls;

namespace AprsMap.UI.Shell;

/// <summary>
/// A panel in the shell (docs/features/F1.0-shell-and-docking.md). Its <see cref="Kind"/> picks the view; only its id,
/// kind and place are saved with a layout, and the content always comes from the engine.
/// </summary>
public class PanelTool : Tool
{
    /// <summary>Which panel this is, one of <see cref="PanelKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;
}

/// <summary>The map: the shell's fixed centre. A document, so panels dock around it; it can't be closed or floated.</summary>
public class MapDocument : Document;

/// <summary>
/// The dashboard (docs/features/F1.22-statistics.md, behavior 1): a tab beside the map in the centre. Unlike the map it
/// can be closed (and shown again from the app menu) and floated.
/// </summary>
public class DashboardDocument : Document;

/// <summary>
/// An extra map view that follows one station (docs/features/F1.16-extra-map-views.md): a document beside the map,
/// which can be closed, tabbed or floated into its own window. Saved with the layout, so it opens again next time.
/// </summary>
public class WatchDocument : Document
{
    /// <summary>The station it follows.</summary>
    public string Callsign { get; set; } = string.Empty;
}

/// <summary>The panels this version knows, and where each one goes in the default layout.</summary>
public static class PanelKinds
{
    /// <summary>The map, the fixed centre (<see cref="MapDocument"/>).</summary>
    public const string Map = "map";

    /// <summary>The dashboard, a tab beside the map (<see cref="DashboardDocument"/>, F1.22).</summary>
    public const string Dashboard = "dashboard";

    /// <summary>The station list (F1.7).</summary>
    public const string Stations = "stations";

    /// <summary>The selected station's details (F1.6).</summary>
    public const string Details = "details";

    /// <summary>The live packet list (F1.8).</summary>
    public const string Packets = "packets";

    /// <summary>The conversations (docs/features/F2.4-messaging.md).</summary>
    public const string Messages = "messages";

    /// <summary>The bulletins and announcements heard (docs/features/F2.5-bulletins.md).</summary>
    public const string Bulletins = "bulletins";

    /// <summary>A source's live traffic, for troubleshooting (docs/features/F1.28-source-console.md).</summary>
    public const string Console = "console";

    /// <summary>The packets capture recorded (docs/features/F1.27-packet-capture.md, behavior 7).</summary>
    public const string Captured = "captured";

    /// <summary>The GPS's fix, accuracy and sky (docs/features/F3.12-gps-status.md).</summary>
    public const string Gps = "gps";

    /// <summary>Every panel, with the id of the dock it starts in (behavior 2).</summary>
    public static IReadOnlyList<(string Kind, string Dock)> All { get; } =
    [
        (Stations, ShellFactory.LeftDock),
        (Details, ShellFactory.RightDock),
        (Messages, ShellFactory.RightDock),
        (Bulletins, ShellFactory.RightDock),
        (Packets, ShellFactory.BottomDock),
        (Console, ShellFactory.BottomDock),
        (Captured, ShellFactory.BottomDock),
        (Gps, ShellFactory.RightDock), // on demand: not in the default layout
    ];

    /// <summary>
    /// Panels left out of the default layout, shown only when opened (the GPS panel, from the GPS pill: most stations
    /// have no GPS). Once open, they're saved with the layout like any other.
    /// </summary>
    public static IReadOnlySet<string> OnDemand { get; } = new HashSet<string>(StringComparer.Ordinal) { Gps };

    /// <summary>The resource key of a panel's title.</summary>
    public static string TitleKey(string kind) => "Shell.Panel." + kind;

    /// <summary>Every panel in the order View › Panels lists them (F1.29 behavior 12).</summary>
    public static IReadOnlyList<string> Ordered { get; } =
    [Map, Dashboard, Stations, Details, Packets, Messages, Bulletins, Console, Captured, Gps];
}
