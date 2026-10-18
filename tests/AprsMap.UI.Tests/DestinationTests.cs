// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Map;
using AprsMap.UI.Platform;

namespace AprsMap.UI.Tests;

/// <summary>My destination (docs/features/F1.18-destination.md, acceptance tests 1, 2, 4).</summary>
public sealed class DestinationTests
{
    private static readonly DestinationInfo Denver = new(
        39.6,
        135,
        [new GeoPoint(40.015, -105.27), new GeoPoint(39.74, -104.99)]
    );

    private static (DestinationViewModel Destination, FakeEngine Engine, MemoryStore Store) Create(
        UnitSettings? units = null
    )
    {
        var engine = new FakeEngine();
        engine.Destinations[(39.74, -104.99)] = Denver;
        engine.Destinations[(40.0625, -105.125)] = Denver with { DistanceKm = 12.3, Bearing = 90 };
        var store = new MemoryStore();
        var destination = new DestinationViewModel(engine, Localizer.Instance, () => units ?? UnitSettings.Metric)
        {
            Preferences = store,
        };
        return (destination, engine, store);
    }

    [Fact]
    public async Task APointShowsItsDistanceAndBearingAndTheLine()
    {
        var (destination, _, _) = Create();

        await destination.SetAsync(39.74, -104.99);

        Assert.True(destination.IsSet);
        Assert.Equal("39.7400, -104.9900 · 40 km SE 135°", destination.Text);
        Assert.Equal(2, destination.Line.Count);

        await destination.ClearCommand.ExecuteAsync(null);
        Assert.False(destination.IsSet);
        Assert.Null(destination.Text);
        Assert.Empty(destination.Line);
    }

    [Fact]
    public async Task InMilesTheDistanceIsInMiles()
    {
        var (destination, _, _) = Create(UnitSettings.Imperial);

        await destination.SetAsync(39.74, -104.99);

        Assert.Equal("39.7400, -104.9900 · 25 mi SE 135°", destination.Text);
    }

    [Fact]
    public async Task WithoutMyLocationTheChipSaysToSetIt()
    {
        var (destination, _, _) = Create();

        await destination.SetAsync(10, 20);

        Assert.Equal("10.0000, 20.0000 · Set your location to see the distance", destination.Text);
        Assert.Empty(destination.Line);
    }

    [Fact]
    public async Task CoordinatesOrALocatorCanBeTypedAndAnythingElseIsRefused()
    {
        var (destination, _, _) = Create();

        destination.Typed = "39.74, -104.99";
        await destination.SetTypedCommand.ExecuteAsync(null);
        Assert.Equal(new GeoPoint(39.74, -104.99), destination.Point);
        Assert.Empty(destination.Typed);

        destination.Typed = "DN70kb";
        await destination.SetTypedCommand.ExecuteAsync(null);
        Assert.Equal(new GeoPoint(40.0625, -105.125), destination.Point);

        destination.Typed = "somewhere nice";
        await destination.SetTypedCommand.ExecuteAsync(null);
        Assert.Equal(
            "Type a latitude and longitude, such as 40.015, -105.27, or a locator such as DN70ka.",
            destination.Error
        );
        Assert.Equal(new GeoPoint(40.0625, -105.125), destination.Point); // unchanged
    }

    [Fact]
    public async Task AStationDestinationFollowsTheStationAndIsKeptAcrossRestarts()
    {
        var (destination, engine, store) = Create();
        await destination.SetAsync(40.0625, -105.125, "W1AW-9");
        Assert.StartsWith("W1AW-9 · 12 km E", destination.Text, StringComparison.Ordinal);

        destination.StationsChanged(
            new StationBatch([new StationState("W1AW-9", DateTimeOffset.UnixEpoch, 39.74, -104.99, "/>")], [])
        );
        await Task.Yield();
        Assert.Equal(new GeoPoint(39.74, -104.99), destination.Point);

        // Another viewer's store keeps its own; this one comes back after a restart.
        var again = new DestinationViewModel(engine, Localizer.Instance, () => UnitSettings.Metric)
        {
            Preferences = store,
        };
        await again.LoadAsync();
        Assert.Equal(("W1AW-9", new GeoPoint(39.74, -104.99)), (again.Station, again.Point));
        var other = new DestinationViewModel(engine, Localizer.Instance, () => UnitSettings.Metric)
        {
            Preferences = new MemoryStore(),
        };
        await other.LoadAsync();
        Assert.False(other.IsSet);
    }

    private sealed class MemoryStore : IPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));

        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetAsideAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _values.Clear();
            return Task.CompletedTask;
        }
    }
}
