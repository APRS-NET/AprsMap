// SPDX-License-Identifier: MIT
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Where others see the station (docs/features/F2.3-seen-as.md, acceptance tests 1–4).</summary>
public sealed class SeenAsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Gps = SourceSettings.GpsNetworkFor(GpsLink.Tcp, "127.0.0.1") with
    {
        Id = "gps",
    };

    private sealed record Station(Engine Engine, FakeTimeProvider Time, Pipe Gps, EngineTests.FakeSourceFactory Sources)
        : IDisposable
    {
        public void Dispose() => Engine.Dispose();

        /// <summary>A fix at 49°N and <paramref name="west"/> degrees west, at <paramref name="knots"/> on 090.</summary>
        public async Task FixAsync(string time, double west, double knots)
        {
            var degrees = (int)west;
            var lon = string.Create(CultureInfo.InvariantCulture, $"{degrees:000}{(west - degrees) * 60:00.000}");
            var speed = knots.ToString("000.0", CultureInfo.InvariantCulture);
            var text =
                GpsTests.Nmea($"GPRMC,{time},A,4900.000,N,{lon},W,{speed},090.0,211026,,,A")
                + GpsTests.Nmea($"GPGGA,{time},4900.000,N,{lon},W,1,09,0.9,545.4,M,46.9,M,,")
                + GpsTests.Nmea("GPGSA,A,3,04,05,09,12,24,25,29,31,32,,,,2.5,0.9,2.1");
            await Gps.Writer.WriteAsync(Encoding.ASCII.GetBytes(text), Ct);
            for (var i = 0; i < 400; i++)
            {
                if (
                    (await Engine.GetStatusAsync(Ct)).Gps is { HasPosition: true, Longitude: var at }
                    && Math.Abs(at!.Value + west) < 1e-4
                )
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
            }
            Assert.Fail("The fix didn't arrive.");
        }

        public async Task<SeenPosition?> SeenAsync() => (await Engine.GetStatusAsync(Ct)).Transmit?.SeenAt;
    }

    private static Station Create()
    {
        var home = InMemorySettingsStore.Profile("N0TEST") with
        {
            Passcode = 12345,
            Latitude = 40,
            Longitude = -105,
            Licensed = true,
            PositionConfirmed = true,
            SourceIds = [AprsIs.Id, Gps.Id],
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
        return new Station(engine, time, pipe, factory);
    }

    private static async Task<(Task Run, CancellationTokenSource Cts)> Run(Station station)
    {
        station.Sources.Enqueue("T2TEST", verified: true);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = station.Engine.RunAsync(cts.Token);
        for (var i = 0; i < 400; i++)
        {
            if (
                (await station.Engine.GetStatusAsync(Ct)).Sources is { Count: 2 } sources
                && sources.All(s => s.State == ConnectionState.Connected)
            )
            {
                return (run, cts);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
        Assert.Fail("The sources didn't connect.");
        return default;
    }

    [Fact]
    public async Task OthersSeeTheStationMovingOnFromItsLastPosition()
    {
        using var station = Create();
        var (run, cts) = await Run(station);
        using (cts)
        {
            Assert.Null(await station.SeenAsync()); // nothing sent yet
            await station.FixAsync("120000", 72, 60); // 60 knots east
            await station.Engine.SendPositionAsync(AprsIs.Id, Ct);
            Assert.True((await station.SeenAsync())!.DistanceKm < 0.01);

            station.Time.Advance(TimeSpan.FromMinutes(2));
            await station.FixAsync("120200", 72, 0); // stopped where the position was sent

            var stopped = (await station.SeenAsync())!;
            Assert.InRange(stopped.DistanceKm, 3.6, 3.8); // 60 knots for 2 minutes: 3.7 km on
            Assert.True(stopped.Longitude > -72); // east of here
            await station.FixAsync("120201", 72 - (stopped.Longitude + 72), 0); // where they see it
            Assert.True((await station.SeenAsync())!.DistanceKm < 0.1);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task WithoutAFreshFixOrLongAfterNothingIsShown()
    {
        using var station = Create();
        var (run, cts) = await Run(station);
        using (cts)
        {
            await station.FixAsync("120000", 72, 0);
            await station.Engine.SendPositionAsync(AprsIs.Id, Ct);
            Assert.NotNull(await station.SeenAsync());

            station.Time.Advance(Engine.GpsFreshFor + TimeSpan.FromSeconds(1));
            Assert.Null(await station.SeenAsync()); // the GPS isn't fresh: where the station is isn't known

            await station.FixAsync("120200", 72, 0);
            Assert.NotNull(await station.SeenAsync());
            station.Time.Advance(Engine.SeenFor);
            await station.FixAsync("123200", 72, 0);
            Assert.Null(await station.SeenAsync()); // 30 minutes after the last position
            await cts.CancelAsync();
            await run;
        }
    }
}
