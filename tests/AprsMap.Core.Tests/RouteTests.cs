// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>A packet's route (docs/features/F1.21-rf-paths.md, behavior 2).</summary>
public sealed class RouteTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static StationStore WithGatewayAndDigi()
    {
        var store = new StationStore();
        // The IGate 0.1° north (about 11 km) and the digipeater 0.1° east of the station.
        Apply(store, "N0GATE-1>APRS,TCPIP*,qAC,T2TEST:!4910.00N/07200.00W&", T0);
        Apply(store, "N0DIGI-1>APRS,TCPIP*,qAC,T2TEST:!4900.00N/07152.00W#", T0);
        return store;
    }

    private static void Apply(StationStore store, string line, DateTimeOffset at) =>
        store.Apply(AprsPacket.Parse(line), at);

    [Fact]
    public void AStationHeardDirectlyGoesStraightToItsIGateWithTheDistance()
    {
        var store = WithGatewayAndDigi();
        Apply(store, "N0CALL-9>APRS,WIDE1-1,qAR,N0GATE-1:!4900.00N/07200.00W>", T0);

        var route = store.Route(new RouteQuery("N0CALL-9"))!;

        Assert.Equal(ArrivalKind.Direct, route.Kind);
        var gateway = Assert.Single(route.Hops);
        Assert.Equal("N0GATE-1", gateway.Name);
        Assert.True(gateway.IsGateway);
        Assert.Equal(18.53, gateway.DistanceKm!.Value, 2); // 10' of latitude
        Assert.Equal(49, route.Latitude!.Value, 3);
    }

    [Fact]
    public void ADigipeatedPacketGoesThroughEachDigipeaterThenTheIGate()
    {
        var store = WithGatewayAndDigi();
        Apply(store, "N0CALL-9>APRS,N0DIGI-1*,WIDE2-1,qAR,N0GATE-1:!4900.00N/07200.00W>", T0);

        var route = store.Route(new RouteQuery("N0CALL-9"))!;

        Assert.Equal(ArrivalKind.Digipeated, route.Kind);
        Assert.Equal(["N0DIGI-1", "N0GATE-1"], route.Hops.Select(h => h.Name));
        Assert.False(route.Hops[0].IsGateway);
        Assert.All(route.Hops, h => Assert.NotNull(h.DistanceKm));
    }

    [Fact]
    public void AHopWithNoPositionIsNamedAndTheNextMeasuresFromTheLastKnownPoint()
    {
        var store = WithGatewayAndDigi();
        Apply(store, "N0CALL-9>APRS,N0NONE-1*,WIDE2-1,qAR,N0GATE-1:!4900.00N/07200.00W>", T0);

        var route = store.Route(new RouteQuery("N0CALL-9"))!;

        Assert.Null(route.Hops[0].Latitude);
        Assert.Null(route.Hops[0].DistanceKm);
        Assert.Equal(18.53, route.Hops[1].DistanceKm!.Value, 2); // from the station, the last known point
    }

    [Fact]
    public void APacketFromTheInternetHasNoHops()
    {
        var store = WithGatewayAndDigi();
        Apply(store, "N0CALL-9>APRS,TCPIP*,qAC,T2TEST:!4900.00N/07200.00W>", T0);

        var route = store.Route(new RouteQuery("N0CALL-9"))!;

        Assert.Equal(ArrivalKind.Internet, route.Kind);
        Assert.Empty(route.Hops);
    }

    [Fact]
    public void ATrackPointHasItsOwnPacketsRoute()
    {
        var store = WithGatewayAndDigi();
        Apply(store, "N0CALL-9>APRS,WIDE1-1,qAR,N0GATE-1:!4900.00N/07200.00W>", T0);
        Apply(store, "N0CALL-9>APRS,N0DIGI-1*,qAR,N0GATE-1:!4901.00N/07200.00W>", T0.AddMinutes(1));

        var first = store.Route(new RouteQuery("N0CALL-9", TrackPoint: T0))!;
        var last = store.Route(new RouteQuery("N0CALL-9"))!;

        Assert.Equal(ArrivalKind.Direct, first.Kind);
        Assert.Equal(49, first.Latitude!.Value, 3);
        Assert.Equal(ArrivalKind.Digipeated, last.Kind);
        Assert.Null(store.Route(new RouteQuery("N0CALL-9", TrackPoint: T0.AddHours(1))));
    }

    [Fact]
    public void APacketFromTheListUsesItsOwnPathAndPosition()
    {
        var store = WithGatewayAndDigi();
        Apply(store, "N0CALL-9>APRS,TCPIP*,qAC,T2TEST:!4900.00N/07200.00W>", T0);

        var route = store.Route(new RouteQuery("N0CALL-9", "N0DIGI-1*,qAR,N0GATE-1", 49.5, -72))!;

        Assert.Equal(ArrivalKind.Digipeated, route.Kind);
        Assert.Equal(49.5, route.Latitude);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a path!,x")]
    public void APathThatSaysNothingGivesNoHops(string path)
    {
        var store = WithGatewayAndDigi();

        var route = store.Route(new RouteQuery("N0GATE-1", path))!;

        Assert.Equal(ArrivalKind.Unknown, route.Kind);
        Assert.Empty(route.Hops);
    }

    [Fact]
    public void AnUnknownStationHasNoRoute() => Assert.Null(new StationStore().Route(new RouteQuery("NOBODY")));
}
