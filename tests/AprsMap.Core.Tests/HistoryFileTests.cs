// SPDX-License-Identifier: MIT
using System.Globalization;
using System.IO.Pipelines;
using System.Text.Json;
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The dashboard's and the GPS's history kept between runs (issue #386).</summary>
public sealed class HistoryFileTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse(
        "2026-10-25T12:00:00Z",
        CultureInfo.InvariantCulture
    );

    private readonly string _folder = Directory.CreateTempSubdirectory("aprsmap-history-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string FilePath => Path.Combine(_folder, FileHistoryStore.FileName);

    /// <summary>Through the file's own JSON, as a restart would.</summary>
    private static HistorySnapshot RoundTrip(HistorySnapshot snapshot) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(snapshot, HistoryJsonContext.Default.HistorySnapshot),
            HistoryJsonContext.Default.HistorySnapshot
        )!;

    private static PacketView Packet(string source, PacketKind kind = PacketKind.Position) =>
        new(Noon, source, "position", "x", "x", Kind: kind) { SourceId = "aprs-is" };

    [Fact]
    public void TheDashboardsCountsComeBackAfterARestart()
    {
        var before = new DashboardCounters(Noon.AddHours(-2));
        before.Record(Packet("N0CAR-9"), ArrivalKind.Direct, [], Noon.AddMinutes(-30));
        before.Record(Packet("N0CAR-9"), ArrivalKind.Direct, [("N0DIGI", false)], Noon.AddMinutes(-20));
        before.Record(Packet("N0WX", PacketKind.Weather), ArrivalKind.Internet, [], Noon.AddMinutes(-10));
        var kept = RoundTrip(new HistorySnapshot(1, Noon, before.Export(), null));

        var after = new DashboardCounters(Noon.AddMinutes(5)); // restarted five minutes later
        after.Import(kept.Dashboard!, Noon.AddMinutes(5));

        var was = before.Read(DashboardRange.Hour, Noon);
        var now = after.Read(DashboardRange.Hour, Noon.AddMinutes(5));
        Assert.Equal(3, now.Packets.Sum());
        Assert.Equal(was.StationsHeardTotal, now.StationsHeardTotal);
        Assert.Equal(was.NewStationsTotal, now.NewStationsTotal);
        Assert.Equal(["N0WX", "N0CAR-9"], now.Newest.Select(n => n.Key));
        Assert.Equal([new DashboardCount("N0DIGI", 1)], now.BusiestRelays);
        Assert.Equal(Noon.AddHours(-2), now.HistoryStart); // counting since the first run
        Assert.Equal(1, now.Arrivals.Single(a => a.Key == nameof(ArrivalKind.Internet)).Values.Sum());
        // My view still counts from the stations' own tallies.
        Assert.Equal(2, after.Read(DashboardRange.Hour, Noon.AddMinutes(5), n => n == "N0CAR-9").Packets.Sum());
    }

    [Fact]
    public void WhatsOlderThanADayIsLeftBehind()
    {
        var before = new DashboardCounters(Noon);
        before.Record(Packet("N0CAR-9"), ArrivalKind.Direct, [], Noon);
        var kept = RoundTrip(new HistorySnapshot(1, Noon, before.Export(), null));

        var after = new DashboardCounters(Noon.AddHours(25));
        after.Import(kept.Dashboard!, Noon.AddHours(25));

        var day = after.Read(DashboardRange.Day, Noon.AddHours(25));
        Assert.Equal(0, day.Packets.Sum());
        Assert.Equal(0, day.StationsHeardTotal);
    }

    [Fact]
    public void TheGpsHistoryComesBackAndAnOutageGoingOnEndsWhereTheAppStopped()
    {
        var before = new GpsHistory();
        var sky = new[] { new GpsSkySatellite(GnssConstellation.BeiDou, 14, null, 40, 90, 35, true) };
        for (var i = 1; i <= 120; i++)
        {
            before.Record(
                new GpsSample("gps", GpsSampleState.Fix3D, new GpsAccuracy(Hdop: 0.8), sky, 39, -94.7, 0),
                Noon.AddMinutes(-10).AddSeconds(i),
                TimeSpan.FromSeconds(1)
            );
        }
        before.Record(new GpsSample("gps", GpsSampleState.NoFix), Noon.AddMinutes(-8), TimeSpan.FromSeconds(1));
        var kept = RoundTrip(new HistorySnapshot(1, Noon, null, before.Export()));

        var after = new GpsHistory();
        var restart = Noon.AddMinutes(30);
        after.Import(kept.Gps!, restart);
        var minute = new DateTimeOffset(restart.Ticks - (restart.Ticks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
        var gps = after.Read(minute.AddMinutes(-59), TimeSpan.FromMinutes(1), 60, restart)!;

        Assert.Equal(TimeSpan.FromSeconds(120), gps.Fix3D);
        Assert.Equal(1, gps.Losses);
        Assert.False(gps.LastOutage!.Ongoing); // not counted on through the time the app was down
        Assert.Equal(0.8, gps.Dilutions.Single(d => d.Key == "hdop").Stat.Average!.Value, 6);
        Assert.Null(gps.SatellitesUsed.Now); // "now" waits for a new sample
        Assert.Equal(GnssConstellation.BeiDou, Assert.Single(gps.Constellations).System);
        Assert.Equal(120, gps.WanderFixes);
        Assert.Null(gps.FixShare[^1]); // the time the app was down is empty, not zero
    }

    [Fact]
    public async Task ADamagedFileOrAnotherVersionIsSetAside()
    {
        var store = new FileHistoryStore(FilePath, NullLogger.Instance);
        await File.WriteAllTextAsync(FilePath, "{ not json", Ct);

        Assert.Null(await store.LoadAsync(Ct));
        Assert.True(File.Exists(FilePath + ".invalid"));

        await store.SaveAsync(new HistorySnapshot(99, Noon, null, null), Ct);
        Assert.Null(await store.LoadAsync(Ct));
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp")); // saved by renaming
    }

    [Fact]
    public async Task AnEngineSavesItsHistoryAndTheNextOneTakesItUp()
    {
        var gpsSource = SourceSettings.GpsNetworkFor(GpsLink.Tcp, "127.0.0.1") with { Id = "gps" };
        var profile = InMemorySettingsStore.Profile("N0TEST") with { SourceIds = [gpsSource.Id] };
        var settings = new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [gpsSource]);
        var options = Options.Create(new EngineOptions { HistoryFile = FilePath });
        var time = new FakeTimeProvider(Noon);
        Engine Create()
        {
            var factory = new EngineTests.FakeSourceFactory();
            var pipe = new Pipe(); // a GPS that says nothing: silent
            factory.Give(gpsSource.Id, new NmeaGpsSource(_ => Task.FromResult(pipe.Reader.AsStream()), "test", time));
            return new Engine(options, factory, new InMemorySettingsStore(settings), time, NullLogger<Engine>.Instance);
        }
        using (var first = Create())
        {
            using var running = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var firstRun = first.RunAsync(running.Token);
            while ((await first.GetStatusAsync(Ct)).Gps is null)
            {
                await Task.Delay(20, Ct);
            }
            first.SampleGps();
            await running.CancelAsync();
            await firstRun; // saved as it stops
        }
        Assert.True(File.Exists(FilePath));

        time.Advance(TimeSpan.FromMinutes(3));
        using var second = Create();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = second.RunAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        DashboardGps? gps;
        while ((gps = (await second.GetDashboardAsync(DashboardRange.Hour, null, Ct)).Gps) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The history wasn't taken up.");
            await Task.Delay(20, Ct);
        }
        Assert.True(gps.Silent >= TimeSpan.FromSeconds(1));

        await cts.CancelAsync();
        await run;
    }
}
