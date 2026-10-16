// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Map;
using AprsMap.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;

namespace AprsMap.UI.Tests;

/// <summary>Tracks on the map (docs/features/F1.5-tracks.md, behaviors 5–10; acceptance tests 3 and 4).</summary>
public sealed class TrackDrawingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TrackColours Colours = new(SKColors.Purple, SKColors.Blue, SKColors.White);

    private static TrackPoint[] Line(int count, double fromLatitude = 40) =>
        [.. Enumerable.Range(0, count).Select(i => new TrackPoint(fromLatitude + (i * 0.01), -105, T0.AddMinutes(i)))];

    private static Mapsui.Viewport Around(double latitude, double longitude, double resolution)
    {
        var (x, y) = Mapsui.Projections.SphericalMercator.FromLonLat(longitude, latitude);
        return new Mapsui.Viewport(x, y, resolution, 0, 800, 600);
    }

    [Fact]
    public void UpdatesApplyAsResetThenAddedThenDropped()
    {
        var points = new List<int> { 1, 2, 3 };

        TrackUpdates.Apply(points, new TrackUpdate("A", [Point(4), Point(5)], Dropped: 2), p => (int)p.Latitude);
        Assert.Equal([3, 4, 5], points);

        TrackUpdates.Apply(points, new TrackUpdate("A", [Point(9)], Reset: true), p => (int)p.Latitude);
        Assert.Equal([9], points);

        static TrackPoint Point(int n) => new(n, 0, T0);
    }

    [Fact]
    public void ATrackInViewIsDrawnAndOneOutOfViewIsNot()
    {
        var layer = new TrackLayer();
        layer.Apply(new TrackUpdate("NEAR", Line(5), Reset: true));
        layer.Apply(new TrackUpdate("FAR", Line(5, fromLatitude: -30), Reset: true));

        using var frame = layer.Build(Around(40.02, -105, 20), _ => (TrackStyle.Other, false), Colours);

        Assert.Equal(4, frame.Segments); // NEAR's four, none of FAR's
    }

    [Fact]
    public void PointsWithinAPixelAreMergedAtAWideZoom()
    {
        var layer = new TrackLayer();
        layer.Apply(new TrackUpdate("A", Line(200), Reset: true));

        using var close = layer.Build(Around(41, -105, 20), _ => (TrackStyle.Other, false), Colours);
        using var wide = layer.Build(Around(41, -105, 20_000), _ => (TrackStyle.Other, false), Colours);

        Assert.True(wide.Segments < close.Segments / 10);
        Assert.True(wide.Segments >= 1); // the last point is always kept
    }

    [Fact]
    public void OnlyTheSelectedTrackHasDotsAtItsPoints()
    {
        var layer = new TrackLayer();
        layer.Apply(new TrackUpdate("SEL", Line(3), Reset: true));
        layer.Apply(new TrackUpdate("OTHER", Line(3, 40.005), Reset: true));

        using var frame = layer.Build(
            Around(40.01, -105, 5),
            name => (name == "SEL" ? TrackStyle.Selected : TrackStyle.Other, false),
            Colours
        );

        Assert.Equal(3, frame.Dots.Count);
    }

    [Fact]
    public void ATrackWithOnePointDrawsNothing()
    {
        var layer = new TrackLayer();
        layer.Apply(new TrackUpdate("A", Line(1), Reset: true));

        using var frame = layer.Build(Around(40, -105, 20), _ => (TrackStyle.Selected, false), Colours);

        Assert.Equal(0, frame.Segments);
        Assert.Empty(frame.Dots);
    }

    [Fact]
    public void ATrackIsForgottenWhenItsLastPointGoes()
    {
        var layer = new TrackLayer();
        layer.Apply(new TrackUpdate("A", Line(2), Reset: true));

        layer.Apply(new TrackUpdate("A", [], Dropped: 2));

        Assert.Equal(0, layer.Count);
    }

    [AvaloniaFact]
    public void TheSettingDecidesWhichTracksAreDrawn()
    {
        using var view = new MapView();
        view.Apply(
            new StationBatch(
                [
                    new StationState("SEL", T0, 40.04, -105, "/>"),
                    new StationState("FOL", T0, 40.04, -104.99, "/>"),
                    new StationState("ANY", T0, 40.04, -104.98, "/>"),
                ],
                [],
                [
                    new TrackUpdate("SEL", Line(5), Reset: true),
                    new TrackUpdate("FOL", Line(5), Reset: true),
                    new TrackUpdate("ANY", Line(5), Reset: true),
                ]
            )
        );
        view.Select("SEL");
        view.Followed = "FOL";
        var viewport = Around(40.02, -105, 20);

        Assert.Equal(12, Drawn(MapTracks.All));
        Assert.Equal(8, Drawn(MapTracks.SelectedAndFollowed));
        Assert.Equal(0, Drawn(MapTracks.None));

        int Drawn(MapTracks setting)
        {
            view.Tracks = setting;
            using var frame = view.TrackLayer.Build(viewport, view.TrackStyleFor, Colours);
            return frame.Segments;
        }
    }

    [AvaloniaFact]
    public void ARemovedStationTakesItsTrack()
    {
        using var view = new MapView();
        view.Apply(
            new StationBatch(
                [new StationState("A", T0, 40, -105, "/>")],
                [],
                [new TrackUpdate("A", Line(3), Reset: true)]
            )
        );

        view.Apply(new StationBatch([], ["A"]));

        Assert.Equal(0, view.TrackLayer.Count);
    }

    [AvaloniaFact]
    public async Task APanelShownAgainGetsTheTracksHeldAndClearTrackAsksTheEngine()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(null, null, new FakeTimeProvider()),
        };
        vm.Show(engine.Settings);
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        vm.Apply(
            new StationBatch(
                [new StationState("N0AAA", T0, 40.04, -105, "/>")],
                [],
                [new TrackUpdate("N0AAA", Line(5), Reset: true)]
            )
        );

        using var panel = new MapPanel { DataContext = vm };
        var window = new Window { Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5, panel.View.TrackLayer.PointsOf("N0AAA").Count);

        vm.SelectedStation = "N0AAA";
        await WaitUntil(() => vm.Details.CanClearTrack);
        await vm.Details.ClearTrackCommand.ExecuteAsync(null);
        Assert.Equal(["N0AAA"], engine.ClearedTracks);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AReadOnlyViewerIsntOfferedClearTrack()
    {
        var engine = new FakeEngine { Settings = FakeEngine.TwoProfiles() with { ReadOnly = true } };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Apply(
            new StationBatch(
                [new StationState("N0AAA", T0, 40.04, -105, "/>")],
                [],
                [new TrackUpdate("N0AAA", Line(5), Reset: true)]
            )
        );

        vm.SelectedStation = "N0AAA";
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.False(vm.Details.CanClearTrack);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
