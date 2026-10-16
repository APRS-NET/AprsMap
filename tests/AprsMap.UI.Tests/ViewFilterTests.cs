// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Stations;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The View filter in the UI (docs/features/F1.9-view-filters.md, behaviors 2, 4–7).</summary>
public sealed class ViewFilterTests
{
    [Fact]
    public async Task ChoosingAConditionTurnsMyFilterOnAndAsksForFilteredStations()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = vm.StartAsync(cts.Token);
        await WaitUntil(() => engine.StationFilters.Count == 1);
        Assert.Null(engine.StationFilters[0]);

        vm.ViewFilter.WithWeather = true;

        Assert.True(vm.ViewFilter.IsOn);
        await WaitUntil(() => engine.StationFilters.Count == 2);
        Assert.True(engine.StationFilters[1]!.WithWeather);

        vm.ViewFilter.ShowAllCommand.Execute(null);
        await WaitUntil(() => engine.StationFilters.Count == 3);
        Assert.Null(engine.StationFilters[2]);
        Assert.True(vm.ViewFilter.Conditions.WithWeather); // kept for next time (behavior 5)

        await cts.CancelAsync();
        await running;
    }

    [Fact]
    public void TheChipSaysHowManyOfHowManyAreShown()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        vm.ViewFilter.WithTrack = true;

        vm.ViewFilter.ShowCounts(312, 2140);

        Assert.Equal("Filtered · 312 of 2,140 · emergencies always shown", vm.ViewFilter.ChipText);
        vm.ViewFilter.IsOn = false;
        vm.ViewFilter.ShowCounts(2140, 2140);
        Assert.Null(vm.ViewFilter.ChipText);
    }

    [Fact]
    public async Task ListsShowWhatHasBeenHeardWithCounts()
    {
        var engine = new FakeEngine
        {
            Facets = new ViewFacets(
                [new Facet("Vehicles", 24), new Facet("Weather", 3)],
                [new Facet("Tracker", 20)],
                [new Facet("EnRoute", 5)],
                [new Facet("#ARES", 2)]
            ),
        };
        var vm = new MainViewModel(engine, new FakeTimeProvider());

        await vm.ViewFilter.RefreshFacetsAsync();

        Assert.Equal(["Vehicles (24)", "Weather (3)"], vm.ViewFilter.Categories.Select(c => c.Label));
        Assert.Equal("Trackers (20)", vm.ViewFilter.DeviceClasses.Single().Label);
        Assert.Equal("En route (5)", vm.ViewFilter.MicEStatuses.Single().Label);
        Assert.Equal("#ARES (2)", vm.ViewFilter.Tags.Single().Label);

        vm.ViewFilter.DeviceClasses[0].IsChecked = true;
        Assert.Equal(["Tracker"], vm.ViewFilter.Current!.DeviceClasses);
    }

    [Fact]
    public async Task TheFilterIsKeptPerViewer()
    {
        var store = new MapTests.MemoryStore();
        var first = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        await first.ViewFilter.LoadAsync(store);
        first.ViewFilter.ReceptionChoice = first.ViewFilter.ReceptionOptions.Single(o =>
            Equals(o.Value, Reception.AnyRf)
        );
        await WaitUntil(() => store.Saves.Count > 0);

        var second = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        await second.ViewFilter.LoadAsync(store);

        Assert.True(second.ViewFilter.IsOn);
        Assert.Equal(Reception.AnyRf, second.ViewFilter.Current!.Reception);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"on\":true}")]
    public void AKeptFilterThatCantBeReadIsIgnored(string json) => Assert.Null(ViewFilterViewModel.Read(json));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
