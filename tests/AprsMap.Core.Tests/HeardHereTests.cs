// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>Packets this station's own TNC heard (docs/features/F3.1-kiss-sources.md, behavior 8).</summary>
public sealed class HeardHereTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // As a TNC passes them on: RF paths, no q-construct, no IGate.
    private const string Direct = "N0CAR-9>APRS,WIDE1-1,WIDE2-1:!4903.50N/07201.75W>";
    private const string Digipeated = "N0CAR-9>APRS,N0DIGI-1*,WIDE1*,WIDE2-1:!4903.50N/07201.75W>";

    private static StationStore Store()
    {
        var store = new StationStore();
        store.SetHome(49.1, -72.03, "N0ME"); // 4.6 km north of the car
        return store;
    }

    [Fact]
    public void ADirectPacketIsHeardDirectlyByThisStationWithTheRouteEndingHere()
    {
        var store = Store();
        store.Apply(AprsPacket.Parse(Direct), T0, "tnc", heardHere: true);

        var arrival = store.Get("N0CAR-9")!.Arrival!;
        Assert.Equal((ArrivalKind.Direct, true, null), (arrival.Kind, arrival.HeardHere, arrival.Gateway));
        var route = store.Route(new RouteQuery("N0CAR-9"))!;
        Assert.True(route.HeardHere);
        var here = Assert.Single(route.Hops);
        Assert.Equal(("N0ME", true), (here.Name, here.IsGateway));
        Assert.Equal(4.63, here.DistanceKm!.Value, 2);
    }

    [Fact]
    public void ADigipeatedPacketGoesThroughItsDigipeatersToThisStation()
    {
        var store = Store();
        store.Apply(AprsPacket.Parse(Digipeated), T0, "tnc", heardHere: true);

        var route = store.Route(new RouteQuery("N0CAR-9"))!;

        Assert.Equal(ArrivalKind.Digipeated, route.Kind);
        Assert.Equal(["N0DIGI-1", "N0ME"], route.Hops.Select(h => h.Name));
        Assert.Equal(["N0DIGI-1"], store.Get("N0CAR-9")!.Arrival!.Digipeaters);
    }

    [Fact]
    public void TheSamePathFromAprsIsIsntHeardHere()
    {
        var store = Store();
        store.Apply(AprsPacket.Parse(Direct), T0, "aprs-is");

        Assert.False(store.Get("N0CAR-9")!.Arrival!.HeardHere);
        Assert.False(store.Route(new RouteQuery("N0CAR-9"))!.HeardHere);
    }

    [Theory]
    [InlineData(Direct, PacketMarkers.HeardHere | PacketMarkers.Direct, 0)]
    [InlineData(Digipeated, PacketMarkers.HeardHere | PacketMarkers.Digipeated, 2)]
    public void PacketsAreMarkedHeardHereWithHowTheyCame(string line, PacketMarkers markers, int hops)
    {
        var view = PacketSummarizer.Summarize(AprsPacket.Parse(line), T0, heardHere: true);

        Assert.Equal((markers, hops), (view.Markers, view.Hops));
    }

    [Fact]
    public void TheNetworksLastLinkEndsAtThisStationWhenItHasAPosition()
    {
        var store = Store();
        store.Apply(AprsPacket.Parse("N0ME>APRS,TCPIP*,qAC,T2TEST:!4906.00N/07201.80W-"), T0); // this station, heard
        store.Apply(AprsPacket.Parse(Digipeated), T0, "tnc", heardHere: true);
        store.Apply(AprsPacket.Parse("N0DIGI-1>APRS,TCPIP*,qAC,T2TEST:!4904.00N/07201.75W#"), T0);

        var links = store.Links().Select(l => (l.From, l.To)).ToList();

        Assert.Contains(("N0CAR-9", "N0DIGI-1"), links);
        Assert.Contains(("N0DIGI-1", "N0ME"), links);
    }
}
