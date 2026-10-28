// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The dashboard over 7 days (docs/features/F1.41-persistent-history.md, behaviors 10, 11).</summary>
public sealed class DashboardWeekTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 10, 0, TimeSpan.Zero);

    private const string Car = "N0CAR-9>APRS,qAR,N0GATE:!4903.50N/07201.75W>";
    private const string House = "N0HOUSE>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W-";
    private const string Bike = "N0BIKE-7>APRS,N0DIGI-1*,WIDE2-1,qAR,N0GATE:!4903.50N/07201.75W>";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("aprsmap-week-");
    private readonly FakeTimeProvider _time = new(T0);

    public void Dispose() => _folder.Delete(recursive: true);

    private string HistoryFolder => Path.Combine(_folder.FullName, "history");

    private string JsonFile => Path.Combine(_folder.FullName, FileHistoryStore.FileName);

    private Engine Create(bool history = true, bool json = false)
    {
        var options = new EngineOptions();
        if (history)
        {
            options.History.Folder = HistoryFolder;
        }
        if (json)
        {
            options.HistoryFile = JsonFile;
        }
        return new Engine(
            Options.Create(options),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            _time,
            NullLogger<Engine>.Instance
        );
    }

    private static int PointOf(Dashboard week, DateTimeOffset at) => (int)((at - week.Start) / week.Step);

    [Fact]
    public async Task SevenDaysHaveAPointAnHourEndingWithTheOneInProgress()
    {
        using var engine = Create(history: false);
        engine.Accept(Car);
        _time.Advance(TimeSpan.FromHours(3));
        engine.Accept(Car);
        engine.Accept(House);

        var week = await engine.GetDashboardAsync(DashboardRange.Week, cancellationToken: Ct);

        Assert.Equal((168, TimeSpan.FromHours(1)), (week.Packets.Count, week.Step));
        Assert.Equal(1, week.Packets[PointOf(week, T0)]);
        Assert.Equal(2, week.Packets[^1]);
        Assert.Equal(2, week.StationsHeard[^1]);
        Assert.Equal(DashboardCounters.Window, week.ListsCover); // no packet history: the lists are the 24 hours
    }

    [Fact]
    public async Task HoursAreKeptPastTheMinutesAndComeBackAfterARestart()
    {
        using (var first = Create())
        {
            await first.GetSettingsAsync(Ct);
            first.Accept(Car);
            first.Accept(House);
            _time.Advance(TimeSpan.FromHours(1));
            first.SweepNow(_time.GetUtcNow()); // the hour has ended: written
        }
        Assert.True(File.Exists(Path.Combine(HistoryFolder, MetricsStore.FileName)));

        _time.Advance(TimeSpan.FromDays(3)); // well past the minutes' 24 hours
        using var second = Create();
        var week = await second.GetDashboardAsync(DashboardRange.Week, cancellationToken: Ct);

        Assert.Equal(2, week.Packets[PointOf(week, T0)]);
        Assert.Equal(2, week.StationsHeard[PointOf(week, T0)]);
        Assert.True(week.HistoryStart <= T0);
    }

    [Fact]
    public async Task TheStationsHeardAndTheBusiestComeFromThePacketHistory()
    {
        using var engine = Create();
        await engine.GetSettingsAsync(Ct);
        engine.Accept(Car);
        _time.Advance(TimeSpan.FromMinutes(1));
        engine.Accept(Car.Replace("4903.50", "4904.50", StringComparison.Ordinal));
        engine.Accept(House);
        _time.Advance(TimeSpan.FromDays(2)); // the minutes no longer hold the car or the house
        engine.Accept(Bike);
        await engine.FlushHistoryAsync();

        var week = await engine.GetDashboardAsync(DashboardRange.Week, cancellationToken: Ct);

        Assert.Equal(3, week.StationsHeardTotal);
        Assert.Equal(new DashboardCount("N0CAR-9", 2), week.BusiestStations[0]);
        Assert.Equal(TimeSpan.FromDays(2).Add(TimeSpan.FromMinutes(1)), week.ListsCover); // all the history kept
    }

    [Fact]
    public async Task MyViewIsntOfferedOverSevenDays()
    {
        using var engine = Create(history: false);
        engine.Accept(Car);
        engine.Accept(House);

        var week = await engine.GetDashboardAsync(
            DashboardRange.Week,
            new StationFilter(Kinds: StationKinds.ObjectsAndItems),
            Ct
        );

        Assert.False(week.MyView);
        Assert.Equal(2, week.Packets[^1]); // the whole feed
    }

    [Fact]
    public async Task AnOlderDashboardFilesHoursAreTakenUpOnTheFirstStart()
    {
        using (var before = Create(history: false, json: true)) // the version before persistent history
        {
            before.Accept(Car);
            before.Accept(House);
            await before.SaveHistoryAsync();
        }
        _time.Advance(TimeSpan.FromHours(2));

        using var after = Create(json: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = after.RunAsync(cts.Token);
        var metrics = new MetricsStore(HistoryFolder);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (
            !File.Exists(Path.Combine(HistoryFolder, MetricsStore.FileName)) || metrics.Read(T0.AddHours(-1)).Count == 0
        )
        {
            Assert.True(DateTime.UtcNow < deadline, "The older file's hours weren't written.");
            await Task.Delay(20, Ct);
        }
        await cts.CancelAsync();
        await run;

        var hour = Assert.Single(metrics.Read(T0.AddHours(-1)));
        metrics.Dispose();
        Assert.Equal((2, 2), (hour.Packets, hour.Stations));
    }

    [Fact]
    public async Task StartingOverDeletesTheHours()
    {
        using var engine = Create();
        await engine.GetSettingsAsync(Ct);
        engine.Accept(Car);
        _time.Advance(TimeSpan.FromHours(1));
        engine.SweepNow(_time.GetUtcNow());

        await engine.ResetSettingsAsync(Ct);

        Assert.False(File.Exists(Path.Combine(HistoryFolder, MetricsStore.FileName)));
    }
}
