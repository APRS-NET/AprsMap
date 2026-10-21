// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class FilterUiTests
{
    [Fact]
    public async Task FollowingIsReportedAtOnceAndTheMapsViewNever()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time);
        engine.Details["W1AW-9"] = FakeEngine.Detail("W1AW-9", time.GetUtcNow());

        await vm.Details.ShowAsync("W1AW-9");
        vm.Details.IsFollowing = true;
        await WaitUntil(() => engine.MapViews.Count == 1);
        Assert.Equal(new MapViewReport(null, null, 0, "W1AW-9"), engine.MapViews[0]); // no map part (F1.2 behavior 1.2)

        vm.Details.IsFollowing = false;
        await WaitUntil(() => engine.MapViews.Count == 2);
        Assert.Equal(new MapViewReport(null, null, 0), engine.MapViews[1]); // stopping takes it out
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
