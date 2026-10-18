// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text.Json.Nodes;
using AprsMap.UI.Localization;
using AprsMap.UI.Shell;
using Avalonia;
using Avalonia.Headless.XUnit;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace AprsMap.UI.Tests;

/// <summary>The shell's layout: default, saved and restored (docs/features/F1.0-shell-and-docking.md).</summary>
public sealed class ShellTests
{
    private static readonly ScreenArea Laptop = WindowPlacementTests.Laptop;

    private static ShellViewModel NewShell() =>
        new(new object(), new Localizer(CultureInfo.GetCultureInfo("en-US")), managedWindows: false);

    [AvaloniaFact]
    public void TheDefaultLayoutHasTheMapInTheMiddleAndPanelsAround()
    {
        var shell = NewShell();

        Assert.Equal("Stations", Dock(shell, ShellFactory.LeftDock).Single().Title);
        Assert.Equal(["Details", "Messages"], Dock(shell, ShellFactory.RightDock).Select(p => p.Title)); // tabs (F2.4)
        Assert.Equal(["Packets", "Console"], Dock(shell, ShellFactory.BottomDock).Select(d => d.Title)); // F1.28 behind Packets
        var map = Assert.IsType<MapDocument>(Find(shell.Layout, PanelKinds.Map));
        Assert.Equal("Map", map.Title);
        Assert.False(map.CanClose);
        Assert.False(map.CanFloat);
    }

    [AvaloniaFact]
    public void APanelCanBeMovedWithoutDragging()
    {
        var shell = NewShell();
        var packets = Find(shell.Layout, PanelKinds.Packets);
        var left = (IDock)Find(shell.Layout, ShellFactory.LeftDock);
        var changes = 0;
        shell.Changed += (_, _) => changes++;

        shell.Factory.MoveDockable((IDock)packets.Owner!, left, packets, null);

        Assert.Same(left, packets.Owner);
        Assert.True(changes > 0);
    }

    [AvaloniaFact]
    public void AChangedLayoutWithAFloatingPanelIsRestoredOnScreen()
    {
        var shell = NewShell();
        var details = Find(shell.Layout, PanelKinds.Details);
        shell.Factory.MoveDockable(
            (IDock)details.Owner!,
            (IDock)Find(shell.Layout, ShellFactory.BottomDock),
            details,
            null
        );
        shell.Factory.FloatDockable(Find(shell.Layout, PanelKinds.Stations));
        var window = Assert.Single(shell.Layout.Windows!);
        window.X = 3000; // on a monitor that won't be there next time
        window.Y = 100;
        window.Width = 500;
        window.Height = 400;
        var saved = shell.Save(new MainWindowPlacement(new PixelPoint(10, 20), new Size(1200, 700), false));

        var restored = NewShell();
        Assert.Equal(LayoutRestore.Restored, restored.Restore(saved, [Laptop]));

        Assert.Contains(Dock(restored, ShellFactory.BottomDock), d => d.Id == PanelKinds.Details);
        var floating = Assert.Single(restored.Layout.Windows!);
        Assert.True(Laptop.WorkingArea.Contains(new PixelPoint((int)floating.X, (int)floating.Y)));
        Assert.Equal("Stations", ShellViewModel.Dockables(floating.Layout!).OfType<PanelTool>().Single().Title);
        Assert.Equal(
            new MainWindowPlacement(new PixelPoint(10, 20), new Size(1200, 700), false),
            LayoutFile.ReadMainWindow(saved)
        );
    }

    [AvaloniaFact]
    public void AClosedPanelStaysClosedAfterARestart()
    {
        var shell = NewShell();
        shell.Factory.CloseDockable(Find(shell.Layout, PanelKinds.Stations));

        var restored = NewShell();
        restored.Restore(shell.Save(), []);

        Assert.DoesNotContain(ShellViewModel.Dockables(restored.Layout), d => d.Id == PanelKinds.Stations);
    }

