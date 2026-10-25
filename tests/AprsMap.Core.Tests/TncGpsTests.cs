// SPDX-License-Identifier: MIT
using System.IO.Pipelines;
using System.Text;
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>A radio's GPS on its TNC port (docs/features/F3.13-tnc-gps.md, acceptance tests 1–5).</summary>
public sealed class TncGpsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EngineOptions Quick = new()
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(100),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
        TncGpsTimeout = TimeSpan.FromMilliseconds(400),
    };

    /// <summary>A second of a radio with a 3D fix at 49°03.5′N 72°01.75′W, at <paramref name="time"/>.</summary>
    private static string Second(string time = "120000") =>
        GpsTests.Nmea($"GPRMC,{time},A,4903.500,N,07201.750,W,000.0,000.0,251026,,,A")
        + GpsTests.Nmea($"GPGGA,{time},4903.500,N,07201.750,W,1,07,1.1,210.0,M,-32.0,M,,")
        + GpsTests.Nmea("GPGSA,A,3,04,05,09,12,24,25,29,,,,,,2.0,1.1,1.6");

    private static readonly string Fix = Second();

    /// <summary>A TH-D72A without a fix: its placeholder position, GGA quality 0, RMC void, GSA mode 1.</summary>
    private static readonly string NoFix =
        GpsTests.Nmea("GPRMC,120000,V,3600.0000,N,13600.0000,E,,,251026,,,N")
        + GpsTests.Nmea("GPGGA,120000,3600.0000,N,13600.0000,E,0,00,,,M,,M,,")
        + GpsTests.Nmea("GPGSA,A,1,,,,,,,,,,,,,,,");

    private static Engine CreateOnSerial(SourceSettings tnc)
    {
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, tnc.Id],
        };
        return new Engine(
            Options.Create(Quick),
            new AprsIsPacketSourceFactory(Options.Create(Quick), NullLoggerFactory.Instance, TimeProvider.System),
            new InMemorySettingsStore(
                new AppSettings(
                    [profile],
                    profile.Id,
                    UnitSettings.Metric,
                    Sources: [InMemorySettingsStore.Rotate with { Server = "127.0.0.1:1" }, tnc]
                )
            ),
            TimeProvider.System,
            NullLogger<Engine>.Instance
        );
    }

    private static async Task<(Engine Engine, Task Run, CancellationTokenSource Cts)> RunOnSerialAsync(
        SourceSettings tnc
    )
    {
        var engine = CreateOnSerial(tnc);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Sources?.FirstOrDefault(s => s.Id == tnc.Id)?.State
            == ConnectionState.Connected
        );
        return (engine, run, cts);
    }

    private static SourceSettings TextSerial(string port, TncGps? gps = null) =>
        SourceSettings.AprsTextSerialFor(port) with
        {
            DtrEnable = false,
            RtsEnable = false,
            TncGps = gps,
        };

    [Fact]
    public async Task TheRadiosGpsInItsAprsModePlacesTheStationBesideItsPackets()
    {
        await using var pair = await PtyPair.StartAsync();
        var tnc = TextSerial(pair.Tnc); // Auto, by default
        var (engine, run, cts) = await RunOnSerialAsync(tnc);
        using (engine)
        using (cts)
        {
            await pair.WriteAsync(Encoding.ASCII.GetBytes(Fix + "N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>\r\n"));

            // The whole second: GSA, its last sentence, says 3D.
            await WaitUntil(async () =>
                (await engine.GetStatusAsync(Ct)).Gps is { HasPosition: true, Fix: GpsFixKind.Fix3D }
            );
            var gps = (await engine.GetStatusAsync(Ct)).Gps!;
            Assert.Equal(tnc.Id, gps.SourceId);
            Assert.Equal(49.0583, gps.Latitude!.Value, 4);
            await foreach (var packet in engine.WatchPacketsAsync(null, Ct))
            {
                Assert.Equal("N0CAR-9", packet.Source); // packets still come
                break;
            }
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task TheRadiosNoFixPlaceholderIsNeverAPosition()
    {
        await using var pair = await PtyPair.StartAsync();
        var tnc = TextSerial(pair.Tnc, TncGps.On);
        var (engine, run, cts) = await RunOnSerialAsync(tnc);
        using (engine)
        using (cts)
        {
            await pair.WriteAsync(Encoding.ASCII.GetBytes(NoFix));
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);

            var gps = (await engine.GetStatusAsync(Ct)).Gps!;
            Assert.Equal((tnc.Id, GpsFixKind.NoFix), (gps.SourceId, gps.Fix)); // On: shown, with no fix
            Assert.False(gps.HasPosition);
            Assert.Null(gps.Latitude);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task OffIgnoresTheRadiosGps()
    {
        await using var pair = await PtyPair.StartAsync();
        var tnc = TextSerial(pair.Tnc, TncGps.Off);
        var (engine, run, cts) = await RunOnSerialAsync(tnc);
        using (engine)
        using (cts)
        {
            await pair.WriteAsync(Encoding.ASCII.GetBytes(Fix));
            await Task.Delay(TimeSpan.FromMilliseconds(300), TimeProvider.System, Ct);

            Assert.Null((await engine.GetStatusAsync(Ct)).Gps);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task OnWarnsWhenTheRadioSendsNoGpsAndStopsWhenItDoes()
    {
        await using var pair = await PtyPair.StartAsync();
        var tnc = TextSerial(pair.Tnc, TncGps.On);
        var (engine, run, cts) = await RunOnSerialAsync(tnc);
        using (engine)
        using (cts)
        {
            async Task<string?> Warning() =>
                (await engine.GetStatusAsync(Ct)).Sources!.Single(s => s.Id == tnc.Id).Warning?.Code;

            await WaitUntil(async () => await Warning() == EngineErrorCodes.TncGpsMissing);

            await pair.WriteAsync(Encoding.ASCII.GetBytes(Fix));
            await WaitUntil(async () => await Warning() is null);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AGpsSourceWinsAndTheRadioStandsInWhileItHasNoFreshFix()
    {
        var gpsSource = SourceSettings.GpsNetworkFor(GpsLink.Tcp, "127.0.0.1") with { Id = "gps" };
        var radio = SourceSettings.AprsTextSerialFor("/dev/ttyUSB0") with { Id = "radio" };
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, radio.Id, gpsSource.Id],
        };
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z", null));
        var factory = new EngineTests.FakeSourceFactory();
        var gpsPipe = new Pipe();
        var radioPipe = new Pipe();
        factory.Give(gpsSource.Id, new NmeaGpsSource(_ => Task.FromResult(gpsPipe.Reader.AsStream()), "gps", time));
        // The radio's GPS reaches the engine the same way as a GPS source's: stood in for by an NMEA stream.
        factory.Give(radio.Id, new NmeaGpsSource(_ => Task.FromResult(radioPipe.Reader.AsStream()), "radio", time));
        factory.Enqueue("T2TEST");
        using var engine = new Engine(
            Options.Create(Quick),
            factory,
            new InMemorySettingsStore(
                new AppSettings(
                    [profile],
                    profile.Id,
                    UnitSettings.Metric,
                    Sources: [InMemorySettingsStore.Rotate, radio, gpsSource]
                )
            ),
            time,
            NullLogger<Engine>.Instance
        );
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Sources?.Count(s => s.State == ConnectionState.Connected) >= 2
        );

        await radioPipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(Fix), Ct);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Gps is { SourceId: "radio", HasPosition: true, Fix: GpsFixKind.Fix3D }
        );

        await gpsPipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(GpsTests.Second("120000", lat: "4910.000")), Ct);
        // The GPS source's whole second (GSA, its last sentence, says 3D) before the clock moves on.
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Gps is { SourceId: "gps", HasPosition: true, Fix: GpsFixKind.Fix3D }
        );
        Assert.Equal(49.1667, (await engine.GetStatusAsync(Ct)).Gps!.Latitude!.Value, 4); // the GPS source's place

        time.Advance(Engine.GpsFreshFor + TimeSpan.FromSeconds(1)); // both go stale; the radio speaks again
        await radioPipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(Second("120101")), Ct);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Gps is { SourceId: "radio", HasPosition: true });

        await cts.CancelAsync();
        await run;
    }

    [Theory]
    [InlineData(TncGps.On, true)]
    [InlineData(TncGps.Auto, false)]
    [InlineData(TncGps.Off, false)]
    public void AGpsSourceBesideARadioSetToOnIsTwoGpss(TncGps gps, bool refused)
    {
        var gpsSource = SourceSettings.GpsNetworkFor(GpsLink.Gpsd, "localhost") with { Id = "gps" };
        var radio = SourceSettings.AprsTextSerialFor("/dev/ttyUSB0") with { Id = "radio", TncGps = gps };
        var profile = InMemorySettingsStore.Profile("N0TEST") with { SourceIds = [radio.Id, gpsSource.Id] };
        var settings = new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [radio, gpsSource]);

        var error = Record.Exception(() => SettingsValidator.Validate(settings, TimeProvider.System));

        Assert.Equal(refused, error is not null);
        if (refused)
        {
            Assert.Equal(
                EngineErrorCodes.ProfileTwoGps,
                Assert.IsAssignableFrom<IHasEngineError>(error).EngineError.Code
            );
        }
    }

    [Fact]
    public void OnlyARadiosTextOutputHasAGpsChoice()
    {
        Assert.Equal(TncGps.Auto, SourceSettings.AprsTextSerialFor("/dev/ttyUSB0").EffectiveTncGps);
        Assert.Equal(
            TncGps.Off,
            (SourceSettings.KissSerialFor("/dev/ttyUSB0") with { TncGps = TncGps.On }).EffectiveTncGps
        );
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 500; i++)
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
