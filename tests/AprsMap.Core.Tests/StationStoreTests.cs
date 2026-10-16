// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsMap.Core;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>The station store (docs/features/F1.3-station-store.md).</summary>
public sealed class StationStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static void Apply(StationStore store, string line, DateTimeOffset at) =>
        store.Apply(AprsPacket.Parse(line), at);

    [Fact]
    public void TheDetailsSayWhatSentTheLastPacketAndHowItArrived()
    {
        var store = new StationStore();
        const string Line = "N0CALL-9>APDR16,N2DIGI*,WIDE2-1,qAR,N1GATE:!4903.50N/07201.75W>";

        Apply(store, Line, T0);

        var entry = store.Get("N0CALL-9")!;
        Assert.Equal(Line, entry.LastPacket);
        Assert.Equal("FN39", entry.Locator![..4]);
        Assert.Equal(6, entry.Locator.Length);
        Assert.Equal("APRSdroid", entry.Device!.Model);
        Assert.Equal(ArrivalKind.Digipeated, entry.Arrival!.Kind);
        Assert.Equal(["N2DIGI"], entry.Arrival.Digipeaters);
        Assert.Equal("N1GATE", entry.Arrival.Gateway);
    }

    [Theory]
    [InlineData("N0CALL>APRS,WIDE1-1,qAR,N1GATE:>hi", ArrivalKind.Direct, "N1GATE")]
    [InlineData("N0CALL>APRS,TCPIP*,qAC,T2TEST:>hi", ArrivalKind.Internet, "T2TEST")]
    [InlineData("N0CALL>APRS,qAS,T2TEST:>hi", ArrivalKind.Unknown, "T2TEST")]
    public void ThePathSaysHowItArrived(string line, ArrivalKind kind, string gateway)
    {
        var store = new StationStore();
        Apply(store, line, T0);

        var arrival = store.Get("N0CALL")!.Arrival!;
        Assert.Equal(kind, arrival.Kind);
        Assert.Equal(gateway, arrival.Gateway);
        Assert.Empty(arrival.Digipeaters);
    }

    [Fact]
    public void AThirdPartyPacketArrivedByTheRouteInsideAndKeepsItsRawText()
    {
        var store = new StationStore();
        const string Line = "N1GATE>APRS,TCPIP*,qAC,T2TEST:}N0CALL>APRS,N2DIGI*,N1GATE*:>hi";

        Apply(store, Line, T0);

        var entry = store.Get("N0CALL")!;
        Assert.Equal(Line, entry.LastPacket);
        Assert.Equal(ArrivalKind.Digipeated, entry.Arrival!.Kind);
    }

    [Fact]
    public void DistanceAndBearingAreMeasuredFromMyLocation()
    {
        var store = new StationStore();
        // About 100 km north-east of 40° N, 105° W.
        Apply(store, "N0FAR>APRS:!4038.17N/10410.17W-", T0);
        Assert.Null(store.Get("N0FAR")!.DistanceKm); // no location yet

        store.SetHome(40, -105);

        var entry = store.Get("N0FAR")!;
        Assert.Equal(100, entry.DistanceKm!.Value, 0);
        Assert.Equal(45, entry.BearingDegrees);
    }

    [Fact]
    public void MovingMyLocationSendsEveryStationAgainWithItsNewDistance()
    {
        var store = new StationStore();
        Apply(store, "N0FAR>APRS:!4038.17N/10410.17W-", T0);
        var watcher = store.Subscribe(out var snapshot);
        Assert.Null(Assert.Single(snapshot).DistanceKm);

        store.SetHome(40, -105);
        var moved = Assert.Single(store.Drain(watcher)!.Changed);
        Assert.Equal(45, moved.BearingDegrees);

        store.SetHome(40, -105); // the same place: nothing to send
        Assert.Null(store.Drain(watcher));

        store.SetHome(null, null);
        Assert.Null(Assert.Single(store.Drain(watcher)!.Changed).DistanceKm);
    }

    [Fact]
    public void APositionThenAStatusThenAPositionKeepOneEntryWithBoth()
    {
        var store = new StationStore();

        Apply(store, "N0CALL-9>APRS,WIDE1-1,qAR,N0IGT:!4903.50N/07201.75W>088/036/A=001234 Heading home", T0);
        Apply(store, "N0CALL-9>APRS:>Net tonight", T0.AddMinutes(1));
        Apply(store, "N0CALL-9>APRS:!4904.00N/07201.75W>", T0.AddMinutes(2));

        var entry = Assert.IsType<StationDetail>(store.Get("N0CALL-9"));
        Assert.Equal(StationKind.Station, entry.Kind);
        Assert.Equal(3, entry.Packets);
        Assert.Equal(T0.AddMinutes(2), entry.LastHeard);
        Assert.Equal(49.0667, entry.Latitude!.Value, 3);
        Assert.Equal("Net tonight", entry.Status);
        Assert.Equal("/A=001234 Heading home", entry.Comment); // kept: the last position carried none
        Assert.Equal(88, entry.CourseDegrees);
        Assert.Equal(36 * 1.852, entry.SpeedKmh!.Value, 3);
        Assert.Equal(1234 * 0.3048, entry.AltitudeMetres!.Value, 3);
        Assert.Equal("/>", entry.Symbol);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void AStationHeardWithoutAPositionHasAnEntryButIsntOnTheMap()
    {
        var store = new StationStore();
        var watcher = store.Subscribe(out _);

        Apply(store, "N0TALK>APRS:>Listening only", T0);

        Assert.Equal("Listening only", store.Get("N0TALK")?.Status);
        Assert.Null(store.Drain(watcher));
    }

    [Fact]
    public void ObjectsChangeOwnersAreKilledByAnyoneAndComeBack()
    {
        var store = new StationStore();
        var watcher = store.Subscribe(out _);

        Apply(store, "N0AAA>APRS:;LEADER   *092345z4903.50N/07201.75W>", T0);
        Apply(store, "N0BBB>APRS:;LEADER   *092345z4903.60N/07201.75W>", T0.AddMinutes(1));
        var leader = Assert.IsType<StationDetail>(store.Get("LEADER"));
        Assert.Equal(StationKind.Object, leader.Kind);
        Assert.Equal("N0BBB", leader.Owner);
        Assert.Equal("N0AAA", leader.PreviousOwner);
        Assert.NotNull(store.Get("N0BBB")); // the sender was heard as well
        store.Drain(watcher);

        Apply(store, "N0CCC>APRS:;LEADER   _092345z4903.60N/07201.75W>", T0.AddMinutes(2));
        Assert.Null(store.Get("LEADER"));
        Assert.Equal(["LEADER"], store.Drain(watcher)!.Removed);

        Apply(store, "N0BBB>APRS:;LEADER   *092345z4903.60N/07201.75W>", T0.AddMinutes(3));
        Assert.Equal("N0BBB", store.Get("LEADER")?.Owner);
    }

    [Fact]
    public void ItemsAreKeyedByName()
    {
        var store = new StationStore();

        Apply(store, "N0SRC>APRS:)AID#2!4903.50N/07201.75WA", T0);

        Assert.Equal(StationKind.Item, store.Get("AID#2")?.Kind);
    }

    [Fact]
    public void AnObjectCantTakeOverAStationsName()
    {
        var store = new StationStore();
        Apply(store, "N0CALL>APRS:!4903.50N/07201.75W>", T0);

        Apply(store, "N0SRC>APRS:;N0CALL   *092345z4903.60N/07201.75W>", T0.AddMinutes(1));

        Assert.Equal(StationKind.Station, store.Get("N0CALL")?.Kind);
    }

    [Fact]
    public void AThirdPartyPacketUpdatesTheStationInsideIt()
    {
        var store = new StationStore();

        Apply(store, "N0IGT>APRS,TCPIP*:}N0INNER>APRS,TCPIP,N0IGT*:!4903.50N/07201.75W-inner", T0);

        Assert.Null(store.Get("N0IGT"));
        var inner = Assert.IsType<StationDetail>(store.Get("N0INNER"));
        Assert.Equal("TCPIP,N0IGT*", inner.Path);
    }

    [Theory]
    [InlineData(120, 29, false, true)]
    [InlineData(120, 31, true, true)]
    [InlineData(120, 121, false, false)]
    [InlineData(null, 600, true, true)]
    public void StationsFadeThenLeaveByAge(int? removeAfter, int minutesLater, bool stale, bool present)
    {
        var store = new StationStore();
        var watcher = store.Subscribe(out _);
        Apply(store, "N0CALL>APRS:!4903.50N/07201.75W>", T0);
        store.Drain(watcher);

        store.Sweep(T0.AddMinutes(minutesLater), new StationAging(30, removeAfter));

        Assert.Equal(present, store.Get("N0CALL") is not null);
        var batch = store.Drain(watcher);
        if (!present)
        {
            Assert.Equal(["N0CALL"], batch!.Removed);
        }
        else
        {
            Assert.Equal(stale, store.Get("N0CALL")!.IsStale);
            Assert.Equal(stale, batch is { Changed: [{ IsStale: true }] });
        }
    }

    [Fact]
    public void HearingAStaleStationAgainMakesItFresh()
    {
        var store = new StationStore();
        Apply(store, "N0CALL>APRS:!4903.50N/07201.75W>", T0);
        store.Sweep(T0.AddMinutes(45), StationAging.Default);

        Apply(store, "N0CALL>APRS:>back", T0.AddMinutes(46));

        Assert.False(store.Get("N0CALL")!.IsStale);
    }

    [Fact]
    public void AtTheCapTheLeastRecentlyHeardGoesFirst()
    {
        var store = new StationStore(maxEntries: 2);
        Apply(store, "N0AAA>APRS:!4903.50N/07201.75W>", T0);
        Apply(store, "N0BBB>APRS:!4903.50N/07201.75W>", T0.AddMinutes(1));
        Apply(store, "N0AAA>APRS:>still here", T0.AddMinutes(2)); // N0BBB is now the oldest

        Apply(store, "N0CCC>APRS:!4903.50N/07201.75W>", T0.AddMinutes(3));

        Assert.Equal(2, store.Count);
        Assert.Null(store.Get("N0BBB"));
        Assert.NotNull(store.Get("N0AAA"));
    }
}
