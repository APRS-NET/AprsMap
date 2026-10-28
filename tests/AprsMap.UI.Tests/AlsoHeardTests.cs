// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Map;
using AprsMap.UI.Platform;
using AprsMap.UI.ViewModels;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Stations heard in the last days on the map (docs/features/F1.43-map-history-range.md).</summary>
public sealed class AlsoHeardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class MemoryStore : IPreferenceStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetAsideAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Values.Clear();
            return Task.CompletedTask;
        }
    }

    private static StationState Kept(string name) => new(name, T0.AddDays(-1), 49.1, -72.0, "/>", IsStale: true);

    private static MainViewModel App(FakeEngine engine)
    {
        var vm = new MainViewModel(engine, new FakeTimeProvider(T0));
        vm.Show(engine.Settings);
        return vm;
    }

    [Fact]
    public async Task TheChoiceFollowsTheHistorysLengthAndIsKept()
    {
        var store = new MemoryStore();
        var vm = App(new FakeEngine());
        await vm.ViewFilter.LoadAsync(store);
        Assert.False(vm.ViewFilter.IsAlsoHeardOffered); // no history: not offered (behavior 6)

        vm.ViewFilter.OfferAlsoHeard(7);
        Assert.Equal(
            ["Off: the live map only", "24 hours", "3 days", "7 days"],
            vm.ViewFilter.AlsoHeardOptions.Select(o => o.Label)
        );
        var changes = 0;
        vm.ViewFilter.AlsoHeardChanged += (_, _) => changes++;
        vm.ViewFilter.AlsoHeard = vm.ViewFilter.AlsoHeardOptions[2];
        Assert.Equal(
            (3, 1, "3"),
            (vm.ViewFilter.AlsoHeardDays, changes, store.Values[Stations.ViewFilterViewModel.AlsoHeardKey])
        );

        vm.ViewFilter.OfferAlsoHeard(2); // a shorter history: 3 days no longer fits
        Assert.Equal(2, vm.ViewFilter.AlsoHeardOptions.Count);
        Assert.Null(vm.ViewFilter.AlsoHeardDays);
        vm.ViewFilter.OfferAlsoHeard(7);
        Assert.Equal(3, vm.ViewFilter.AlsoHeardDays); // the viewer's choice comes back
    }

    [Fact]
    public async Task LoadingAsksForTheAreaAndSaysHowManyWereAdded()
    {
        var engine = new FakeEngine { KeptStations = new KeptStations([Kept("N0CAR-9"), Kept("N0BIKE-7")], 2) };
        var vm = App(engine);
        vm.ViewFilter.OfferAlsoHeard(7);
        Assert.Empty((await vm.LoadAlsoHeardAsync(48, -73, 50, -71)).Stations); // off: nothing asked
        Assert.Empty(engine.KeptQueries);

        vm.ViewFilter.AlsoHeard = vm.ViewFilter.AlsoHeardOptions[2];
        var kept = await vm.LoadAlsoHeardAsync(48, -73, 50, -71);

        Assert.Equal(2, kept.Stations.Count);
        Assert.Equal(new KeptStationsQuery(T0.AddDays(-3), 48, -73, 50, -71), engine.KeptQueries[^1]);
        Assert.Equal("+2 heard in the last 3 days, from the history", vm.ViewFilter.AlsoHeardText);
        engine.KeptStations = new KeptStations([Kept("N0CAR-9")], 2_400);
        await vm.LoadAlsoHeardAsync(48, -73, 50, -71);
        Assert.Equal("+1 heard in the last 3 days: the most recent, as there are more", vm.ViewFilter.AlsoHeardText);
    }

    [Fact]
    public async Task SelectingOneShowsWhatTheHistoryHas()
    {
        var engine = new FakeEngine { KeptStations = new KeptStations([Kept("N0CAR-9")], 1) };
        engine.KeptDetails["N0CAR-9"] = new StationDetail(
            "N0CAR-9",
            StationKind.Station,
            T0.AddDays(-1),
            2,
            true,
            49.1,
            -72.0,
            0,
            "/>",
            "yesterday",
            null,
            null,
            null,
            null,
            null,
            null,
            null
        );
        var vm = App(engine);
        vm.ViewFilter.OfferAlsoHeard(7);
        vm.ViewFilter.AlsoHeard = vm.ViewFilter.AlsoHeardOptions[3];
        await vm.LoadAlsoHeardAsync(48, -73, 50, -71);

        await vm.Details.ShowAsync("N0CAR-9");

        Assert.True(vm.Details.FromHistory);
        Assert.False(vm.Details.IsGone);
        Assert.Equal("yesterday", vm.Details.Comment);
        await vm.Details.ShowAsync("N0NOTKEPT");
        Assert.False(vm.Details.FromHistory);
    }

    [AvaloniaFact]
    public void TheMapDrawsAddedStationsOnceAndLiveOnesLive()
    {
        using var view = new MapView();
        view.SetStations([new StationState("N0LIVE", T0, 49, -72, "/-")]);

        view.SetAlsoHeard([Kept("N0CAR-9"), Kept("N0LIVE")]);
        view.Select("N0CAR-9");
        view.SetStations([new StationState("N0LIVE", T0, 49, -72, "/-")]); // a batch: the selection stays

        Assert.Equal("N0CAR-9", view.Selected);
        Assert.Equal(2, view.AlsoHeard.Count);
        view.SetAlsoHeard([]);
        Assert.Empty(view.AlsoHeard);
    }
}
