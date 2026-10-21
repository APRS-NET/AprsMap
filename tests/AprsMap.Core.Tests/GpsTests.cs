// SPDX-License-Identifier: MIT
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>GPS sources (docs/features/F3.6-gps.md, acceptance tests 1–6).</summary>
public sealed class GpsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Gps = SourceSettings.GpsNetworkFor(GpsLink.Tcp, "127.0.0.1") with
    {
        Id = "gps",
    };

    /// <summary>An NMEA sentence with its checksum.</summary>
    internal static string Nmea(string body)
    {
        var sum = 0;
        foreach (var c in body)
        {
            sum ^= c;
        }
        return string.Create(CultureInfo.InvariantCulture, $"${body}*{sum:X2}\r\n");
    }

    /// <summary>
    /// One second of a GPS at <paramref name="lat"/> (N) and <paramref name="lon"/> (W, in degrees and minutes as
    /// NMEA writes them), moving at 22.4 knots on course 84.4, 545.4 m up, 9 satellites, a 3D fix.
    /// </summary>
    internal static string Second(string time, string lat = "4903.500", string lon = "07201.750") =>
        Nmea($"GPRMC,{time},A,{lat},N,{lon},W,022.4,084.4,211026,,,A")
        + Nmea($"GPGGA,{time},{lat},N,{lon},W,1,09,0.9,545.4,M,46.9,M,,")
        + Nmea("GPGSA,A,3,04,05,09,12,24,25,29,31,32,,,,2.5,0.9,2.1");

    private sealed record Station(Engine Engine, EngineTests.FakeSourceFactory Sources, FakeTimeProvider Time, Pipe Gps)
        : IDisposable
    {
        public void Dispose() => Engine.Dispose();

        public async Task SendAsync(string text)
        {
            await Gps.Writer.WriteAsync(Encoding.ASCII.GetBytes(text), Ct);
        }
    }

    private static Station Create(PositionBeaconSettings? beacon = null, int range = 50)
    {
        var home = InMemorySettingsStore.Profile("N0TEST") with
        {
            Passcode = 12345,
            Latitude = 40,
            Longitude = -105,
            RangeKm = range,
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
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T12:00:00Z", CultureInfo.InvariantCulture));
        var factory = new EngineTests.FakeSourceFactory();
        var pipe = new Pipe();
        factory.Give(Gps.Id, new NmeaGpsSource(_ => Task.FromResult(pipe.Reader.AsStream()), "test", time));
        var engine = new Engine(Options.Create(new EngineOptions()), factory, store, time, NullLogger<Engine>.Instance);
        return new Station(engine, factory, time, pipe);
    }

    private static async Task<(Task Run, CancellationTokenSource Cts, EngineTests.FakeSource AprsIs)> Run(
        Station station
    )
    {
        var aprsIs = station.Sources.Enqueue("T2TEST", verified: true);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = station.Engine.RunAsync(cts.Token);
        await WaitUntil(async () =>
            (await station.Engine.GetStatusAsync(Ct)).Sources is { Count: 2 } sources
            && sources.All(s => s.State == ConnectionState.Connected)
        );
        return (run, cts, aprsIs);
    }

    [Fact]
    public async Task NmeaSentencesGiveAThreeDFixInTheStatusAndTheConsole()
    {
        using var station = Create();
        var (run, cts, _) = await Run(station);
        using (cts)
        {
            Assert.Equal(GpsFixKind.NoFix, (await station.Engine.GetStatusAsync(Ct)).Gps?.Fix);

            await station.SendAsync(Second("120000"));
            await WaitUntil(async () =>
                (await station.Engine.GetStatusAsync(Ct)).Gps is { Satellites: 9, Fix: GpsFixKind.Fix3D }
            ); // GSA, the second's last sentence, says 3D

            var gps = (await station.Engine.GetStatusAsync(Ct)).Gps!;
            Assert.Equal((GpsFixKind.Fix3D, true), (gps.Fix, gps.IsFresh));
            Assert.Equal(49.0583, gps.Latitude!.Value, 4);
            Assert.Equal(-72.0292, gps.Longitude!.Value, 4);
            Assert.Equal((22.4, 84.4, 545.4, 0.9), (gps.SpeedKnots, gps.Course, gps.AltitudeMeters, gps.Hdop));
            using var watch = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            watch.CancelAfter(TimeSpan.FromSeconds(1));
            var lines = new List<TrafficEntry>();
            try
            {
                await foreach (var entry in station.Engine.WatchSourceTrafficAsync(Gps.Id, watch.Token))
                {
                    lines.Add(entry);
                    if (lines.Count >= 3)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested) { }
            Assert.Contains(
                lines,
                e => e.Kind == TrafficKind.GpsSentence && e.Text!.StartsWith("$GPRMC", StringComparison.Ordinal)
            );
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ANewSecondsFirstSentenceKeepsTheStationWhereItIsAndTheFixAsItWas()
    {
        using var station = Create();
        var (run, cts, _) = await Run(station);
        using (cts)
        {
            await station.SendAsync(Second("120000"));
            await WaitUntil(async () =>
                (await station.Engine.GetStatusAsync(Ct)).Gps is { Satellites: 9, Fix: GpsFixKind.Fix3D }
            ); // GSA, the second's last sentence, says 3D

            // The next second's RMC, before its GGA and GSA say the rest.
            await station.SendAsync(Nmea("GPRMC,120001,A,4903.510,N,07201.750,W,022.4,084.4,211026,,,A"));
            await WaitUntil(async () => (await station.Engine.GetStatusAsync(Ct)).Gps is { Latitude: > 49.0584 });

            var gps = (await station.Engine.GetStatusAsync(Ct)).Gps!;
            Assert.True(gps.HasPosition);
            Assert.Equal(GpsFixKind.Fix3D, gps.Fix);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AFreshFixIsWhereTheStationIsAndAStaleOneFallsBackToTheProfile()
    {
        using var station = Create();
        var (run, cts, aprsIs) = await Run(station);
        using (cts)
        {
            await station.SendAsync(Second("120000"));
            // The whole second: RMC's position, then GGA's altitude and GSA's satellites.
            await WaitUntil(async () =>
                (await station.Engine.GetStatusAsync(Ct)).Gps
                    is { HasPosition: true, Satellites: 9, AltitudeMeters: not null }
            );

            await station.Engine.SendPositionAsync(AprsIs.Id, Ct);
            var moving = Assert.IsType<PositionReport>(AprsPacket.Parse(aprsIs.Sent.Last()).Payload);
            Assert.Equal(49.0583, Math.Round(moving.Position.Latitude, 4));
            Assert.Equal((84, 22), (moving.Course, (int)Math.Round(moving.SpeedKnots!.Value)));
            Assert.Equal(1789, (int)Math.Round(moving.AltitudeFeet!.Value)); // 545.4 m

            station.Time.Advance(Engine.GpsFreshFor + TimeSpan.FromSeconds(1));
            Assert.False((await station.Engine.GetStatusAsync(Ct)).Gps!.IsFresh);
            await station.Engine.SendPositionAsync(AprsIs.Id, Ct);
            var fixedSpot = Assert.IsType<PositionReport>(AprsPacket.Parse(aprsIs.Sent.Last()).Payload);
            Assert.Equal(
                (40, -105),
                ((int)Math.Round(fixedSpot.Position.Latitude), (int)Math.Round(fixedSpot.Position.Longitude))
            );
            Assert.Null(fixedSpot.Course);
            Assert.Equal(40, (await station.Engine.GetSettingsAsync(Ct)).GetActiveProfile()!.Latitude); // never overwritten
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task TheFiltersRangeFollowsTheGpsOnceItMovesAQuarterOfTheRange()
    {
        using var station = Create(range: 50);
        var (run, cts, aprsIs) = await Run(station);
        using (cts)
        {
            await station.SendAsync(Second("120000"));
            await WaitUntil(async () => (await station.Engine.GetStatusAsync(Ct)).Gps is { HasPosition: true });
            station.Time.Advance(TimeSpan.FromSeconds(10)); // the filter interval since the login's
            await WaitUntil(() =>
                Task.FromResult(
                    aprsIs.Filters.Any(f => f.StartsWith("r/49.0583/-72.0292/50", StringComparison.Ordinal))
                )
            );
            var sent = aprsIs.Filters.Count;

            station.Time.Advance(TimeSpan.FromSeconds(10)); // past the filter interval
            await station.SendAsync(Second("120010", lat: "4904.000")); // about 1 km: not far enough
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            Assert.Equal(sent, aprsIs.Filters.Count);

            station.Time.Advance(TimeSpan.FromSeconds(10));
            await station.SendAsync(Second("120020", lat: "4910.000")); // about 12 km: a quarter of 50 is 12.5
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            Assert.Equal(sent, aprsIs.Filters.Count);

            station.Time.Advance(TimeSpan.FromSeconds(10));
            await station.SendAsync(Second("120030", lat: "4915.000")); // about 21 km
            await WaitUntil(() => Task.FromResult(aprsIs.Filters.Count == sent + 1));
            Assert.StartsWith("r/49.25/-72.0292/50", aprsIs.Filters.Last(), StringComparison.Ordinal);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ABeaconGoesOutWhereTheGpsSays()
    {
        using var station = Create(
            new PositionBeaconSettings(On: true, Schedule: PositionBeaconSchedule.Fixed, IntervalMinutes: 30)
        );
        var (run, cts, aprsIs) = await Run(station);
        using (cts)
        {
            // A fix every 20 s until the first beacon, due within its first three minutes, goes out.
            for (var second = 0; aprsIs.Sent.IsEmpty && second < 300; second += 20)
            {
                await station.SendAsync(
                    Second(string.Create(CultureInfo.InvariantCulture, $"12{second / 60:00}{second % 60:00}"))
                );
                await WaitUntil(async () => (await station.Engine.GetStatusAsync(Ct)).Gps is { HasPosition: true });
                station.Time.Advance(TimeSpan.FromSeconds(20));
                await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
            }
            await WaitUntil(() => Task.FromResult(!aprsIs.Sent.IsEmpty));

            var beacon = Assert.IsType<PositionReport>(AprsPacket.Parse(aprsIs.Sent.First()).Payload);
            Assert.Equal(49.0583, Math.Round(beacon.Position.Latitude, 4));
            Assert.Equal(84, beacon.Course);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task NmeaOverTcpIsReadFromTheSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var factory = new AprsIsPacketSourceFactory(
            Options.Create(new EngineOptions()),
            NullLoggerFactory.Instance,
            TimeProvider.System
        );
        var source = factory.Create(
            InMemorySettingsStore.Profile("N0TEST"),
            SourceSettings.GpsNetworkFor(GpsLink.Tcp, "127.0.0.1", port),
            string.Empty
        );
        await using (source)
        {
            var fixes = new System.Collections.Concurrent.ConcurrentQueue<GpsFix>();
            ((IGpsSource)source).Fixes = fixes.Enqueue;
            var accepting = listener.AcceptTcpClientAsync(Ct);
            var login = await source.StartAsync(_ => { }, Ct);
            using var client = await accepting;
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Second("120000")), Ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var reading = Task.Run(
                async () =>
                {
                    await foreach (var _ in source.ReadPacketsAsync(stop.Token)) { }
                },
                Ct
            );

            await WaitUntil(() => Task.FromResult(fixes.Any(f => f.Satellites == 9)));

            Assert.Equal($"127.0.0.1:{port}", login.Server);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        }
    }

    [Fact]
    public async Task GpsdsFixesComeThroughAPRSNETsClient()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var factory = new AprsIsPacketSourceFactory(
            Options.Create(new EngineOptions()),
            NullLoggerFactory.Instance,
            TimeProvider.System
        );
        var source = factory.Create(
            InMemorySettingsStore.Profile("N0TEST"),
            SourceSettings.GpsNetworkFor(GpsLink.Gpsd, "127.0.0.1", port),
            string.Empty
        );
        await using (source)
        {
            var fixes = new System.Collections.Concurrent.ConcurrentQueue<GpsFix>();
            ((IGpsSource)source).Fixes = fixes.Enqueue;
            var gpsd = Task.Run(
                async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync(Ct);
                    var stream = client.GetStream();
                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes(
                            "{\"class\":\"VERSION\",\"release\":\"3.25\",\"proto_major\":3,\"proto_minor\":15}\n"
                                + "{\"class\":\"TPV\",\"mode\":3,\"time\":\"2026-10-21T12:00:00.000Z\",\"lat\":49.0583,"
                                + "\"lon\":-72.0292,\"altMSL\":545.4,\"speed\":11.52,\"track\":84.4}\n"
                        ),
                        Ct
                    );
                    await Task.Delay(TimeSpan.FromSeconds(5), TimeProvider.System, Ct);
                },
                Ct
            );
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var login = await source.StartAsync(_ => { }, Ct);
            var reading = Task.Run(
                async () =>
                {
                    await foreach (var _ in source.ReadPacketsAsync(stop.Token)) { }
                },
                Ct
            );

            await WaitUntil(() => Task.FromResult(!fixes.IsEmpty));

            var fix = fixes.First();
            Assert.Equal((49.0583, -72.0292), (fix.Position!.Value.Latitude, fix.Position.Value.Longitude));
            Assert.Equal(GpsFixMode.Fix3D, fix.Mode);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        }
    }

    [Theory]
    [InlineData(GpsLink.Serial, null, null)]
    [InlineData(GpsLink.Tcp, null, null)]
    [InlineData(GpsLink.Gpsd, "pi.local", 70000)]
    public async Task AGpsWithoutWhereToReadItIsRefused(GpsLink link, string? host, int? port)
    {
        var store = InMemorySettingsStore.WithProfile();
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            store,
            TimeProvider.System,
            NullLogger<Engine>.Instance
        );
        var settings = await engine.GetSettingsAsync(Ct);

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(
                settings with
                {
                    Sources =
                    [
                        .. settings.SourceList,
                        new("gps", "GPS", SourceKind.Gps, Host: host, Port: port, GpsLink: link),
                    ],
                },
                Ct
            )
        );

        Assert.Equal(EngineErrorCodes.GpsSourceInvalid, error.EngineError.Code);
    }

    [Fact]
    public async Task AProfileUsesOneGps()
    {
        var store = InMemorySettingsStore.WithProfile();
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            store,
            TimeProvider.System,
            NullLogger<Engine>.Instance
        );
        var settings = await engine.GetSettingsAsync(Ct);
        var one = SourceSettings.GpsNetworkFor(GpsLink.Gpsd, "localhost");
        var two = SourceSettings.GpsSerialFor("/dev/ttyUSB0");
        var profile = settings.GetActiveProfile()!;

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(
                settings with
                {
                    Sources = [.. settings.SourceList, one, two],
                    Profiles = [profile with { SourceIds = [.. profile.SourceIds, one.Id, two.Id] }],
                },
                Ct
            )
        );

        Assert.Equal(EngineErrorCodes.ProfileTwoGps, error.EngineError.Code);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
        Assert.Fail("Condition was not met in time.");
    }
}
