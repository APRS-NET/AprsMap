// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Map;
using AprsMap.UI.ViewModels;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;

namespace AprsMap.UI.Tests;

/// <summary>The selected packet's route (docs/features/F1.21-rf-paths.md, behavior 2).</summary>
public sealed class RouteTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly PacketRoute Direct = new(
        "N0CALL-9",
        ArrivalKind.Direct,
        49,
        -72,
        [new RouteHop("N0GATE-1", true, 49.1667, -72, 18.53)]
    );

    private static readonly PacketRoute Digipeated = new(
        "N0CALL-9",
        ArrivalKind.Digipeated,
        49,
        -72,
        [new RouteHop("N0DIGI-1", false, 49, -71.8667, 9.7), new RouteHop("N0GATE-1", true, 49.1667, -72, 21.2)]
    );

    private static SKPoint Screen(double x, double y) => new((float)(x / 100), (float)(-y / 100));

    [Fact]
    public void ADirectRouteIsOneDottedLegWithItsDistance()
    {
        using var frame = RouteFrame.Build(Direct, Screen, 2, SKColors.Blue, SKColors.White)!;

        Assert.Equal((0, 1), frame.Legs);
        Assert.Equal(18.53, frame.DirectLabel!.Value.Km);
    }

    [Fact]
    public void ADigipeatedRouteHasArrowsThenADottedLastLeg()
    {
        using var frame = RouteFrame.Build(Digipeated, Screen, 2, SKColors.Blue, SKColors.White)!;

        Assert.Equal((1, 1), frame.Legs);
        Assert.Null(frame.DirectLabel);
    }

    [Fact]
    public void TheLineStopsAtTheFirstHopWithNoPosition()
    {
        var route = Digipeated with { Hops = [new RouteHop("N0NONE-1", false), Digipeated.Hops[1]] };

        Assert.Null(RouteFrame.Build(route, Screen, 2, SKColors.Blue, SKColors.White));
    }

    [Fact]
    public void ARouteWithNoHopsOrNoPositionDrawsNothing()
    {
        Assert.Null(RouteFrame.Build(Direct with { Hops = [] }, Screen, 2, SKColors.Blue, SKColors.White));
        Assert.Null(RouteFrame.Build(Direct with { Latitude = null }, Screen, 2, SKColors.Blue, SKColors.White));
    }

    [AvaloniaFact]
    public void PathsOffDrawsNoRoute()
    {
        using var view = new MapView { Route = Direct };
        var viewport = new Mapsui.Viewport(0, 0, 1000, 0, 800, 600);

        using (var drawn = view.BuildRoute(viewport))
        {
            Assert.NotNull(drawn);
        }
        view.Paths = MapPaths.Off;
        Assert.Null(view.BuildRoute(viewport));
    }

    [Fact]
    public async Task SelectingAStationAsksForItsLastPacketsRouteAndDetailsWordIt()
    {
        var engine = new FakeEngine();
        engine.Routes["N0CALL-9"] = Direct;
        var vm = new MainViewModel(engine, new FakeTimeProvider());

        vm.SelectedStation = "N0CALL-9";
        await WaitUntil(() => vm.Route is not null);

        Assert.Equal(new RouteQuery("N0CALL-9"), engine.RouteQueries[^1]);
        Assert.Equal("Heard directly by N0GATE-1, 19 km away", vm.Details.RouteText);
        Assert.Null(vm.PickedRoute); // the map shows a selected station's links, not its last route
    }

    [Fact]
    public async Task APacketFromTheListAsksForThatPacketsRoute()
    {
        var engine = new FakeEngine();
        engine.Routes["N0CALL-9"] = Digipeated;
        var vm = new MainViewModel(engine, new FakeTimeProvider());

        vm.SelectPacket(
            new PacketView(T0, "N0CALL-9", "Position", "x", "N0CALL-9>APRS,N0DIGI-1*:!", 49, -72, Path: "N0DIGI-1*")
        );
        await WaitUntil(() => vm.Route is not null);

        Assert.Equal("N0CALL-9", vm.SelectedStation);
        Assert.Equal(new RouteQuery("N0CALL-9", "N0DIGI-1*", 49, -72), engine.RouteQueries[^1]);
        Assert.Same(vm.Route, vm.PickedRoute);
        Assert.Equal("N0CALL-9 → N0DIGI-1 (9.7 km) → N0GATE-1 (21 km)", vm.Details.RouteText);
    }

    [Fact]
    public async Task ATrackPointAsksForItsPacketsRoute()
    {
        var engine = new FakeEngine();
        engine.Routes["N0CALL-9"] = Direct with { Kind = ArrivalKind.Internet, Hops = [] };
        var vm = new MainViewModel(engine, new FakeTimeProvider());

        vm.SelectTrackPoint("N0CALL-9", T0);
        await WaitUntil(() => vm.Route is not null);

        Assert.Equal(new RouteQuery("N0CALL-9", TrackPoint: T0), engine.RouteQueries[^1]);
        Assert.Equal("Not heard on RF", vm.Details.RouteText);
    }

    [Fact]
    public async Task ANewPacketFromTheSelectedStationRefreshesItsRoute()
    {
        var engine = new FakeEngine();
        engine.Routes["N0CALL-9"] = Direct;
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.SelectedStation = "N0CALL-9";
        await WaitUntil(() => vm.Route is not null);
        var asked = engine.RouteQueries.Count;

        engine.Routes["N0CALL-9"] = Digipeated;
        vm.Apply(new StationBatch([new StationState("N0CALL-9", T0, 49, -72, "/>")], []));
        await WaitUntil(() => vm.Route == Digipeated);

        Assert.Equal(asked + 1, engine.RouteQueries.Count);
    }

    [Fact]
    public async Task ClearingTheSelectionClearsTheRoute()
    {
        var engine = new FakeEngine();
        engine.Routes["N0CALL-9"] = Direct;
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.SelectedStation = "N0CALL-9";
        await WaitUntil(() => vm.Route is not null);

        vm.SelectedStation = null;
        await WaitUntil(() => vm.Route is null);

        Assert.Null(vm.Details.RouteText);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
