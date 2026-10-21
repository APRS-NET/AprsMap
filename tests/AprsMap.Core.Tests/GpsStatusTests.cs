// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
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

/// <summary>GPS status: accuracy, the sky and gpsd's lines (docs/features/F3.12-gps-status.md, acceptance tests 1, 2).</summary>
public sealed class GpsStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A second with five satellites in view, three of them used, and GST's error estimate.</summary>
    private static readonly string Sky =
        GpsTests.Nmea("GPRMC,120000,A,4903.500,N,07201.750,W,022.4,084.4,211026,,,A")
        + GpsTests.Nmea("GPGGA,120000,4903.500,N,07201.750,W,1,03,0.9,545.4,M,46.9,M,,")
        + GpsTests.Nmea("GPGSA,A,3,04,05,09,,,,,,,,,,2.5,0.9,2.1")
        + GpsTests.Nmea("GPGSV,2,1,05,04,45,120,42,05,30,250,38,09,70,010,45,12,05,300,")
        + GpsTests.Nmea("GPGSV,2,2,05,24,20,180,22")
        + GpsTests.Nmea("GPGST,120000,1.2,3.0,2.0,45.0,2.4,1.8,4.5");

    [Fact]
    public async Task NmeaGivesTheSatellitesInViewTheDopsAndTheError()
    {
        var pipe = new Pipe();
        await using var source = new NmeaGpsSource(
            _ => Task.FromResult(pipe.Reader.AsStream()),
            "test",
            TimeProvider.System
        );
        var skies = new ConcurrentQueue<(GpsQuality Quality, IReadOnlyList<GpsSatellite> Satellites)>();
        source.Sky = (q, s) => skies.Enqueue((q, s));
        await source.StartAsync(_ => { }, Ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reading = Task.Run(
            async () =>
            {
                await foreach (var _ in source.ReadPacketsAsync(stop.Token)) { }
            },
            Ct
        );

        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(Sky), Ct);
        await WaitUntil(() => skies.LastOrDefault().Quality?.HorizontalErrorMeters is not null);

        var (quality, satellites) = skies.Last();
        Assert.Equal(5, satellites.Count);
        Assert.Equal([4, 5, 9], satellites.Where(s => s.Used).Select(s => s.Id).Order());
        var four = satellites.Single(s => s.Id == 4);
        Assert.Equal((45, 120, 42), ((int?)four.ElevationDegrees, (int?)four.AzimuthDegrees, (int?)four.SnrDbHz));
        Assert.Null(satellites.Single(s => s.Id == 12).SnrDbHz); // seen, not tracked
        Assert.Equal((2.5, 0.9, 2.1), (quality.Pdop, quality.Hdop, quality.Vdop));
        Assert.Equal(3.0, Math.Round(quality.HorizontalErrorMeters!.Value, 1)); // √(2.4² + 1.8²)
        Assert.Equal(4.5, quality.AltitudeErrorMeters);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    [Fact]
    public async Task TheStatusCarriesTheAccuracyAndTheSkyStrongestFirstInTheList()
    {
        var aprsIs = InMemorySettingsStore.Rotate;
        var gps = SourceSettings.GpsNetworkFor(GpsLink.Tcp, "127.0.0.1") with { Id = "gps" };
        var home = InMemorySettingsStore.Profile("N0TEST") with { SourceIds = [aprsIs.Id, gps.Id] };
        var store = new InMemorySettingsStore(
            new AppSettings([home], home.Id, UnitSettings.Metric, Sources: [aprsIs, gps])
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T12:00:00Z", CultureInfo.InvariantCulture));
        var factory = new EngineTests.FakeSourceFactory();
        var pipe = new Pipe();
        factory.Give(gps.Id, new NmeaGpsSource(_ => Task.FromResult(pipe.Reader.AsStream()), "test", time));
        factory.Enqueue("T2TEST");
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            factory,
            store,
            time,
            NullLogger<Engine>.Instance
        );
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await WaitUntil(() =>
            engine.GetStatusAsync(Ct).Result.Sources is { } sources
            && sources.Any(s => s.Id == gps.Id && s.State == ConnectionState.Connected)
        );

        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(Sky), Ct);
        await WaitUntil(() => engine.GetStatusAsync(Ct).Result.Gps?.Accuracy?.HorizontalErrorMeters is not null);

        var status = (await engine.GetStatusAsync(Ct)).Gps!;
        Assert.Equal((3, 5), (status.Accuracy!.SatellitesUsed, status.Accuracy.SatellitesInView));
        Assert.Equal(5, status.Sky!.Count);
        Assert.All(status.Sky, s => Assert.Equal(GnssConstellation.Gps, s.System));
        Assert.True(status.Sky.Single(s => s.Id == 9).Used);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task GpsdsSkyAndErrorsComeThroughAndItsLinesReachTheConsole()
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
            var skies = new ConcurrentQueue<(GpsQuality Quality, IReadOnlyList<GpsSatellite> Satellites)>();
            var lines = new ConcurrentQueue<TrafficEntry>();
            ((IGpsSource)source).Sky = (q, s) => skies.Enqueue((q, s));
            ((ITrafficSource)source).WatchTraffic(lines.Enqueue);
            var gpsd = Task.Run(
                async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync(Ct);
                    var stream = client.GetStream();
                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes(
                            "{\"class\":\"VERSION\",\"release\":\"3.25\",\"proto_major\":3,\"proto_minor\":15}\n"
                                + "{\"class\":\"SKY\",\"hdop\":0.9,\"pdop\":1.6,\"vdop\":1.3,\"satellites\":["
                                + "{\"PRN\":4,\"gnssid\":0,\"el\":45,\"az\":120,\"ss\":42,\"used\":true},"
                                + "{\"PRN\":67,\"gnssid\":6,\"el\":20,\"az\":200,\"ss\":30,\"used\":false}]}\n"
                                + "{\"class\":\"TPV\",\"mode\":3,\"time\":\"2026-10-21T12:00:00.000Z\",\"lat\":49.0583,"
                                + "\"lon\":-72.0292,\"altMSL\":545.4,\"speed\":11.52,\"track\":84.4,\"eph\":3.1,\"epv\":5.2}\n"
                        ),
                        Ct
                    );
                    await Task.Delay(TimeSpan.FromSeconds(5), TimeProvider.System, Ct);
                },
                Ct
            );
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            await source.StartAsync(_ => { }, Ct);
            var reading = Task.Run(
                async () =>
                {
                    await foreach (var _ in source.ReadPacketsAsync(stop.Token)) { }
                },
                Ct
            );

            await WaitUntil(() => !skies.IsEmpty);

            var (quality, satellites) = skies.Last();
            Assert.Equal((3.1, 5.2), (quality.HorizontalErrorMeters, quality.AltitudeErrorMeters));
            Assert.Equal(2, satellites.Count);
            Assert.Equal(GnssSystem.Glonass, satellites.Single(s => !s.Used).System);
            Assert.Contains(
                lines,
                l => l.Kind == TrafficKind.GpsdReport && l.Text!.Contains("\"SKY\"", StringComparison.Ordinal)
            );
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
