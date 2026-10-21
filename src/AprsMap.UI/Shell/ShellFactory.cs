// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.Input;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace AprsMap.UI.Shell;

/// <summary>
/// Builds the default layout (behavior 2) and attaches the app's view model to every panel of a layout, new or loaded.
/// Floating panels open as OS windows on the desktop, and inside the page (managed windows) in the browser.
/// </summary>
public sealed class ShellFactory(object context, bool managedWindows) : Factory
{
    /// <summary>The ids of the default docks, where panels start and new panels are added (behavior 11).</summary>
    public const string LeftDock = "left";

    /// <inheritdoc cref="LeftDock" />
    public const string RightDock = "right";

    /// <inheritdoc cref="LeftDock" />
    public const string BottomDock = "bottom";

    /// <summary>The id of the dock that holds the map.</summary>
    public const string CentreDock = "centre";

    /// <summary>Set by the shell: moves a panel to the dock with the given id, creating it if it was collapsed.</summary>
    internal Action<IDockable, string>? Mover { get; set; }

    /// <summary>Moves a panel to the left dock (the panel menu's Move to left, behavior 4).</summary>
    public IRelayCommand<IDockable?> MoveToLeft => field ??= MoveCommand(LeftDock);

    /// <summary>Moves a panel to the right dock.</summary>
    public IRelayCommand<IDockable?> MoveToRight => field ??= MoveCommand(RightDock);

    /// <summary>Moves a panel to the bottom dock.</summary>
    public IRelayCommand<IDockable?> MoveToBottom => field ??= MoveCommand(BottomDock);

    /// <summary>Moves a panel to a tab beside the map (F1.0 behavior 16).</summary>
    public IRelayCommand<IDockable?> MoveBesideMap => field ??= MoveCommand(CentreDock);

    /// <summary>Set by the shell: puts the floating dashboard back beside the map.</summary>
    internal Action<IDockable>? Returner { get; set; }

    /// <summary>Docks the floating dashboard beside the map again (its tab menu, F1.22 behavior 1).</summary>
    public IRelayCommand<IDockable?> DockBesideMap =>
        field ??= new RelayCommand<IDockable?>(dashboard =>
        {
            if (dashboard is DashboardDocument)
            {
                Returner?.Invoke(dashboard);
            }
        });

    private RelayCommand<IDockable?> MoveCommand(string dockId) =>
        new(panel =>
        {
            if (panel is PanelTool)
            {
                Mover?.Invoke(panel, dockId);
            }
        });

    /// <inheritdoc />
    public override IRootDock CreateLayout()
    {
        var map = CreateMap();
        var centre = new DocumentDock
        {
            Id = CentreDock,
            IsCollapsable = false,
            CanCreateDocument = false,
            VisibleDockables = CreateList<IDockable>(map, CreateDashboard()),
            ActiveDockable = map,
        };
        var left = Side(LeftDock, Alignment.Left, 0.2);
        var right = Side(RightDock, Alignment.Right, 0.28);
        var bottom = Side(BottomDock, Alignment.Bottom, 0.3);
        foreach (var (kind, dockId) in PanelKinds.All.Where(p => !PanelKinds.OnDemand.Contains(p.Kind)))
        {
            var dock = dockId switch
            {
                LeftDock => left,
                RightDock => right,
                _ => bottom,
            };
            var panel = CreatePanel(kind);
            dock.VisibleDockables!.Add(panel);
            dock.ActiveDockable ??= panel;
        }

        var upper = new ProportionalDock
        {
            Id = "upper",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(
                left,
                new ProportionalDockSplitter(),
                centre,
                new ProportionalDockSplitter(),
                right
            ),
        };
        var main = new ProportionalDock
        {
            Id = "main",
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(upper, new ProportionalDockSplitter(), bottom),
        };
        var root = CreateRootDock();
        root.Id = "root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(main);
        root.ActiveDockable = main;
        root.DefaultDockable = main;
        return root;
    }

    /// <inheritdoc />
    public override void InitLayout(IDockable layout)
    {
        // Every panel gets the app's view model; the panel's Kind picks what it shows.
        DefaultContextLocator = () => context;
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => managedWindows ? new ManagedHostWindow() : new HostWindow(),
        };
        base.InitLayout(layout);
    }

    /// <summary>A new panel of <paramref name="kind"/>, with the id and abilities every panel has.</summary>
    public static PanelTool CreatePanel(string kind) =>
        new()
        {
            Id = kind,
            Kind = kind,
            CanClose = true,
            CanFloat = true,
            CanPin = true,
            CanDockAsDocument = true, // a tab beside the map, never in its place (F1.0 behavior 16)
        };

    /// <summary>The map, which can't be closed, floated or pinned away.</summary>
    public static MapDocument CreateMap() =>
        new()
        {
            Id = PanelKinds.Map,
            CanClose = false,
            CanFloat = false,
            CanPin = false,
        };

    /// <summary>The dashboard, a tab beside the map that can be closed and floated (F1.22 behavior 1).</summary>
    public static DashboardDocument CreateDashboard() =>
        new()
        {
            Id = PanelKinds.Dashboard,
            CanClose = true,
            CanFloat = true,
            CanPin = false,
        };

    private ToolDock Side(string id, Alignment alignment, double proportion) =>
        new()
        {
            Id = id,
            Proportion = proportion,
            Alignment = alignment,
            VisibleDockables = CreateList<IDockable>(),
        };
}
