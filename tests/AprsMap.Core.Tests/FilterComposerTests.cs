// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsMap.Core;

namespace AprsMap.Core.Tests;

public sealed class FilterComposerTests
{
    private static readonly StationProfile Home = InMemorySettingsStore.Profile("N0CALL") with
    {
        Latitude = 40,
        Longitude = -105,
        RangeKm = 150, // a wide range, so map views inside and outside it are easy to place
    };

    [Fact]
    public void MyRangeComesFirstAndTheProfilesTextLast()
    {
        Assert.Equal("r/40/-105/150", FilterComposer.Compose(Home, null, []));
        Assert.Equal("r/40/-105/150 t/m", FilterComposer.Compose(Home, "t/m", []));
        Assert.Equal("t/m", FilterComposer.Compose(Home with { RangeKm = 0 }, "t/m", []));
        Assert.Equal(string.Empty, FilterComposer.Compose(Home with { RangeKm = 0 }, null, []));
    }

    [Fact]
    public void WithoutALocationItIsMyLastPositionOnAprsIs() =>
        Assert.Equal("m/150", FilterComposer.Compose(Home with { Latitude = null, Longitude = null }, null, []));

    [Fact]
    public void AMapViewOutsideMyRangeAddsARangeAroundIt()
    {
        var denver = new MapViewReport(39.74, -104.99, 30); // inside my 150 km
        var kansasCity = new MapViewReport(39.1, -94.58, 80);
        var continent = new MapViewReport(39, -98, 1_500);

        Assert.Equal("r/40/-105/150", FilterComposer.Compose(Home, null, [denver]));
        Assert.Equal("r/40/-105/150 r/39.1/-94.58/80", FilterComposer.Compose(Home, null, [kansasCity]));
        Assert.Equal("r/40/-105/150", FilterComposer.Compose(Home, null, [continent])); // wider than 1,000 km: none
    }

    [Fact]
    public void FollowedStationsAddAFriendRange()
    {
        Assert.Equal(
            "r/40/-105/150 f/W1AW-9/150",
            FilterComposer.Compose(Home, null, [new MapViewReport(null, null, 0, "W1AW-9")])
        );
        Assert.Equal(
            "f/W1AW-9/150",
            FilterComposer.Compose(
                Home with
                {
                    RangeKm = 0,
                },
                null,
                [new MapViewReport(null, null, 0, "w1aw-9"), new MapViewReport(null, null, 0, "W1AW-9")]
            )
        );
    }

    [Fact]
    public void NoMoreThanEightMapPartsAreAdded()
    {
        var views = Enumerable.Range(0, 12).Select(i => new MapViewReport(10 + i, 20, 50)).ToArray();

        var filter = FilterComposer.Compose(Home, null, views);

        Assert.Equal(9, filter.Split(' ').Count(t => t.StartsWith("r/", StringComparison.Ordinal)));
        Assert.Contains("r/10/20/50", filter, StringComparison.Ordinal); // the most recent first
    }

    [Theory]
    [InlineData(40.0, -105.0, 100.0, false)] // the same view
    [InlineData(40.1, -105.0, 100.0, false)] // 11 km: under a quarter of the radius
    [InlineData(40.3, -105.0, 100.0, true)] // 33 km: over a quarter
    [InlineData(40.0, -105.0, 130.0, true)] // the radius grew by more than a quarter
    public void OnlyAViewThatMovedEnoughCounts(double lat, double lon, double radius, bool differs) =>
        Assert.Equal(
            differs,
            FilterComposer.Differs(new MapViewReport(40, -105, 100), new MapViewReport(lat, lon, radius))
        );

    [Fact]
    public void StartingOrStoppingFollowingAlwaysCounts() =>
        Assert.True(
            FilterComposer.Differs(new MapViewReport(40, -105, 100), new MapViewReport(40, -105, 100, "W1AW-9"))
        );

    [Fact]
    public void WatchedStationsAddAFriendRangeEachOnceWithTheFollowedOne() =>
        Assert.Equal(
            "r/40/-105/150 f/W1AW-9/150 f/K0K/150",
            FilterComposer.Compose(
                Home,
                null,
                [
                    new MapViewReport(null, null, 0, "W1AW-9", ["k0k", "W1AW-9"]),
                    new MapViewReport(null, null, 0, Watched: ["K0K"]),
                ]
            )
        );

    [Fact]
    public void OpeningOrClosingAViewAlwaysCounts()
    {
        var one = new MapViewReport(40, -105, 100, Watched: ["K0K"]);

        Assert.True(FilterComposer.Differs(new MapViewReport(40, -105, 100), one));
        Assert.True(FilterComposer.Differs(one, new MapViewReport(40, -105, 100)));
        Assert.False(FilterComposer.Differs(one, new MapViewReport(40, -105, 100, Watched: ["k0k"])));
    }

    [Fact]
    public void ANewProfileReceivesFiftyKmAroundItsLocation() =>
        Assert.Equal(
            "r/40/-105/50",
            FilterComposer.Compose(
                InMemorySettingsStore.Profile("N0CALL") with
                {
                    Latitude = 40,
                    Longitude = -105,
                },
                null,
                []
            )
        );
}
