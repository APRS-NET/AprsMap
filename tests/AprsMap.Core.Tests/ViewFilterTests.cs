// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>The View filter in the engine (docs/features/F1.9-view-filters.md, behaviors 1–3, 6).</summary>
public sealed class ViewFilterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static void Apply(StationStore store, string line, DateTimeOffset? at = null) =>
        store.Apply(AprsPacket.Parse(line), at ?? T0);

    private static StationStore Heard()
    {
        var store = new StationStore();
        Apply(store, "N0WX>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W_220/004g005t077"); // weather, internet
        Apply(store, "N0CAR-9>APRS,WIDE1-1,qAR,N0GATE:=4904.50N/07201.75W>on the way #ARES"); // car, direct, messages
        Apply(store, "N0CALL>APRS,TCPIP*,qAC,T2TEST:;OBJECT   *092345z4905.50N/07201.75W-"); // an object
        return store;
    }

    private static string[] Shown(StationStore store, StationFilter filter)
    {
        store.Subscribe(filter, out var snapshot, out _);
        return [.. snapshot.Select(s => s.Name).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void EachConditionNarrowsWhatIsShown()
    {
        var store = Heard();

        Assert.Equal(["N0CAR-9", "N0WX"], Shown(store, new StationFilter(Kinds: StationKinds.Stations)));
        Assert.Equal(["OBJECT"], Shown(store, new StationFilter(Kinds: StationKinds.ObjectsAndItems)));
        Assert.Equal(["N0WX"], Shown(store, new StationFilter(WithWeather: true)));
        Assert.Equal(["N0CAR-9"], Shown(store, new StationFilter(Reception: Reception.Direct)));
        Assert.Equal(["N0CAR-9"], Shown(store, new StationFilter(MessageCapable: true)));
        Assert.Equal(["N0CAR-9"], Shown(store, new StationFilter(Tags: ["#ARES"])));
        Assert.Equal(["N0WX"], Shown(store, new StationFilter(Categories: ["Weather"])));
        Assert.Equal(3, Shown(store, StationFilter.None).Length);
    }

    [Fact]
    public void AStationThatStopsMatchingIsRemovedAndOneThatStartsArrivesWithItsTrack()
    {
        var store = new StationStore();
        Apply(store, "N0CAR-9>APRS,WIDE1-1,qAR,N0GATE:!4904.50N/07201.75W>");
        Apply(store, "N0CAR-9>APRS,WIDE1-1,qAR,N0GATE:!4905.50N/07201.75W>", T0.AddMinutes(1));
        var watcher = store.Subscribe(new StationFilter(Reception: Reception.Internet), out var snapshot, out _);
        Assert.Empty(snapshot);

        Apply(store, "N0CAR-9>APRS,TCPIP*,qAC,T2TEST:!4906.50N/07201.75W>", T0.AddMinutes(2));
        var started = store.Drain(watcher)!;
        Assert.Equal("N0CAR-9", Assert.Single(started.Changed).Name);
        var track = Assert.Single(started.Tracks!);
        Assert.True(track.Reset);
        Assert.Equal(3, track.Added.Count);

        Apply(store, "N0CAR-9>APRS,WIDE1-1,qAR,N0GATE:!4907.50N/07201.75W>", T0.AddMinutes(3));
        Assert.Equal(["N0CAR-9"], store.Drain(watcher)!.Removed);
    }

    [Fact]
    public void EmergenciesAreAlwaysShown()
    {
        var store = Heard();
        Apply(store, "N0SOS>APRS,TCPIP*,qAC,T2TEST:!4903.50N\\07201.75W!help");

        Assert.Contains("N0SOS", Shown(store, new StationFilter(WithWeather: true)));
    }

    [Fact]
    public void HeardWithinLetsGoAsTimePasses()
    {
        var store = Heard();
        var watcher = store.Subscribe(new StationFilter(HeardWithinMinutes: 10), out var snapshot, out _);
        Assert.Equal(3, snapshot.Length);

        store.Sweep(T0.AddMinutes(11), StationAging.Default);

        Assert.Equal(3, store.Drain(watcher)!.Removed.Count);
    }

    [Fact]
    public void TheListAppliesTheFilterToo()
    {
        var store = Heard();

        var page = store.Query(new StationQuery(Filter: new StationFilter(WithWeather: true)));

        Assert.Equal("N0WX", Assert.Single(page.Rows).Name);
    }

    [Fact]
    public void FacetsCountWhatHasBeenHeard()
    {
        var facets = Heard().Facets();

        Assert.Contains(facets.Categories, f => f.Value == "Weather" && f.Count == 1);
        Assert.Equal([new Facet("#ARES", 1)], facets.Tags);
    }
}
