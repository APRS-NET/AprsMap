// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Gps;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The GPS panel (docs/features/F3.12-gps-status.md, acceptance tests 3, 4).</summary>
public sealed class GpsPanelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    internal static readonly GpsStatus Status = new(
        "gps",
        GpsFixKind.Fix3D,
        T0,
        true,
        49.0583,
        -72.0292,
        545.4,
        22.4,
        84.4,
        9,
        0.9,
        new GpsAccuracy(3, 6, 2.5, 0.9, 2.1, 3.0, 4.5),
        [
            new GpsSkySatellite(GnssConstellation.Gps, 4, 1, 45, 120, 42, true),
            new GpsSkySatellite(GnssConstellation.Gps, 4, 5, 45, 120, 47, true), // L5: the stronger signal
            new GpsSkySatellite(GnssConstellation.Gps, 12, null, 5, 300, null, false),
            new GpsSkySatellite(GnssConstellation.Glonass, 67, null, 20, 200, 30, true),
            new GpsSkySatellite(GnssConstellation.Galileo, 11, null, 70, 10, 45, true),
            new GpsSkySatellite(GnssConstellation.BeiDou, 21, null, 35, 60, 25, false),
            new GpsSkySatellite(GnssConstellation.Sbas, 46, null, 30, 160, 33, false),
        ]
    );

    private static GpsPanelViewModel Create(FakeTimeProvider? time = null, List<string>? opened = null) =>
        new(
            Localizer.Instance,
            time ?? new FakeTimeProvider(T0.AddSeconds(12)),
            id =>
            {
                opened?.Add(id);
                return Task.CompletedTask;
            }
        );

    [Fact]
    public void ItSaysTheFixTheAccuracyAndTheSatellitesStrongestFirst()
    {
        var opened = new List<string>();
        var gps = Create(opened: opened);

        gps.Show(Status, UnitSettings.Metric);

        Assert.Equal("3D fix", gps.FixText);
        Assert.Equal("12 seconds ago", gps.AgeText);
        Assert.Equal("±3.0 m across, ±4.5 m up", gps.ErrorText);
        Assert.Equal("PDOP 2.5 · HDOP 0.9 · VDOP 2.1", gps.DopText);
        Assert.Equal("3 of 6 satellites used", gps.SatellitesText);
        Assert.Equal(6, gps.Sky!.Count); // satellite 4 once, on its stronger signal
        Assert.Equal(["4", "11", "46", "67", "21", "12"], gps.Satellites.Select(r => r.IdText));
        Assert.Equal("47", gps.Satellites[0].SignalText);
        Assert.Equal("–", gps.Satellites[^1].SignalText); // seen, not tracked
        gps.OpenConsoleCommand.Execute(null);
        Assert.Equal(["gps"], opened);
    }

    [Fact]
    public void WithoutAnErrorEstimateTheDopsSayHowGoodItIs()
    {
        var gps = Create();

        gps.Show(Status with { Accuracy = new GpsAccuracy(Hdop: 1.2), Sky = null }, UnitSettings.Metric);

        Assert.Null(gps.ErrorText);
        Assert.Equal("HDOP 1.2", gps.DopText);
        Assert.Equal("9 satellites used", gps.SatellitesText);
        Assert.Empty(gps.Satellites);
    }

    [Fact]
    public void WithoutAGpsItSaysHowToAddOne()
    {
        var gps = Create();
        gps.Show(Status, UnitSettings.Metric);

        gps.Show(null, UnitSettings.Metric);

        Assert.False(gps.HasGps);
        Assert.False(gps.OpenConsoleCommand.CanExecute(null));
    }

    [Fact]
    public void AnOldFixSaysSo()
    {
        var gps = Create();
        gps.Show(Status with { IsFresh = false }, UnitSettings.Metric);
        Assert.Equal("Fix too old to use: the profile's location is used", gps.FixText);
    }

    [Fact]
    public void ASatelliteIsPlacedByAzimuthAndElevation()
    {
        var centre = new Point(100, 100);
        Assert.Equal(new Point(100, 100), SkyView.Place(centre, 90, 90, 0)); // straight up: the centre
        var east = SkyView.Place(centre, 90, 0, 90);
        Assert.Equal((190, 100), (Math.Round(east.X), Math.Round(east.Y))); // on the horizon, east
        var north = SkyView.Place(centre, 90, 30, 0);
        Assert.Equal((100, 40), (Math.Round(north.X), Math.Round(north.Y))); // 30° up, north: two thirds out
    }

    [AvaloniaFact]
    public void TheGpsPillOpensThePanel()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider(T0));
        vm.Show(engine.Settings);
        var panel = new GpsPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 420,
            Height = 1000,
        };
        window.Show();
        vm.OpenGps();
        vm.Show(engine.Status with { Gps = Status });
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.Gps.HasGps);
        var sky = window.GetVisualDescendants().OfType<SkyView>().Single();
        Assert.Equal(6, sky.Satellites!.Count);
        Assert.True(sky.Bounds.Width > 0 && sky.Bounds.Width == sky.Bounds.Height);
        window.CaptureRenderedFrame()?.Dispose(); // draws
        window.Close();
    }

    [Fact]
    public void OnlyAGpsPillOffersGpsStatus()
    {
        var opened = 0;
        var pill = new SourcePill(
            "gps",
            Localizer.Instance,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            openGps: () => opened++
        );
        pill.Show(new SourceStatus("gps", "GPS", SourceKind.Gps, ConnectionState.Connected), true, gps: Status);
        Assert.True(pill.IsGps);
        pill.GpsStatusCommand.Execute(null);
        Assert.Equal(1, opened);

        pill.Show(new SourceStatus("gps", "Direwolf", SourceKind.KissTcp, ConnectionState.Connected), true);
        Assert.False(pill.IsGps);
    }
}
