// SPDX-License-Identifier: MIT
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>GPS statistics on the dashboard (docs/features/F3.14-gps-dashboard.md, acceptance tests 1–7).</summary>
public sealed class GpsHistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse(
        "2026-10-25T12:00:00Z",
        CultureInfo.InvariantCulture
    );
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static GpsSample Fix(
        IReadOnlyList<GpsSkySatellite>? sky = null,
        GpsAccuracy? accuracy = null,
        double latitude = 39,
        double longitude = -94.7,
        double speed = 0
    ) => new("gps", GpsSampleState.Fix3D, accuracy, sky, latitude, longitude, speed);

    private static GpsSkySatellite Satellite(GnssConstellation system, int id, bool used, double snr = 40) =>
        new(system, id, null, 45, 90, snr, used);

    /// <summary>Records one sample a second from <paramref name="from"/> for <paramref name="seconds"/>.</summary>
    private static DateTimeOffset Run(GpsHistory history, DateTimeOffset from, int seconds, Func<int, GpsSample> sample)
    {
        for (var i = 0; i < seconds; i++)
        {
            history.Record(sample(i), from + (Second * (i + 1)), Second);
        }
        return from + (Second * seconds);
    }

    /// <summary>The last hour in a point a minute, as the dashboard asks.</summary>
    private static DashboardGps? LastHour(GpsHistory history, DateTimeOffset now)
    {
        var minute = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
        return history.Read(minute.AddMinutes(-59), TimeSpan.FromMinutes(1), 60, now);
    }

    [Fact]
    public void FiftyMinutesWithAFixAndTenWithoutGiveTheSharesTheLossAndTheOutage()
    {
        var history = new GpsHistory();
        var now = Run(history, Noon - Second, 50 * 60, _ => Fix()); // samples 12:00:00 to 12:59:59, the last hour's
        var lostAt = now + Second;
        now = Run(history, now, 10 * 60, _ => new GpsSample("gps", GpsSampleState.NoFix));

        var gps = LastHour(history, now)!;

        Assert.Equal(TimeSpan.FromMinutes(50), gps.Fix3D);
        Assert.Equal(TimeSpan.FromMinutes(10), gps.NoFix);
        Assert.Equal((TimeSpan.Zero, TimeSpan.Zero), (gps.Fix2D, gps.Silent));
        Assert.Equal(1, gps.Losses);
        Assert.Equal(lostAt, gps.LastOutage!.Start);
        Assert.True(gps.LastOutage.Ongoing);
        Assert.Equal(now - lostAt, gps.LongestOutage);
        Assert.Equal(("gps", false), (gps.SourceId, gps.OtherInUse));
        Assert.Equal(100, gps.FixShare[0]);
        Assert.Equal(0, gps.FixShare[^1]);
    }

    [Fact]
    public void ASilentGpsCountsAsSilentAndItsReturnEndsTheOutage()
    {
        var history = new GpsHistory();
        var now = Run(history, Noon, 30, _ => Fix());
        now = Run(history, now, 20, _ => new GpsSample("gps", GpsSampleState.Silent));
        now = Run(history, now, 10, _ => Fix());

        var gps = LastHour(history, now)!;

        Assert.Equal(TimeSpan.FromSeconds(20), gps.Silent);
        Assert.Equal(1, gps.Losses);
        Assert.False(gps.LastOutage!.Ongoing);
        Assert.Equal(TimeSpan.FromSeconds(20), gps.LastOutage.Length);
    }

    [Fact]
    public void SatellitesAreCountedBySystemNowAndOnAverage()
    {
        var history = new GpsHistory();
        GpsSkySatellite[] sky =
        [
            Satellite(GnssConstellation.Gps, 4, used: true, snr: 40),
            Satellite(GnssConstellation.Gps, 9, used: true, snr: 44),
            Satellite(GnssConstellation.Gps, 12, used: false),
            Satellite(GnssConstellation.BeiDou, 14, used: true, snr: 30),
            Satellite(GnssConstellation.BeiDou, 21, used: false),
            Satellite(GnssConstellation.BeiDou, 21, used: false), // the same satellite on a second signal
        ];
        var now = Run(history, Noon, 60, _ => Fix(sky));

        var gps = LastHour(history, now)!;

        Assert.Equal(new DashboardStat(3, 3, 3, 3), gps.SatellitesUsed);
        Assert.Equal(5, gps.SatellitesInView.Now);
        Assert.Equal([new(GnssConstellation.Gps, 2, 3, 2), new(GnssConstellation.BeiDou, 1, 2, 1)], gps.Constellations);
        Assert.Equal(38, gps.Signal.Now!.Value, 6); // the average of the satellites used
    }

    [Fact]
    public void TheDilutionsAndErrorsGiveNowTheAverageLowestAndHighest()
    {
        var history = new GpsHistory();
        var now = Run(
            history,
            Noon,
            4,
            i => Fix(accuracy: new GpsAccuracy(Hdop: 1 + i, Vdop: 2, HorizontalErrorMeters: 3 + i))
        );

        var gps = LastHour(history, now)!;

        Assert.Equal(new DashboardStat(4, 2.5, 1, 4), gps.Dilutions.Single(d => d.Key == "hdop").Stat);
        Assert.Equal(["hdop", "vdop"], gps.Dilutions.Select(d => d.Key)); // never given: no PDOP, TDOP or GDOP
        Assert.Equal(new DashboardStat(6, 4.5, 3, 6), gps.Errors.Single(e => e.Key == "horizontal").Stat);
        Assert.Single(gps.Errors);
        Assert.Equal(2.5, gps.HdopSeries[^1]);
    }

    [Fact]
    public void WanderIsTheSpreadOfStillFixesAndMovingOnesDontCount()
    {
        var history = new GpsHistory();
        // Four corners 10 m east, west, north and south of a point: every fix 10 m from the average, so 2DRMS is 20 m.
        var metre = 1 / 111_195.0;
        (double, double)[] corners = [(10 * metre, 0), (-10 * metre, 0), (0, 10 * metre), (0, -10 * metre)];
        var now = Run(history, Noon, 40, i => Fix(latitude: corners[i % 4].Item1, longitude: corners[i % 4].Item2));
        now = Run(history, now, 20, i => Fix(latitude: 1 + i, speed: 30)); // driving away

        var gps = LastHour(history, now)!;

        Assert.Equal(40, gps.WanderFixes);
        Assert.Equal(20, gps.WanderMeters!.Value, 1);
    }

    [Fact]
    public void NothingSampledGivesNoGps() =>
        Assert.Null(new GpsHistory().Read(Noon, TimeSpan.FromMinutes(1), 60, Noon.AddHours(1)));

    [Fact]
    public async Task TheEngineSamplesTheGpsInUseForTheDashboardAndTheMetrics()
    {
        var gpsSource = SourceSettings.GpsNetworkFor(GpsLink.Tcp, "127.0.0.1") with { Id = "gps" };
        var profile = InMemorySettingsStore.Profile("N0TEST") with { SourceIds = [gpsSource.Id] };
        var store = new InMemorySettingsStore(
            new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [gpsSource])
        );
        var time = new FakeTimeProvider(Noon);
        var factory = new EngineTests.FakeSourceFactory();
        var pipe = new Pipe();
        factory.Give(gpsSource.Id, new NmeaGpsSource(_ => Task.FromResult(pipe.Reader.AsStream()), "test", time));
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            factory,
            store,
            time,
            NullLogger<Engine>.Instance
        );

        var fixTime = new Dictionary<string, double>(StringComparer.Ordinal);
        long losses = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (
                instrument.Meter.Name == EngineTelemetry.Name
                && instrument.Name.StartsWith("aprsmap.gps.", StringComparison.Ordinal)
            )
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>(
            (_, value, tags, _) =>
            {
                lock (fixTime)
                {
                    var fix = (string)tags.ToArray().Single(t => t.Key == "fix").Value!;
                    fixTime[fix] = fixTime.GetValueOrDefault(fix) + value;
                }
            }
        );
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref losses, value));
        listener.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(GpsTests.Second("120000")), Ct);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Gps is { Fix: GpsFixKind.Fix3D });

        engine.SampleGps();
        time.Advance(GpsHistory.SilentAfter + Second); // the GPS says nothing more
        engine.SampleGps();

        // The engine's own sampler ticks with the clock too.
        var gps = (await engine.GetDashboardAsync(DashboardRange.Hour, null, Ct)).Gps!;
        Assert.True(gps.Fix3D >= Second && gps.Silent >= Second);
        Assert.Equal(1, gps.Losses);
        Assert.Equal(9, gps.SatellitesUsed.Average);
        lock (fixTime)
        {
            Assert.True(fixTime.GetValueOrDefault("3d") >= 1);
            Assert.True(fixTime.GetValueOrDefault("silent") >= 1);
        }
        Assert.True(Interlocked.Read(ref losses) >= 1); // other tests' engines may record too

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AProfileWithoutAGpsHasNoGpsOnTheDashboard()
    {
        var profile = InMemorySettingsStore.Profile("N0TEST");
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            new InMemorySettingsStore(new AppSettings([profile], profile.Id, UnitSettings.Metric)),
            new FakeTimeProvider(Noon),
            NullLogger<Engine>.Instance
        );
        engine.SampleGps();

        Assert.Null((await engine.GetDashboardAsync(DashboardRange.Hour, null, Ct)).Gps);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(20, Ct);
        }
    }
}
