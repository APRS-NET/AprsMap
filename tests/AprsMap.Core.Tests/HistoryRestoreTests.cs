// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>A restart puts back what the history kept (docs/features/F1.41-persistent-history.md, behavior 7).</summary>
public sealed class HistoryRestoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Moving = "N0POS-9>APRS,WIDE1-1:!4903.50N/07201.75W>moving";
    private const string Old = "N0OLD>APRS:!4803.50N/07101.75W>long gone";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("aprsmap-restore-");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-27T12:00:00Z", null));
    private readonly InMemorySettingsStore _settings = InMemorySettingsStore.WithProfile();

    public void Dispose() => _folder.Delete(recursive: true);

    private Engine Create() =>
        new(
            Options.Create(new EngineOptions { History = { Folder = _folder.FullName } }),
            new EngineTests.FakeSourceFactory(),
            _settings,
            _time,
            NullLogger<Engine>.Instance
        );

    /// <summary>A first run: an old station, then one moving over the last half hour.</summary>
    private async Task RunOnceAsync()
    {
        using var engine = Create();
        await engine.GetSettingsAsync(Ct);
        engine.Accept(Old);
        _time.Advance(TimeSpan.FromHours(3)); // past the 2 hours a station is kept
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            engine.Accept(Moving.Replace("4903.50", $"490{3 + i}.50", StringComparison.Ordinal));
        }
        engine.Accept(Moving.Replace("4903.50", "4905.50", StringComparison.Ordinal)); // a copy: listed, not applied
        await engine.FlushHistoryAsync();
    }

    [Fact]
    public async Task StationsTracksAndThePacketListComeBackAfterARestart()
    {
        await RunOnceAsync();
        using var engine = Create();

        await engine.RestoreFromHistoryAsync(Ct);

        var station = await engine.GetStationAsync("N0POS-9", Ct);
        Assert.NotNull(station);
        Assert.Equal(3, engine.TrackOf("N0POS-9").Count);
        Assert.Null(await engine.GetStationAsync("N0OLD", Ct)); // older than the 2 hours: not put back
        var listed = await RecentAsync(engine);
        Assert.Equal(4, listed.Count);
        Assert.True(listed[^1].Markers.HasFlag(PacketMarkers.Duplicate));
    }

    [Fact]
    public async Task PuttingThemBackCountsNothingAndKeepsNothingAgain()
    {
        await RunOnceAsync();
        using var engine = Create();

        await engine.RestoreFromHistoryAsync(Ct);
        await engine.FlushHistoryAsync();

        var dashboard = await engine.GetDashboardAsync(DashboardRange.Hour, cancellationToken: Ct);
        Assert.All(dashboard.Packets, count => Assert.Equal(0, count));
        Assert.Equal(0, (await engine.GetStatusAsync(Ct)).PacketsReceived);
        Assert.Equal(5, (await engine.ReadHistoryAsync(DateTimeOffset.UnixEpoch).ToListAsync(Ct)).Count);
    }

    [Fact]
    public async Task WithHistoryOffNothingIsPutBack()
    {
        await RunOnceAsync();
        await _settings.SaveAsync(_settings.Stored! with { History = new HistorySettings(Days: 0) }, Ct);
        using var engine = Create();

        await engine.RestoreFromHistoryAsync(Ct);

        Assert.Null(await engine.GetStationAsync("N0POS-9", Ct));
    }

    private static async Task<List<PacketView>> RecentAsync(Engine engine)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listed = new List<PacketView>();
        await using var watch = engine.WatchPacketsAsync(null, cts.Token).GetAsyncEnumerator(cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await watch.MoveNextAsync())
            {
                listed.Add(watch.Current);
            }
        }
        catch (OperationCanceledException) { }
        return listed;
    }
}
