// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Map;
using AprsMap.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mapsui.Projections;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Dead reckoning (docs/features/F1.17-dead-reckoning.md, acceptance tests 1, 2, 4).</summary>
public sealed class DeadReckoningTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static StationMark Moving(double kmh, int course = 90, bool stale = false) =>
        StationMark.From(
            new StationState("N0CALL-9", T0, 40, -105, "/>", IsStale: stale, Course: course, SpeedKmh: kmh)
        );

    [Fact]
    public void NinetyKmhEastForTwoMinutesIsThreeKilometresEast()
    {
        var (x, y, left) = MapView.Estimate(Moving(90), T0.AddMinutes(2))!.Value;

        var (lon, lat) = SphericalMercator.ToLonLat(x, y);
        var (lat2, lon2) = MapView.PointAlong(40, -105, 90, 3); // 3 km east
        Assert.Equal(lat2, lat, 6);
        Assert.Equal(lon2, lon, 6);
        var km = (lon - -105) * Math.PI / 180 * 6371.0088 * Math.Cos(40 * Math.PI / 180);
        Assert.InRange(km, 2.97, 3.03); // within 1 %
        Assert.Equal(0.8, left, 3); // 8 of the 10 minutes left
    }

    [Theory]
    [InlineData(4.9, 2, false)] // too slow: the course means little
    [InlineData(90, 11, false)] // more than 10 minutes since
    [InlineData(90, 2, true)] // stale
    public void NoEstimateWhenSlowOldOrStale(double kmh, int minutes, bool stale)
    {
        Assert.Null(MapView.Estimate(Moving(kmh, stale: stale), T0.AddMinutes(minutes)));
    }

    [AvaloniaFact]
    public void TheMapDrawsAndPicksTheStationWhereItProbablyIsWhenOn()
    {
        var time = new FakeTimeProvider(T0.AddMinutes(2));
        using var view = new MapView { Time = time };
        var window = new Window
        {
            Content = view,
            Width = 800,
            Height = 600,
        };
        window.Show();
        view.SetStations([new StationState("N0CALL-9", T0, 40, -105, "/>", Course: 90, SpeedKmh: 90)]);
        view.ShowAround(40, -105, 20);
        Dispatcher.UIThread.RunJobs();
        var heard = view.ScreenPoint(40, -105);
        var (lat, lon) = MapView.PointAlong(40, -105, 90, 3);
        var probably = view.ScreenPoint(lat, lon);

        Assert.Equal("N0CALL-9", view.HitTest(heard)?.Name); // off by default: where it was heard

        view.DeadReckoning = true;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose(); // a frame works the estimate out
        Assert.Equal("N0CALL-9", view.HitTest(probably)?.Name);
        Assert.Null(view.HitTest(heard));
        window.Close();
    }

    [Fact]
    public async Task DetailsSayWhereItProbablyIsWhileDeadReckoningIsOn()
    {
        var time = new FakeTimeProvider(T0.AddMinutes(2));
        var engine = new FakeEngine();
        engine.Details["N0CALL-9"] = FakeEngine.Detail("N0CALL-9", T0); // 66.7 km/h on course 88
        var appearance = new Appearance.AppearanceService();
        var vm = new MainViewModel(engine, time, appearance: appearance);
        await vm.Details.ShowAsync("N0CALL-9");
        Assert.Null(vm.Details.EstimateText); // off by default (F1.17 decision 1)

        appearance.Apply(appearance.Current with { DeadReckoning = true });

        Assert.Equal("Probably 2.2 km on along its course, if it kept going", vm.Details.EstimateText);
        time.Advance(TimeSpan.FromMinutes(9));
        vm.Details.Refresh();
        Assert.Null(vm.Details.EstimateText); // past 10 minutes
    }
}
