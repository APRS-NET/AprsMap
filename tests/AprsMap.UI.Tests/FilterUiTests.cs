// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class FilterUiTests
{
    [Fact]
    public async Task TheMapIsReportedTwoSecondsAfterItStopsAndFollowingAtOnce()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time);
        engine.Details["W1AW-9"] = FakeEngine.Detail("W1AW-9", time.GetUtcNow());

        vm.MapMoved(40, -105, 80);
        time.Advance(TimeSpan.FromSeconds(1));
        vm.MapMoved(41, -105, 80); // still moving
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(engine.MapViews);
        time.Advance(MainViewModel.MapReportDelay);
        await WaitUntil(() => engine.MapViews.Count == 1);
        Assert.Equal(new MapViewReport(41, -105, 80), engine.MapViews[0]);

        await vm.Details.ShowAsync("W1AW-9");
        vm.Details.IsFollowing = true;
        await WaitUntil(() => engine.MapViews.Count == 2);
        Assert.Equal("W1AW-9", engine.MapViews[1].Following);
    }

    [Fact]
    public void TheConnectionTooltipShowsTheFilter()
    {
        var status = new EngineStatus(ConnectionState.Connected, "T2TEST", 1, "1.0", Filter: "r/40/-105/150 t/m");

        Assert.EndsWith(
            "Filter: r/40/-105/150 t/m",
            ConnectionText.Details(Localizer.Instance, status),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void FilterAreasComeWithTheStatus()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        FilterArea[] areas = [new(40, -105, 150)];

        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 1, "1.0", FilterAreas: areas));

        Assert.Equal(areas, vm.FilterAreas);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
