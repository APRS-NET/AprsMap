// SPDX-License-Identifier: AGPL-3.0-only
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace AprsMap.UI.Shell;

/// <summary>
/// Builds the default layout and re-attaches view models to panels after a saved layout is loaded. Floating panels
/// open as OS windows on desktop, and inside the page (managed windows) in the browser.
/// </summary>
public sealed class ShellFactory(object context, bool managedWindows) : Factory
{
    public override IRootDock CreateLayout()
    {
        var map = new MapDocument
        {
            Id = "map",
            Title = "Map",
            CanClose = false,
            CanFloat = false,
        };
        var packets = Panel("packets", "packets", "Packets");
        var stations = Panel("stations", "stations", "Stations");
        var details = Panel("details", "details", "Details");
        var messages = Panel("messages", "messages", "Messages");
        var dm1 = Panel("dm:W1AW-9", "dm", "W1AW-9");
        var dm2 = Panel("dm:K1ABC", "dm", "K1ABC");

        var centre = new DocumentDock
        {
            Id = "centre",
            IsCollapsable = false,
            CanCreateDocument = false,
            VisibleDockables = CreateList<IDockable>(map),
            ActiveDockable = map,
        };
        var right = new ToolDock
        {
            Id = "right",
            Proportion = 0.28,
            Alignment = Alignment.Right,
            VisibleDockables = CreateList<IDockable>(details, messages, dm1, dm2),
            ActiveDockable = details,
        };
        var left = new ToolDock
        {
            Id = "left",
            Proportion = 0.2,
            Alignment = Alignment.Left,
            VisibleDockables = CreateList<IDockable>(stations),
            ActiveDockable = stations,
        };
        var bottom = new ToolDock
        {
            Id = "bottom",
            Proportion = 0.3,
            Alignment = Alignment.Bottom,
            VisibleDockables = CreateList<IDockable>(packets),
            ActiveDockable = packets,
        };
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

    private static PanelTool Panel(string id, string kind, string title) =>
        new()
        {
            Id = id,
            Kind = kind,
            Title = title,
            CanClose = kind == "dm",
        };
}
