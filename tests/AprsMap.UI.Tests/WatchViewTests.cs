// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Map;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Dock.Model.Core;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Extra map views (docs/features/F1.16-extra-map-views.md).</summary>
public sealed class WatchViewTests
{
    private static ShellViewModel NewShell() =>
        new(new object(), new Localizer(CultureInfo.GetCultureInfo("en-US")), managedWindows: false);

    private static IReadOnlyList<WatchDocument> Views(ShellViewModel shell) =>
        [.. ShellViewModel.Dockables(shell.Layout).OfType<WatchDocument>()];

    [AvaloniaFact]
    public void AViewOpensBesideTheMapAndUpToThreeCanBeOpen()
    {
        var shell = NewShell();
        var changes = 0;
        shell.WatchedChanged += (_, _) => changes++;

        Assert.True(shell.Watch("N0AAA"));
        var view = Assert.Single(Views(shell));
        Assert.Equal(("N0AAA", "N0AAA", true), (view.Callsign, view.Title, view.CanFloat));
        var dock = Assert.IsAssignableFrom<IDock>(view.Owner);
        Assert.Equal(ShellViewModel.WatchDock, dock.Id);
        Assert.Contains(ShellViewModel.Dockables(shell.Layout), d => d is MapDocument); // the map stays in view

        Assert.True(shell.Watch("n0aaa")); // the same station: its view, not another
        Assert.True(shell.Watch("N0BBB"));
        Assert.True(shell.Watch("N0CCC"));
        Assert.False(shell.Watch("N0DDD"));
        Assert.Equal(["N0AAA", "N0BBB", "N0CCC"], shell.Watched);
        Assert.Equal(3, changes);
    }

    [AvaloniaFact]
    public void ClosingAViewTakesItsStationOutAndViewsComeBackWithTheLayout()
    {
        var shell = NewShell();
        shell.Watch("N0AAA");
        shell.Watch("N0BBB");
        var changed = false;
        shell.WatchedChanged += (_, _) => changed = true;

        shell.Factory.CloseDockable(Views(shell).First(v => v.Callsign == "N0AAA"));

        Assert.True(changed);
        Assert.Equal(["N0BBB"], shell.Watched);
        var restored = NewShell();
        restored.Restore(shell.Save(), []);
        Assert.Equal(["N0BBB"], restored.Watched);
    }

    [AvaloniaFact]
    public void WatchingReportsTheStationForTheFilterAndTooManySaysSo()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        foreach (var name in new[] { "N0AAA", "N0BBB", "N0CCC" })
        {
            vm.Watch(name);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["N0AAA", "N0BBB", "N0CCC"], engine.MapViews[^1].Watched);
        vm.Watch("N0DDD");

        Assert.Equal("Up to 3 extra map views: close one to watch another station.", vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task AViewFollowsItsStationUntilEscapeAndFollowsAgainWithL()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(new MapTests.FakeTiles(), null, new FakeTimeProvider()),
        };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        vm.Apply(new StationBatch([new StationState("N0AAA", DateTimeOffset.UnixEpoch, 40.0, -105.0, "/>")], []));
        var panel = new WatchPanel(vm, "N0AAA");
        var window = new Window
        {
            Content = panel,
            Width = 600,
            Height = 400,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((40.0, -105.0), Centre(panel));
        Assert.Equal("N0AAA", panel.View.Followed); // its track in the followed style

        vm.Apply(new StationBatch([new StationState("N0AAA", DateTimeOffset.UnixEpoch, 40.2, -105.1, "/>")], []));
        Assert.Equal((40.2, -105.1), Centre(panel));

        panel.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.False(panel.IsFollowing);
        vm.Apply(new StationBatch([new StationState("N0AAA", DateTimeOffset.UnixEpoch, 40.4, -105.2, "/>")], []));
        Assert.Equal((40.2, -105.1), Centre(panel)); // stays where it was

        panel.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.L });
        Assert.True(panel.IsFollowing);
        Assert.Equal((40.4, -105.2), Centre(panel));
        window.Close();
    }

    private static (double, double) Centre(WatchPanel panel) =>
        panel.View.View is var (lat, lon, _) ? (Math.Round(lat, 3), Math.Round(lon, 3)) : (double.NaN, double.NaN);
}
