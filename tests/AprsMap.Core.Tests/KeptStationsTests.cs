// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Stations heard in the last days, from the history (docs/features/F1.43-map-history-range.md).</summary>
public sealed class KeptStationsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-20T12:00:00Z", null);

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("aprsmap-kept-");
    private readonly FakeTimeProvider _time = new(T0);

    public void Dispose() => _folder.Delete(recursive: true);

    private Engine Create() =>
        new(
            Options.Create(new EngineOptions { History = { Folder = _folder.FullName } }),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            _time,
            NullLogger<Engine>.Instance
        );

    private KeptStationsQuery Around(int days, int max = KeptStationsQuery.DefaultMax) =>
        new(_time.GetUtcNow().AddDays(-days), 48, -73, 50, -71, max);

    private async Task<Engine> HeardAsync()
    {
        var engine = Create();
        await engine.GetSettingsAsync(Ct);
        engine.Accept("N0OLD>APRS:!4903.50N/07201.75W>five days ago");
        _time.Advance(TimeSpan.FromDays(4));
        engine.Accept("N0CAR-9>APRS:!4903.50N/07201.75W>yesterday, first");
        engine.Accept("N0CAR-9>APRS:!4913.50N/07201.75W>yesterday, last");
        engine.Accept("N0FAR>APRS:!3903.50N/10501.75W-far away");
        engine.Accept("N0OBJ>APRS:;AID-3    *111111z4903.50N/07201.75WAaid station");
        _time.Advance(TimeSpan.FromDays(1));
        engine.Accept("N0LIVE>APRS:!4903.50N/07201.75W-heard now");
        engine.SweepNow(_time.GetUtcNow()); // yesterday's leave the store
        await engine.FlushHistoryAsync();
        return engine;
    }

    [Fact]
    public async Task StationsHeardInTheDaysAndAreaAreAddedAtTheirLastPositions()
    {
        using var engine = await HeardAsync();

        var kept = await engine.GetKeptStationsAsync(Around(3), Ct);

        Assert.Equal(["AID-3", "N0CAR-9"], kept.Stations.Select(s => s.Name).Order());
        var car = kept.Stations.Single(s => s.Name == "N0CAR-9");
        Assert.Equal((49.225, "/>", true), (Math.Round(car.Latitude, 3), car.Symbol, car.IsStale));
        Assert.Equal(StationKind.Object, kept.Stations.Single(s => s.Name == "AID-3").Kind);
        Assert.Equal(3, (await engine.GetKeptStationsAsync(Around(7), Ct)).Stations.Count); // and five days ago
    }

    [Fact]
    public async Task TheLimitKeepsTheMostRecentAndSaysHowManyThereWere()
    {
        using var engine = await HeardAsync();

        var kept = await engine.GetKeptStationsAsync(Around(7, max: 1), Ct);

        Assert.Equal((1, 3), (kept.Stations.Count, kept.Total));
        Assert.Equal(T0.AddDays(4), kept.Stations[0].LastHeard);
    }

    [Fact]
    public async Task AnAreaAcrossTheAntimeridianIsFound()
    {
        using var engine = Create();
        await engine.GetSettingsAsync(Ct);
        engine.Accept("ZL0EAST>APRS:!4100.00S/17930.00E-east of it");
        engine.Accept("N0WEST>APRS:!4100.00S/17930.00W-west of it");
        await engine.FlushHistoryAsync();
        _time.Advance(TimeSpan.FromHours(3));
        engine.SweepNow(_time.GetUtcNow()); // out of the store: the history's alone

        var kept = await engine.GetKeptStationsAsync(new KeptStationsQuery(T0.AddDays(-1), -42, 179, -40, -179), Ct);

        Assert.Equal(["N0WEST", "ZL0EAST"], kept.Stations.Select(s => s.Name).Order());
    }

    [Fact]
    public async Task AStationOnlyTheHistoryHasIsDescribedByItsKeptPackets()
    {
        using var engine = await HeardAsync();

        var car = await engine.GetKeptStationAsync("N0CAR-9", Ct);

        Assert.NotNull(car);
        Assert.Equal(("yesterday, last", true, 2L), (car.Comment, car.IsStale, car.Packets));
        Assert.Null(await engine.GetKeptStationAsync("N0NEVER", Ct));
    }
}
