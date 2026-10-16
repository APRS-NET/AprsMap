// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>This station's details: what its own radio heard (docs/features/F3.1-kiss-sources.md, behavior 8).</summary>
public sealed class MyStationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThisStationListsWhatItHeardAndARowSelectsTheStation()
    {
        var engine = new FakeEngine();
        engine.Details["N0ME"] = new StationDetail(
            "N0ME",
            StationKind.Station,
            DateTimeOffset.MinValue,
            0,
            false,
            49.1,
            -72.03,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Locator: "FN39xc",
            Me: new ThisStation(
                [new HeardStation("N0CAR-9", 4.63, T0), new HeardStation("N0HOUSE", null, T0)],
                [new HeardStation("N0BIKE-7", 12.1, T0)]
            )
        );
        var vm = new MainViewModel(engine, new FakeTimeProvider(T0));

        vm.SelectedStation = "N0ME";
        for (var i = 0; i < 200 && !vm.Details.IsMe; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        Assert.True(vm.Details.IsMe);
        Assert.Equal("This station: your profile", vm.Details.KindText);
        Assert.Null(vm.Details.HeardText); // not heard on its own: no "heard … ago"
        Assert.Equal(
            "2 stations heard directly and 1 through digipeaters in the last hour, on this station's own radio.",
            vm.Details.HeardHereText
        );
        Assert.Equal(
            [("N0CAR-9", "4.6 km"), ("N0HOUSE", null)],
            vm.Details.HeardDirectly.Select(r => (r.Name, r.Distance))
        );
        Assert.True(vm.Details.HasHeardThroughDigipeaters);

        vm.Details.HeardThroughDigipeaters[0].Open.Execute(null);
        Assert.Equal("N0BIKE-7", vm.SelectedStation);
    }
}
