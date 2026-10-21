// SPDX-License-Identifier: MIT
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The moving schedule: SmartBeaconing from the GPS (docs/features/F2.2-beacons.md, revised for #335).</summary>
public sealed class MovingBeaconTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Gps = SourceSettings.GpsNetworkFor(GpsLink.Tcp, "127.0.0.1") with
    {
        Id = "gps",
    };

    private static readonly PositionBeaconSettings Moving = new(
        On: true,
        Schedule: PositionBeaconSchedule.Moving,
        IntervalMinutes: 30
    );

    private sealed class Station(Engine engine, FakeTimeProvider time, Pipe gps, EngineTests.FakeSourceFactory sources)
        : IDisposable
    {
        private int _seconds;

        public Engine Engine { get; } = engine;

        public FakeTimeProvider Time { get; } = time;

        public EngineTests.FakeSourceFactory Sources { get; } = sources;

        public void Dispose() => Engine.Dispose();

        /// <summary>
        /// Drives for <paramref name="seconds"/> at <paramref name="knots"/> on <paramref name="course"/>: a fix every
        /// <paramref name="step"/> seconds (fresh for 60), the clock moving with it.
        /// </summary>
        public async Task DriveAsync(int seconds, double knots, double course, int step = 10)
        {
            for (var t = 0; t < seconds; t += step)
            {
                var hms = TimeSpan.FromSeconds(_seconds).ToString(@"hhmmss", CultureInfo.InvariantCulture);
                var speed = knots.ToString("000.0", CultureInfo.InvariantCulture);
                var heading = course.ToString("000.0", CultureInfo.InvariantCulture);
                var text =
                    GpsTests.Nmea($"GPRMC,{hms},A,4903.500,N,07201.750,W,{speed},{heading},211026,,,A")
                    + GpsTests.Nmea($"GPGGA,{hms},4903.500,N,07201.750,W,1,09,0.9,545.4,M,46.9,M,,")
                    + GpsTests.Nmea("GPGSA,A,3,04,05,09,12,24,25,29,31,32,,,,2.5,0.9,2.1");
                await gps.Writer.WriteAsync(Encoding.ASCII.GetBytes(text), Ct);
                await FixTakenAsync(); // the engine has the fix before the clock moves on
                Time.Advance(TimeSpan.FromSeconds(step));
                _seconds += step;
                await Task.Delay(TimeSpan.FromMilliseconds(30), TimeProvider.System, Ct);
            }
        }

        private async Task FixTakenAsync()
        {
            var now = Time.GetUtcNow();
            for (var i = 0; i < 500; i++)
            {
                if ((await Engine.GetStatusAsync(Ct)).Gps is { } gps && gps.At == now)
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
            }
            Assert.Fail("The engine didn't take the GPS fix.");
        }
    }

    private static Station Create(PositionBeaconSettings beacon)
    {
        var home = InMemorySettingsStore.Profile("N0TEST") with
        {
            Passcode = 12345,
            Latitude = 40,
            Longitude = -105,
            Licensed = true,
            PositionConfirmed = true,
            SourceIds = [AprsIs.Id, Gps.Id],
            PositionBeacon = beacon,
        };
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home],
                home.Id,
                UnitSettings.Metric,
                Sources: [AprsIs, Gps],
                Toggles: StationToggles.AllOn with
                {
                    Transmit = true,
                }
            )
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T00:00:00Z", CultureInfo.InvariantCulture));
        var factory = new EngineTests.FakeSourceFactory();
        var pipe = new Pipe();
        factory.Give(Gps.Id, new NmeaGpsSource(_ => Task.FromResult(pipe.Reader.AsStream()), "test", time));
        var engine = new Engine(Options.Create(new EngineOptions()), factory, store, time, NullLogger<Engine>.Instance);
        return new Station(engine, time, pipe, factory);
    }

    private static int Positions(EngineTests.FakeSource source) =>
        source.Sent.Count(l => AprsPacket.Parse(l).Payload is PositionReport);

    /// <summary>Waits for the <paramref name="count"/>th position to go out (slow runners), then checks no more did.</summary>
    private static async Task ExpectPositions(EngineTests.FakeSource source, int count)
    {
        for (var i = 0; i < 500 && Positions(source) < count; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
        Assert.Equal(count, Positions(source));
    }

    private static async Task<(Task Run, CancellationTokenSource Cts, EngineTests.FakeSource AprsIs)> Run(
        Station station
    )
    {
        var aprsIs = station.Sources.Enqueue("T2TEST", verified: true);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = station.Engine.RunAsync(cts.Token);
        for (var i = 0; i < 400; i++)
        {
            if (
                (await station.Engine.GetStatusAsync(Ct)).Sources is { Count: 2 } sources
                && sources.All(s => s.State == ConnectionState.Connected)
            )
            {
                return (run, cts, aprsIs);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
        Assert.Fail("The sources didn't connect.");
        return default;
    }

    [Fact]
    public async Task AtSpeedItBeaconsEveryNinetySecondsAndOnATurn()
    {
        using var station = Create(Moving);
        var (run, cts, aprsIs) = await Run(station);
        using (cts)
        {
            await station.DriveAsync(10, 60, 90); // 69 mph: the first beacon at once
            await ExpectPositions(aprsIs, 1);

            await station.DriveAsync(70, 60, 90);
            Assert.Equal(1, Positions(aprsIs)); // 80 s since the first: not yet
            await station.DriveAsync(20, 60, 90);
            await ExpectPositions(aprsIs, 2);

            await station.DriveAsync(20, 60, 180); // a right turn of 90°: at once, after the 10 s turn time
            await ExpectPositions(aprsIs, 3);
            var turned = Assert.IsType<PositionReport>(AprsPacket.Parse(aprsIs.Sent.Last()).Payload);
            Assert.Equal(180, turned.Course);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task StoppedItBeaconsAtTheSlowRate()
    {
        using var station = Create(Moving);
        var (run, cts, aprsIs) = await Run(station);
        using (cts)
        {
            await station.DriveAsync(10, 0, 0);
            await ExpectPositions(aprsIs, 1);

            await station.DriveAsync(25 * 60, 0, 0, step: 50);
            await ExpectPositions(aprsIs, 1);
            await station.DriveAsync(6 * 60, 0, 0, step: 50); // 30 minutes stopped
            await ExpectPositions(aprsIs, 2);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task WithoutAGpsFixItBeaconsAtTheFixedInterval()
    {
        using var station = Create(Moving with { IntervalMinutes = 10 });
        var (run, cts, aprsIs) = await Run(station);
        using (cts)
        {
            station.Time.Advance(Beaconer.MaxStartWait);
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            await ExpectPositions(aprsIs, 1);
            var fixedSpot = Assert.IsType<PositionReport>(AprsPacket.Parse(aprsIs.Sent.Last()).Payload);
            Assert.Equal(40, (int)Math.Round(fixedSpot.Position.Latitude)); // the profile's location

            station.Time.Advance(TimeSpan.FromMinutes(10));
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            await ExpectPositions(aprsIs, 2);
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(4.3, 1800, 52.1, 1900)] // the fast rate slower than the slow one
    [InlineData(60, 1800, 10, 90)] // the slow speed above the fast one
    public async Task UnusableMovingSettingsAreRefused(double slow, int slowRate, double fast, int fastRate)
    {
        using var station = Create(Moving);
        var settings = await station.Engine.GetSettingsAsync(Ct);
        var profile = settings.GetActiveProfile()! with
        {
            PositionBeacon = Moving with { Moving = new MovingBeaconSettings(slow, slowRate, fast, fastRate) },
        };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            station.Engine.SaveSettingsAsync(settings with { Profiles = [profile] }, Ct)
        );

        Assert.Equal(EngineErrorCodes.ProfileMovingInvalid, error.EngineError.Code);
    }
}
