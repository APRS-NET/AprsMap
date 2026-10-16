// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Map;
using AprsMap.UI.Platform;
using AprsMap.UI.Stations;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class StationListTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static StationRow Row(string name, double? km = null, string? comment = null, int minutesAgo = 0) =>
        new(
            name,
            StationKind.Station,
            "/>",
            Now.AddMinutes(-minutesAgo),
            false,
            km,
            km is null ? null : 45,
            comment,
            true
        );

    private static StationPage Page(params StationRow[] rows) => new(rows, rows.Length);

    private static (MainViewModel Vm, FakeEngine Engine, FakeTimeProvider Time) Create()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider(Now);
        var vm = new MainViewModel(engine, time);
        vm.Show(engine.Settings);
        return (vm, engine, time);
    }

    [Fact]
    public async Task RowsShowHowLongAgoHowFarAndTheComment()
    {
        var (vm, engine, _) = Create();
        engine.StationPage = Page(Row("N0FAR", 100, "Heading\u0007 home", 4), Row("N0OLD", minutesAgo: 125));

        await vm.StationList.RefreshAsync();

        var far = vm.StationList.Rows[0];
        Assert.Equal("4 min", far.HeardText);
        Assert.Equal("100 km NE 045°", far.RangeText);
        Assert.Equal("Heading␇ home", far.Comment);
        Assert.Equal("2 h", vm.StationList.Rows[1].HeardText);
        Assert.Null(vm.StationList.Rows[1].RangeText);
        Assert.Equal("2 stations", vm.StationList.CountText);
    }

    [Fact]
    public async Task SearchKindsAndSortGoToTheEngine()
    {
        var (vm, engine, time) = Create();
        var list = vm.StationList;

        list.SelectedSort = list.Sorts.Single(s => s.Value == StationSort.Distance);
        list.SelectedKinds = list.KindOptions.Single(k => k.Value == StationKinds.ObjectsAndItems);
        list.Search = "w1a";
        Assert.NotEqual("w1a", engine.LastQuery?.Search); // waits for typing to stop
        time.Advance(StationListViewModel.TypingDelay);
        await WaitUntil(() => engine.LastQuery?.Search == "w1a");

        Assert.Equal(StationSort.Distance, engine.LastQuery!.Sort);
        Assert.Equal(StationKinds.ObjectsAndItems, engine.LastQuery.Kinds);
        Assert.Equal(0, engine.LastQuery.Offset);
    }

    [Fact]
    public async Task StationChangesAskAgainAtMostOnceASecond()
    {
        var (vm, engine, time) = Create();
        await vm.StationList.RefreshAsync();
        var before = engine.Queries;

        vm.Apply(new StationBatch([FakeEngine.Station("N0A")], []));
        vm.Apply(new StationBatch([FakeEngine.Station("N0B")], []));
        vm.Apply(new StationBatch([FakeEngine.Station("N0C")], []));
        Assert.Equal(before, engine.Queries);

        time.Advance(StationListViewModel.RefreshInterval);
        await WaitUntil(() => engine.Queries == before + 1);
    }

    [Fact]
    public async Task WhileHeldRowsKeepTheirOrderAndNewOnesAreAddedAtTheEnd()
    {
        var (vm, engine, _) = Create();
        var list = vm.StationList;
        engine.StationPage = Page(Row("N0A"), Row("N0B"));
        await list.RefreshAsync();

        list.IsHolding = true;
        engine.StationPage = Page(Row("N0NEW"), Row("N0B"), Row("N0A", comment: "moved"));
        await list.RefreshAsync();
        Assert.Equal(["N0A", "N0B", "N0NEW"], list.Rows.Select(r => r.Name));
        Assert.Equal("moved", list.Rows[0].Comment);

        list.IsHolding = false;
        await WaitUntil(() => list.Rows[0].Name == "N0NEW");
        Assert.Equal(["N0NEW", "N0B", "N0A"], list.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task TheNextPageLoadsAfterTheRowsAlreadyListed()
    {
        var (vm, engine, _) = Create();
        engine.StationPage = new StationPage([Row("N0A"), Row("N0B")], 3);
        await vm.StationList.RefreshAsync();

        engine.StationPage = new StationPage([Row("N0C")], 3);
        await vm.StationList.LoadMoreAsync();

        Assert.Equal(2, engine.LastQuery!.Offset);
        Assert.Equal(["N0A", "N0B", "N0C"], vm.StationList.Rows.Select(r => r.Name));
        var queries = engine.Queries;
        await vm.StationList.LoadMoreAsync(); // all loaded: nothing to ask
        Assert.Equal(queries, engine.Queries);
    }

    [Fact]
    public async Task TheSortIsKeptForThisViewer()
    {
        var store = new MemoryStore();
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider(Now)) { Preferences = store };
        vm.StationList.SelectedSort = vm.StationList.Sorts.Single(s => s.Value == StationSort.Name);
        await WaitUntil(() => store.Values.ContainsKey(StationListViewModel.SortKey));

        var again = new MainViewModel(engine, new FakeTimeProvider(Now)) { Preferences = store };
        await again.StationList.LoadAsync();

        Assert.Equal(StationSort.Name, again.StationList.SelectedSort.Value);
        Assert.Equal(StationSort.Name, engine.LastQuery!.Sort);
    }

    [AvaloniaFact]
    public async Task SelectingARowOpensItsDetailsAndFollowShowsTheChipUntilTheMapIsDragged()
    {
        var (vm, engine, _) = Create();
        engine.StationPage = Page(Row("N0CALL-9"));
        engine.Details["N0CALL-9"] = FakeEngine.Detail("N0CALL-9", Now);
        var main = new MainViewModel(engine, new FakeTimeProvider(Now))
        {
            Map = new MapViewModel(null, null, new FakeTimeProvider(Now)),
        };
        await main.Map!.LoadAsync(TestContext.Current.CancellationToken);
        main.Show(engine.Settings);
        await main.StationList.RefreshAsync();
        var stations = new StationsPanel { DataContext = main };
        using var map = new MapPanel { DataContext = main };
        var window = new Window
        {
            Content = new DockPanel { Children = { stations, map } },
            Width = 1000,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = stations.FindControl<ListBox>("StationList")!;
        list.SelectedIndex = 0;
        Assert.Equal("N0CALL-9", main.SelectedStation);
        await WaitUntil(() => main.Details.LastPacket is not null);

        main.Details.IsFollowing = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(map.FindControl<Border>("FollowingChip")!.IsVisible);
        Assert.Equal("Following N0CALL-9", main.Details.FollowingText);

        map.FindControl<Button>("StopFollowingButton")!
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(main.Details.IsFollowing);
        window.Close();
        _ = vm;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }

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

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
