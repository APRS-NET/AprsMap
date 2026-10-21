// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text.Json.Nodes;
using AprsMap.UI.Localization;
using AprsMap.UI.Shell;
using Avalonia.Headless.XUnit;
using Dock.Model.Core;

namespace AprsMap.UI.Tests;

/// <summary>The dashboard's tab beside the map (docs/features/F1.22-statistics.md, behavior 1).</summary>
public sealed class DashboardShellTests
{
    private static ShellViewModel NewShell() =>
        new(new object(), new Localizer(CultureInfo.GetCultureInfo("en-US")), managedWindows: false);

    private static IDockable Find(ShellViewModel shell, string id) =>
        ShellViewModel.Dockables(shell.Layout).First(d => d.Id == id);

    [AvaloniaFact]
    public void TheDashboardIsATabBesideTheMapWhichStaysInFront()
    {
        var shell = NewShell();

        var dashboard = Assert.IsType<DashboardDocument>(Find(shell, PanelKinds.Dashboard));
        var centre = Assert.IsAssignableFrom<IDock>(dashboard.Owner);
        Assert.Equal(ShellFactory.CentreDock, centre.Id);
        Assert.Equal([PanelKinds.Map, PanelKinds.Dashboard], centre.VisibleDockables!.Select(d => d.Id));
        Assert.Equal(PanelKinds.Map, centre.ActiveDockable!.Id);
        Assert.Equal("Dashboard", dashboard.Title);
        Assert.True(dashboard.CanClose);
    }

    [AvaloniaFact]
    public void AClosedDashboardIsOfferedFirstAndComesBackAsATab()
    {
        var shell = NewShell();
        shell.Factory.CloseDockable(Find(shell, PanelKinds.Dashboard));
        shell.Factory.CloseDockable(Find(shell, PanelKinds.Stations));
        Assert.Equal([PanelKinds.Dashboard, PanelKinds.Stations, PanelKinds.Gps], shell.ClosedPanels); // GPS on demand

        shell.Show(PanelKinds.Dashboard);

        var dashboard = Find(shell, PanelKinds.Dashboard);
        Assert.Equal(ShellFactory.CentreDock, dashboard.Owner!.Id);
        Assert.Same(dashboard, ((IDock)dashboard.Owner).ActiveDockable); // shown, not left behind the map
        Assert.Equal("Dashboard", dashboard.Title);
        Assert.Equal([PanelKinds.Stations, PanelKinds.Gps], shell.ClosedPanels);
    }

    [AvaloniaFact]
    public void AClosedDashboardStaysClosedAfterARestart()
    {
        var shell = NewShell();
        shell.Factory.CloseDockable(Find(shell, PanelKinds.Dashboard));

        var restored = NewShell();
        restored.Restore(shell.Save(), []);

        Assert.DoesNotContain(ShellViewModel.Dockables(restored.Layout), d => d is DashboardDocument);
    }

    [AvaloniaFact]
    public void ALayoutSavedBeforeTheDashboardGainsItBehindTheMap()
    {
        var old = NewShell();
        old.Factory.CloseDockable(Find(old, PanelKinds.Dashboard));
        var file = JsonNode.Parse(old.Save())!.AsObject();
        file["panels"] = new JsonArray("stations", "details", "packets"); // what a version without it knew

        var shell = NewShell();
        Assert.Equal(LayoutRestore.Restored, shell.Restore(file.ToJsonString(), []));

        var dashboard = Assert.IsType<DashboardDocument>(Find(shell, PanelKinds.Dashboard));
        Assert.Equal(ShellFactory.CentreDock, dashboard.Owner!.Id);
        Assert.Equal(PanelKinds.Map, ((IDock)dashboard.Owner).ActiveDockable!.Id);
    }
}
