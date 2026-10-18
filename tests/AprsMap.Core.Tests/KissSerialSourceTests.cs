// SPDX-License-Identifier: MIT
using System.Diagnostics;
using AprsMap.Contracts;
using AprsNet.Ax25;
using AprsNet.Kiss;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AprsMap.Core.Tests;

/// <summary>KISS over serial sources (docs/features/F3.1-kiss-sources.md, behaviors 1.2, 4, 13; acceptance test 5).</summary>
public sealed class KissSerialSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EngineOptions Quick = new()
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(100),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
    };

    private static Engine Create(SourceSettings tnc)
    {
        // The profile's APRS-IS server can't be reached: everything here comes from the TNC.
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

    private static async Task<SourceStatus?> StatusOf(Engine engine, string id) =>
        (await engine.GetStatusAsync(Ct)).Sources?.FirstOrDefault(s => s.Id == id);

    [Fact]
    public void TheConnectionGetsTheSourcesPortSpeedTncPortAndControlLines()
    {
        var source = SourceSettings.KissSerialFor("/dev/ttyUSB0", 19200, 2) with { RtsEnable = false };

        var options = AprsIsPacketSourceFactory.SerialOptions(source, Quick, TimeProvider.System);

        Assert.Equal(
            ("/dev/ttyUSB0", 19200, 2, true, false),
            (options.PortName, options.BaudRate, options.TncPort, options.DtrEnable, options.RtsEnable)
        );
        Assert.Equal(TimeSpan.FromMinutes(30), options.QuietWarning); // decision 5
        Assert.Equal(
            (Quick.InitialRetryDelay, Quick.MaxRetryDelay),
            (options.ReconnectDelay, options.MaxReconnectDelay)
        );
        options.Validate();

        var defaults = AprsIsPacketSourceFactory.SerialOptions(
            SourceSettings.KissSerialFor("COM3"),
            Quick,
            TimeProvider.System
        );
        Assert.Equal(
            (9600, 0, true, true),
            (defaults.BaudRate, defaults.TncPort, defaults.DtrEnable, defaults.RtsEnable)
        );
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("/dev/ttyUSB0", 0, null)]
    [InlineData("/dev/ttyUSB0", null, 16)]
    public async Task ASerialSourceWithoutAPortOrWithABadSpeedOrTncPortIsRefused(string port, int? baud, int? tncPort)
    {
        var good = SourceSettings.KissSerialFor("/dev/ttyUSB0");
        using var engine = Create(good);
        var settings = await engine.GetSettingsAsync(Ct);

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(
                settings with
                {
                    Sources = [settings.SourceList[0], good with { SerialPort = port, Baud = baud, TncPort = tncPort }],
                },
                Ct
            )
        );

        Assert.Equal(EngineErrorCodes.KissSerialSourceInvalid, error.EngineError.Code);
    }

    [Fact]
    public async Task APortThatIsntThereSaysTheTncCantBeReachedAndKeepsTrying()
    {
        var tnc = SourceSettings.KissSerialFor("/dev/aprsmap-no-such-port");
        using var engine = Create(tnc);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { State: ConnectionState.Reconnecting } s
            && s.Error?.Code == EngineErrorCodes.KissUnreachable
        );

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task TheSerialPortsPresentAreListedInOrder()
    {
        using var engine = Create(SourceSettings.KissSerialFor("/dev/ttyUSB0"));

        var ports = await engine.GetSerialPortsAsync(Ct);

        Assert.Equal(ports.Order(StringComparer.Ordinal), ports);
    }

    /// <summary>
    /// A TNC on a pseudo-terminal pair from socat: what the "radio" end writes arrives as packets, heard by this station.
    /// Skipped where socat isn't installed.
    /// </summary>
    [Fact]
    public async Task PacketsFromASerialTncArriveWithItsSource()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/socat"))
        {
            Assert.Skip("Needs Linux and socat for a pseudo-terminal pair.");
        }
        var dir = Directory.CreateTempSubdirectory("aprsmap-serial-");
        using var socat = Process.Start(
            new ProcessStartInfo(
                "/usr/bin/socat",
                $"pty,raw,echo=0,link={dir.FullName}/tnc pty,raw,echo=0,link={dir.FullName}/radio"
            )
            {
                RedirectStandardError = true,
            }
        )!;
        try
        {
            await WaitUntil(() =>
                Task.FromResult(File.Exists($"{dir.FullName}/tnc") && File.Exists($"{dir.FullName}/radio"))
            );
            // A pseudo-terminal has no modem lines, so DTR and RTS stay low.
            var tnc = SourceSettings.KissSerialFor($"{dir.FullName}/tnc") with
            {
                DtrEnable = false,
                RtsEnable = false,
            };
            using var engine = Create(tnc);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var run = engine.RunAsync(cts.Token);
            await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);

            await using (var radio = new FileStream($"{dir.FullName}/radio", FileMode.Open, FileAccess.Write))
            {
                var frame = new KissFrame(
                    0,
                    KissCommand.Data,
                    Ax25Frame.FromAprsPacket(AprsPacket.Parse("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>")).GetBytes()
                ).Encode();
                await radio.WriteAsync(frame, Ct);
                await radio.FlushAsync(Ct);
            }

            await foreach (var packet in engine.WatchPacketsAsync(null, Ct))
            {
                Assert.Equal(("N0CAR-9", tnc.Id), (packet.Source, packet.SourceId));
                Assert.True(packet.Markers.HasFlag(PacketMarkers.HeardHere));
                break;
            }

            await cts.CancelAsync();
            await run;
        }
        finally
        {
            socat.Kill();
            await socat.WaitForExitAsync(CancellationToken.None);
            dir.Delete(recursive: true);
        }
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
