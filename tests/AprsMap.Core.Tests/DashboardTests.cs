// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The dashboard's counters (docs/features/F1.22-statistics.md, behaviors 3, 4 and "Engine").</summary>
public sealed class DashboardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private const string Direct = "N0CAR-9>APRS,qAR,N0GATE:!4903.50N/07201.75W>";
    private const string Digipeated = "N0BIKE-7>APRS,N0DIGI-1*,WIDE2-1,qAR,N0GATE:!4903.50N/07201.75W>";
    private const string Internet = "N0HOUSE>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W-";
    private const string Weather = "N0WX>APRS,TCPIP*,qAC,T2TEST:_10161200c090s005g010t050";
    private const string Invalid = "N0BAD>APRS,TCPIP*,qAC,T2TEST:!9999.99N/99999.99W- malformed";

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

    [Fact]
    public async Task AnHourHasAPointAMinuteEndingWithTheOneInProgress()
    {
        var (engine, time) = Create();
        engine.Accept(Direct);
        engine.Accept(Internet);
        engine.Accept(Internet.Replace("N0HOUSE", "N0SHED", StringComparison.Ordinal));
        time.Advance(TimeSpan.FromMinutes(2.5));
        engine.Accept(Direct.Replace("4903.50", "4904.50", StringComparison.Ordinal));

        var dashboard = await engine.GetDashboardAsync(DashboardRange.Hour, Ct);

        Assert.Equal(TimeSpan.FromMinutes(1), dashboard.Step);
        Assert.Equal(60, dashboard.Packets.Count);
        Assert.Equal(T0.AddMinutes(2 - 59), dashboard.Start);
        Assert.Equal([3, 0, 1], dashboard.Packets.TakeLast(3));
        Assert.Equal(4 / 5.0, dashboard.PacketsPerMinute);
        Assert.Equal(T0, dashboard.HistoryStart);
    }

    [Fact]
    public async Task StationsCountOnceAPointAndAreNewOnlyTheFirstTime()
    {
        var (engine, time) = Create();
        engine.Accept(Direct);
        engine.Accept(Direct.Replace("4903.50", "4904.50", StringComparison.Ordinal));
        engine.Accept(Internet);
        time.Advance(TimeSpan.FromMinutes(1));
        engine.Accept(Direct.Replace("4903.50", "4905.50", StringComparison.Ordinal));

        var hour = await engine.GetDashboardAsync(DashboardRange.Hour, Ct);

        Assert.Equal([2, 1], hour.StationsHeard.TakeLast(2));
        Assert.Equal(2, hour.StationsHeardTotal);
        Assert.Equal([2, 0], hour.NewStations.TakeLast(2));
        Assert.Equal(2, hour.NewStationsTotal);
        Assert.Equal(["N0HOUSE", "N0CAR-9"], hour.Newest.Select(n => n.Key));
        var day = await engine.GetDashboardAsync(DashboardRange.Day, Ct);
        Assert.Equal(2, day.StationsHeard[^1]); // one 15-minute point: each station once
    }

    [Fact]
    public async Task PacketsAreCountedByArrivalKindAndRelay()
    {
        var (engine, _) = Create();
        engine.Accept(Direct);
        engine.Accept(Digipeated);
        engine.Accept(Internet);

        var dashboard = await engine.GetDashboardAsync(DashboardRange.Hour, Ct);

        int Last(string kind) => dashboard.Arrivals.Single(a => a.Key == kind).Values[^1];
        Assert.Equal(1, Last(nameof(ArrivalKind.Direct)));
        Assert.Equal(1, Last(nameof(ArrivalKind.Digipeated)));
        Assert.Equal(1, Last(nameof(ArrivalKind.Internet)));
        Assert.Equal([new DashboardCount("N0GATE", 2), new DashboardCount("N0DIGI-1", 1)], dashboard.BusiestRelays);
    }

    [Fact]
    public async Task KindsProblemsAndTheBusiestStationsAreCounted()
    {
        var (engine, _) = Create();
        engine.Accept(Weather);
        engine.Accept(Direct);
        engine.Accept(Direct.Replace("4903.50", "4904.50", StringComparison.Ordinal));
        engine.Accept(Invalid);

        var dashboard = await engine.GetDashboardAsync(DashboardRange.Hour, Ct);

        // The malformed position is still a position report, flagged.
        Assert.Equal(3, dashboard.Kinds.Single(k => k.Key == nameof(PacketKind.Position)).Count);
        Assert.Equal(1, dashboard.Kinds.Single(k => k.Key == nameof(PacketKind.Weather)).Count);
        Assert.Equal(1, dashboard.ProblemsTotal);
        Assert.Equal(new DashboardCount("N0CAR-9", 2), dashboard.BusiestStations[0]);
        Assert.Equal(3, dashboard.BusiestStations.Count);
    }

    [Fact]
    public async Task ARejectedLineCountsAsAProblem()
    {
        var (engine, _) = Create();
        engine.Accept("N0X>APRS:>" + new string('\u20AC', 1)); // not Latin-1: the line is dropped

        var dashboard = await engine.GetDashboardAsync(DashboardRange.Hour, Ct);

        Assert.Equal(0, dashboard.Packets[^1]);
        Assert.Equal(1, dashboard.ProblemsTotal);
    }

    [Fact]
    public async Task LongerRangesHaveCoarserPointsAndOldCountsFallOff()
    {
        var (engine, time) = Create();
        engine.Accept(Direct);
        time.Advance(TimeSpan.FromMinutes(7));
        engine.Accept(Internet);

        var six = await engine.GetDashboardAsync(DashboardRange.SixHours, Ct);
        Assert.Equal(TimeSpan.FromMinutes(5), six.Step);
        Assert.Equal(72, six.Packets.Count);
        Assert.Equal([1, 1], six.Packets.TakeLast(2));
        var day = await engine.GetDashboardAsync(DashboardRange.Day, Ct);
        Assert.Equal(96, day.Packets.Count);
        Assert.Equal(2, day.Packets[^1]);

        time.Advance(TimeSpan.FromHours(25));
        var later = await engine.GetDashboardAsync(DashboardRange.Day, Ct);
        Assert.Equal(0, later.Packets.Sum());
        Assert.Equal(0, later.StationsHeardTotal);
        Assert.Empty(later.BusiestStations);
        Assert.Empty(later.Newest);
    }

    [Fact]
    public async Task ClearingPacketsStartsTheCountsOver()
    {
        var (engine, time) = Create();
        engine.Accept(Direct);
        time.Advance(TimeSpan.FromMinutes(3));

        await engine.ClearAsync(ClearScope.Packets, Ct);
        engine.Accept(Direct);
        var dashboard = await engine.GetDashboardAsync(DashboardRange.Hour, Ct);

        Assert.Equal(T0.AddMinutes(3), dashboard.HistoryStart);
        Assert.Equal(1, dashboard.Packets.Sum());
        Assert.Equal(1, dashboard.NewStationsTotal); // heard again after the clear: new to this history
    }

    [Fact]
    public void OneSourceGivesNoPerSourceSeriesTwoGiveOneEach()
    {
        var counters = new DashboardCounters(T0);
        var packet = new PacketView(T0, "N0CAR-9", "Position", "x", "raw", SourceId: "aprs-is");
        counters.Record(packet, ArrivalKind.Internet, [], T0);
        Assert.Empty(counters.Read(DashboardRange.Hour, T0).PacketsBySource);

        counters.Record(packet with { SourceId = "kiss" }, ArrivalKind.Direct, [], T0);
        var dashboard = counters.Read(DashboardRange.Hour, T0);

        Assert.Equal(["aprs-is", "kiss"], dashboard.PacketsBySource.Select(s => s.Key));
        Assert.All(dashboard.PacketsBySource, s => Assert.Equal(1, s.Values[^1]));
        Assert.Equal(["aprs-is", "kiss"], dashboard.Sources.Select(s => s.Key));
    }

    [Fact]
    public void ASourceRepeatingAnotherIsntQuiet()
    {
        var counters = new DashboardCounters(T0);
        counters.RecordSource("kiss", T0.AddMinutes(5));

        var dashboard = counters.Read(DashboardRange.Hour, T0.AddMinutes(5));

        Assert.Equal(new DashboardMoment("kiss", T0.AddMinutes(5)), Assert.Single(dashboard.Sources));
        Assert.Equal(0, dashboard.Packets.Sum());
    }

    [Fact]
    public async Task TheTablesCountDevicesSymbolsKindsAndReceptionOfTheStationsHeld()
    {
        var (engine, _) = Create();
        engine.Accept("N0DROID-7>APDR16,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W>"); // APRSdroid, a car
        engine.Accept("N0DROID-8>APDR16,TCPIP*,qAC,T2TEST:!4904.50N/07201.75W>");
        engine.Accept(Direct); // an unknown device
        engine.Accept("N0OBJ>APRS,TCPIP*,qAC,T2TEST:;AIDSTN1  *161200z4903.50N/07201.75Wa Aid station");

        var tables = (await engine.GetDashboardAsync(DashboardRange.Hour, Ct)).Tables!;

        Assert.Equal(5, tables.Total); // the object's sender is a station too
        var droid = Assert.Single(
            tables.Platforms,
            p => $"{p.Vendor} {p.Model}".Contains("APRSdroid", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(2, droid.Count);
        Assert.Equal(new DashboardSymbol("/>", "Car", "Vehicles", 3), tables.Symbols[0]);
        Assert.Contains(new DashboardCount(nameof(StationKind.Object), 1), tables.Kinds);
        Assert.Contains(new DashboardCount(nameof(ArrivalKind.Direct), 1), tables.Reception);
    }

    [Fact]
    public async Task WeatherExtremesComeFromTheStationsHeardInTheRange()
    {
        var (engine, time) = Create();
        engine.Accept("N0COLD>APRS,TCPIP*,qAC,T2TEST:@161200z4903.50N/07201.75W_090/005g010t014");
        engine.Accept("N0WARM>APRS,TCPIP*,qAC,T2TEST:@161200z4904.50N/07201.75W_090/005g030t095");

        var weather = (await engine.GetDashboardAsync(DashboardRange.Hour, Ct)).Weather!;

        Assert.Equal(2, weather.Stations);
        Assert.Equal("N0COLD", weather.Coldest!.Station);
        Assert.Equal(-10, weather.Coldest.Value, 1); // 14 °F
        Assert.Equal("N0WARM", weather.Warmest!.Station);
        Assert.Equal("N0WARM", weather.Gustiest!.Station);

        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, (await engine.GetDashboardAsync(DashboardRange.Hour, Ct)).Weather!.Stations);
    }

    [Fact]
    public async Task TheFarthestDirectHopsNeedTheIGatesPosition()
    {
        var (engine, _) = Create();
        engine.Accept(Direct); // heard directly by N0GATE, whose position isn't known yet
        Assert.Empty((await engine.GetDashboardAsync(DashboardRange.Hour, Ct)).FarthestDirect!);

        engine.Accept("N0GATE>APRS,TCPIP*,qAC,T2TEST:!4913.50N/07201.75W&"); // 10' of latitude north: ~18.5 km

        var hop = Assert.Single((await engine.GetDashboardAsync(DashboardRange.Hour, Ct)).FarthestDirect!);
        Assert.Equal(("N0CAR-9", "N0GATE"), (hop.Station, hop.Gateway));
        Assert.Equal(18.53, hop.DistanceKm, 2);
    }
}
