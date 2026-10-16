// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>Area objects and signposts (docs/features/F1.12-area-objects.md, behaviors 1, 6).</summary>
public sealed class AreaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static StationState Only(string line)
    {
        var store = new StationStore();
        store.Apply(AprsPacket.Parse(line), T0);
        store.Subscribe(out var snapshot);
        return snapshot.Single(s => s.Kind != StationKind.Station);
    }

    [Fact]
    public void AnAreaObjectCarriesItsShape()
    {
        // APRS101 p. 61: a filled ellipse, colour 3, size codes 10 (10² / 1500 degrees).
        var area = Only("N0CALL>APRS:;SEARCH   *092345z4903.50N\\07201.75Wl710/310").Area!;

        Assert.Equal(AreaKind.Ellipse, area.Kind);
        Assert.True(area.Filled);
        Assert.Equal(3, area.Color);
        Assert.Equal(100 / 1500.0, area.LatitudeDegrees, 6);
    }

    [Fact]
    public void ALineCarriesItsCorridorInKilometres()
    {
        var area = Only("N0CALL>APRS:;FLIGHTPTH*092345z4903.50N\\07201.75Wl610/310{100}").Area!;

        Assert.Equal(AreaKind.LineLeft, area.Kind);
        Assert.False(area.Filled);
        Assert.Equal(160.9, area.CorridorKm!.Value, 1);
    }

    [Fact]
    public void ASignpostCarriesItsText() =>
        Assert.Equal("55", Only("N0CALL>APRS:)I91 3N!4903.50N\\07201.75Wm{55}").Signpost);

    [Fact]
    public void AnOrdinaryObjectHasNeither()
    {
        var state = Only("N0CALL>APRS:;LEADER   *092345z4903.50N/07201.75W>");

        Assert.Null(state.Area);
        Assert.Null(state.Signpost);
    }
}
