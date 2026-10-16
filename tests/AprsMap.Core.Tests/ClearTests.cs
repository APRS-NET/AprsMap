// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Clearing what the station has collected (docs/features/F1.19-housekeeping.md).</summary>
public sealed class ClearTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Engine Engine, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(T0);
        var engine = new Engine(
            Options.Create(new EngineOptions { RecentPacketCapacity = 100 }),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            time,
            NullLogger<Engine>.Instance
        );
        return (engine, time);
    }

    private static void HeardOld(Engine engine, FakeTimeProvider time)
    {
        engine.Accept("N0OLD>APRS:!4903.50N/07201.75W-");
        time.Advance(TimeSpan.FromMinutes(45)); // past the 30 minutes' stale time
        engine.Accept("N0CAR-9>APRS:!4903.50N/07201.75W>");
        time.Advance(TimeSpan.FromMinutes(1));
        engine.Accept("N0CAR-9>APRS:!4904.50N/07201.75W>");
    }

    [Fact]
    public async Task TheCountsSayWhatEachClearWouldTake()
    {
        var (engine, time) = Create();
        HeardOld(engine, time);
        await time.SweepAsync(engine);

        var counts = await engine.GetClearCountsAsync(Ct);

        Assert.Equal(new ClearCounts(2, 1, 1, 3), counts);
    }

    [Fact]
    public async Task ClearingStaleStationsKeepsTheRest()
    {
        var (engine, time) = Create();
        HeardOld(engine, time);
        await time.SweepAsync(engine);

        Assert.Equal(1, await engine.ClearAsync(ClearScope.StaleStations, Ct));

        Assert.Null(await engine.GetStationAsync("N0OLD", Ct));
        Assert.NotNull(await engine.GetStationAsync("N0CAR-9", Ct));
    }

    [Fact]
    public async Task ClearingTracksKeepsEachStationsLatestPosition()
    {
        var (engine, time) = Create();
        HeardOld(engine, time);

        Assert.Equal(1, await engine.ClearAsync(ClearScope.Tracks, Ct));

        Assert.Equal(0, (await engine.GetClearCountsAsync(Ct)).Tracks);
        Assert.NotNull((await engine.GetStationAsync("N0CAR-9", Ct))!.Latitude);
    }

    [Fact]
    public async Task ClearingStationsEmptiesTheStoreButKeepsTheSettings()
    {
        var (engine, time) = Create();
        HeardOld(engine, time);
        var settings = await engine.GetSettingsAsync(Ct);

        Assert.Equal(2, await engine.ClearAsync(ClearScope.Stations, Ct));

        Assert.Equal(0, (await engine.GetClearCountsAsync(Ct)).Stations);
        Assert.Equal(settings.Profiles, (await engine.GetSettingsAsync(Ct)).Profiles);
        engine.Accept("N0NEW>APRS:!4903.50N/07201.75W-");
        Assert.NotNull(await engine.GetStationAsync("N0NEW", Ct)); // the store fills again
    }

    [Fact]
    public async Task ClearingPacketsEmptiesTheHistoryAndTellsEveryViewer()
    {
        var (engine, time) = Create();
        HeardOld(engine, time);
        var before = (await engine.GetStatusAsync(Ct)).PacketHistory;

        Assert.Equal(3, await engine.ClearAsync(ClearScope.Packets, Ct));

        Assert.Equal(0, (await engine.GetClearCountsAsync(Ct)).Packets);
        Assert.Equal(before + 1, (await engine.GetStatusAsync(Ct)).PacketHistory);
        Assert.Equal(2, (await engine.GetClearCountsAsync(Ct)).Stations); // stations stay
    }
}

/// <summary>Runs the engine's once-a-minute sweep without its background loop.</summary>
internal static class SweepExtensions
{
    public static Task SweepAsync(this FakeTimeProvider time, Engine engine)
    {
        engine.SweepNow(time.GetUtcNow());
        return Task.CompletedTask;
    }
}