    [AvaloniaFact]
    public void APanelThisVersionDoesntKnowIsDroppedAndANewOneAppearsInItsPlace()
    {
        var file = JsonNode.Parse(NewShell().Save())!.AsObject();
        // Saved by a version without the Details panel, and with a panel this one doesn't have.
        file["panels"] = new JsonArray("stations", "packets", "weather");
        var text = file.ToJsonString()
            .Replace("\"Kind\":\"details\"", "\"Kind\":\"weather\"", StringComparison.Ordinal);
        Assert.Contains("weather", text, StringComparison.Ordinal);

        var shell = NewShell();
        Assert.Equal(LayoutRestore.Restored, shell.Restore(text, []));

        Assert.DoesNotContain(ShellViewModel.Dockables(shell.Layout).OfType<PanelTool>(), p => p.Kind == "weather");
        Assert.Contains(Dock(shell, ShellFactory.RightDock), d => d.Id == PanelKinds.Details);
    }

    [AvaloniaTheory]
    [InlineData("{ not json")]
    [InlineData("{\"format\":99,\"panels\":[],\"dock\":{}}")]
    [InlineData("{\"format\":1,\"panels\":[]}")]
    public void AnUnreadableLayoutGivesTheDefault(string text)
    {
        var shell = NewShell();

        Assert.Equal(LayoutRestore.Unreadable, shell.Restore(text, []));

        Assert.IsType<MapDocument>(Find(shell.Layout, PanelKinds.Map));
    }

    [AvaloniaFact]
    public void ALayoutWithoutTheMapIsUnreadable()
    {
        var text = NewShell().Save().Replace("\"Id\":\"map\"", "\"Id\":\"gone\"", StringComparison.Ordinal);
        text = text.Replace("MapDocument", "Document", StringComparison.Ordinal);

        Assert.Equal(LayoutRestore.Unreadable, NewShell().Restore(text, []));
    }

    [AvaloniaFact]
    public void AHandEditedFileCantMakeTheMapClosable()
    {
        var text = NewShell().Save();
        var shell = NewShell();

        shell.Restore(text.Replace("\"CanClose\":false", "\"CanClose\":true", StringComparison.Ordinal), []);

        Assert.False(Find(shell.Layout, PanelKinds.Map).CanClose);
    }

    [AvaloniaFact]
    public void ResetGoesBackToTheDefault()
    {
        var shell = NewShell();
        shell.Factory.CloseDockable(Find(shell.Layout, PanelKinds.Packets));

        shell.Reset();

        Assert.Single(Dock(shell, ShellFactory.BottomDock), d => d.Id == PanelKinds.Packets);
    }

    [AvaloniaFact]
    public void PanelTitlesFollowTheLanguage()
    {
        var text = new Localizer(CultureInfo.GetCultureInfo("en-US"));
        var shell = new ShellViewModel(new object(), text, managedWindows: false);

        text.SetLanguage(Localizer.PseudoLanguage);

        Assert.Equal(PseudoLocalizer.Transform("Packets"), Find(shell.Layout, PanelKinds.Packets).Title);
    }

    [AvaloniaFact]
    public void MoveToTakesAPanelToAnotherDockFromItsMenu()
    {
        var shell = NewShell();
        var details = Find(shell.Layout, PanelKinds.Details);
        var messages = Find(shell.Layout, PanelKinds.Messages);

        shell.Factory.MoveToBottom.Execute(details);
        shell.Factory.MoveToBottom.Execute(messages);

        Assert.Equal(ShellFactory.BottomDock, details.Owner!.Id);
        Assert.Same(messages, ((IDock)messages.Owner!).ActiveDockable);
        Assert.DoesNotContain(
            ShellViewModel.Dockables(shell.Layout).OfType<IToolDock>(),
            d => d.Id == ShellFactory.RightDock && d.VisibleDockables!.Count > 0
        );
    }

    [AvaloniaFact]
    public void MovingIntoADockThatCollapsedCreatesItBesideTheMap()
    {
        var shell = NewShell();
        var stations = Find(shell.Layout, PanelKinds.Stations);
        shell.MoveTo(stations, ShellFactory.BottomDock); // the left dock empties and collapses
        Assert.DoesNotContain(ShellViewModel.Dockables(shell.Layout), d => d.Id == ShellFactory.LeftDock);

        shell.Factory.MoveToLeft.Execute(stations);

        Assert.Equal(ShellFactory.LeftDock, stations.Owner!.Id);
    }

