// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.Core;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

public sealed class StationQueryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static StationStore Store()
    {
        var store = new StationStore();
        Apply(store, "W1AW-9>APRS:!4100.00N/10500.00W>Far north", T0); // ~111 km from 40N 105W
        Apply(store, "N0NEAR>APRS:!4010.00N/10500.00W-Close by", T0.AddMinutes(1)); // ~18 km
        Apply(store, "K0NOPOS>APRS:>Status only", T0.AddMinutes(2)); // no position
        Apply(store, "N0OWN>APRS:;LEADER   *092345z4005.00N/10500.00W>Object", T0.AddMinutes(3)); // ~9 km
        return store;
    }

    private static void Apply(StationStore store, string line, DateTimeOffset at) =>
        store.Apply(AprsPacket.Parse(line), at);

    private static string[] Names(StationPage page) => [.. page.Rows.Select(r => r.Name)];

    [Fact]
    public void EachSortOrdersTheRows()
    {
        var store = Store();
        store.SetHome(40, -105);

        Assert.Equal(["LEADER", "N0OWN", "K0NOPOS", "N0NEAR", "W1AW-9"], Names(store.Query(new StationQuery())));
        Assert.Equal(
            ["K0NOPOS", "LEADER", "N0NEAR", "N0OWN", "W1AW-9"],
            Names(store.Query(new StationQuery(Sort: StationSort.Name)))
        );
        // Nearest first; N0OWN (the object's owner) and K0NOPOS have no position, so they come last, by name.
        Assert.Equal(
            ["LEADER", "N0NEAR", "W1AW-9", "K0NOPOS", "N0OWN"],
            Names(store.Query(new StationQuery(Sort: StationSort.Distance)))
        );
    }

    [Fact]
    public void WithoutMyLocationTheDistanceSortFallsBackToNames()
    {
        var store = Store();

        Assert.Equal(
            ["K0NOPOS", "LEADER", "N0NEAR", "N0OWN", "W1AW-9"],
            Names(store.Query(new StationQuery(Sort: StationSort.Distance)))
        );
    }

    [Theory]
    [InlineData("w1a", new[] { "W1AW-9" })]
    [InlineData("-9", new[] { "W1AW-9" })]
    [InlineData("n0", new[] { "N0NEAR", "N0OWN" })]
    [InlineData("  ", new[] { "K0NOPOS", "LEADER", "N0NEAR", "N0OWN", "W1AW-9" })]
    public void SearchMatchesAnyPartOfTheNameIgnoringCase(string search, string[] names) =>
        Assert.Equal(names, Names(Store().Query(new StationQuery(search, Sort: StationSort.Name))));

    [Fact]
    public void KindsCombineWithSearch()
    {
        var store = Store();

        Assert.Equal(["LEADER"], Names(store.Query(new StationQuery(Kinds: StationKinds.ObjectsAndItems))));
        Assert.Empty(store.Query(new StationQuery("N0", StationKinds.ObjectsAndItems)).Rows);
        Assert.Equal(
            ["N0NEAR", "N0OWN"],
            Names(store.Query(new StationQuery("N0", StationKinds.Stations, StationSort.Name)))
        );
    }

    [Fact]
    public void APageHoldsItsRowsAndTheTotal()
    {
        var store = Store();
        store.SetHome(40, -105);

        var page = store.Query(new StationQuery(Sort: StationSort.Name, Offset: 1, Count: 2));

        Assert.Equal(5, page.Total);
        Assert.Equal(["LEADER", "N0NEAR"], Names(page));
        var near = page.Rows[1];
        Assert.Equal("Close by", near.Comment);
        Assert.True(near.HasPosition);
        Assert.Equal(18.5, near.DistanceKm!.Value, 1);
        Assert.Equal(0, near.BearingDegrees);
        Assert.Equal("Status only", store.Query(new StationQuery("K0NOPOS")).Rows[0].Comment); // no comment: the status
    }

    [Fact]
    public void AQueryOverFiftyThousandEntriesTakesUnderFiftyMilliseconds()
    {
        var store = new StationStore();
        store.SetHome(40, -105);
        for (var i = 0; i < 50_000; i++)
        {
            var lat = 30 + (i % 2000) * 0.01;
            var lon = 100 + (i / 2000) * 0.5;
            Apply(
                store,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"N{i:00000}>APRS:!{(int)lat:00}{lat % 1 * 60:00.00}N/{(int)lon:000}{lon % 1 * 60:00.00}W>"
                ),
                T0.AddSeconds(i)
            );
        }

        var best = TimeSpan.MaxValue;
        for (var run = 0; run < 5; run++)
        {
            var watch = Stopwatch.StartNew();
            var page = store.Query(new StationQuery("N1", Sort: StationSort.Distance, Count: 100));
            watch.Stop();
            Assert.Equal(10_000, page.Total);
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        Assert.True(best < TimeSpan.FromMilliseconds(50), $"Best of five: {best.TotalMilliseconds:0} ms");
    }
}