    [AvaloniaFact]
    public void AFloatingPanelCanBeDockedAgainByMenu()
    {
        var shell = NewShell();
        var packets = Find(shell.Layout, PanelKinds.Packets);
        shell.Factory.FloatDockable(packets);
        Assert.Single(shell.Layout.Windows!);

        shell.MoveTo(packets, ShellFactory.RightDock);

        Assert.Equal(ShellFactory.RightDock, packets.Owner!.Id);
    }

    [AvaloniaFact]
    public void TheFloatingDashboardCanBeDockedBesideTheMapAgainFromItsTabMenu()
    {
        var shell = NewShell();
        var dashboard = Find(shell.Layout, PanelKinds.Dashboard);
        Assert.True(
            (bool)ShellConverters.IsDocked.Convert(dashboard, typeof(bool), null, CultureInfo.InvariantCulture)!
        );
        shell.Factory.FloatDockable(dashboard);
        Assert.True(
            (bool)ShellConverters.IsFloating.Convert(dashboard, typeof(bool), null, CultureInfo.InvariantCulture)!
        );
        var changed = 0;
        shell.Changed += (_, _) => changed++;

        shell.Factory.DockBesideMap.Execute(dashboard);

        var centre = (IDock)Find(shell.Layout, ShellFactory.CentreDock);
        Assert.Same(centre, dashboard.Owner);
        Assert.Same(dashboard, centre.ActiveDockable);
        Assert.Contains(centre.VisibleDockables!, d => d.Id == PanelKinds.Map);
        Assert.False(
            (bool)ShellConverters.IsFloating.Convert(dashboard, typeof(bool), null, CultureInfo.InvariantCulture)!
        );
        Assert.DoesNotContain(
            shell.Layout.Windows ?? [],
            w => ShellViewModel.Dockables(w.Layout!).Any(d => d.Id == PanelKinds.Dashboard)
        );
        Assert.NotEqual(0, changed);
    }

    [AvaloniaFact]
    public void AnAutoHiddenPanelCanBeMoved()
    {
        var shell = NewShell();
        var stations = Find(shell.Layout, PanelKinds.Stations);
        shell.Factory.PinDockable(stations);

        shell.MoveTo(stations, ShellFactory.RightDock);

        Assert.Equal(ShellFactory.RightDock, stations.Owner!.Id);
        Assert.DoesNotContain(stations, shell.Layout.LeftPinnedDockables ?? []);
    }

    [AvaloniaFact]
    public void AClosedPanelCanBeShownAgainInItsDefaultPlace()
    {
        var shell = NewShell();
        shell.Factory.CloseDockable(Find(shell.Layout, PanelKinds.Stations));
        Assert.Equal([PanelKinds.Stations], shell.ClosedPanels);

        shell.Show(PanelKinds.Stations);

        Assert.Empty(shell.ClosedPanels);
        var stations = Find(shell.Layout, PanelKinds.Stations);
        Assert.Equal(ShellFactory.LeftDock, stations.Owner!.Id);
        Assert.Equal("Stations", stations.Title);
        Assert.NotNull(stations.Context);
    }

    [AvaloniaFact]
    public void GatherBringsFloatingWindowsToTheMainScreen() => NewShell().Gather(WindowPlacementTests.Laptop); // no open host windows in a test: nothing to move, no error

    [AvaloniaFact]
    public void NoSavedLayoutMeansTheDefault() => Assert.Equal(LayoutRestore.Default, NewShell().Restore(null, []));

    private static IDockable Find(IRootDock root, string id) => ShellViewModel.Dockables(root).First(d => d.Id == id);

    private static IList<IDockable> Dock(ShellViewModel shell, string id) =>
        ((IDock)Find(shell.Layout, id)).VisibleDockables!;
}
