// SPDX-License-Identifier: MIT
using System.Diagnostics;
using AprsMap.Contracts;
using AprsNet.Ax25;
using AprsNet.Kiss;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AprsMap.Core.Tests;

/// <summary>
/// Serial sources: KISS over serial (docs/features/F3.1-kiss-sources.md, behaviors 1.2, 4, 13; acceptance test 5), and
/// radios' built-in TNCs (docs/features/F3.2-built-in-tncs.md, behaviors 1–7; acceptance tests 1–3, 6).
/// </summary>
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

    [Fact]
    public void ARadioInAprsModeIsReadAsTextAndATncTypeSwitchesItIntoKiss()
    {
        var text = AprsIsPacketSourceFactory.SerialOptions(
            SourceSettings.AprsTextSerialFor("/dev/ttyUSB0"),
            Quick,
            TimeProvider.System
        );
        Assert.True(text.TextMode);
        Assert.Null(text.Setup);

        var plain = AprsIsPacketSourceFactory.SerialOptions(
            SourceSettings.KissSerialFor("/dev/ttyUSB0"),
            Quick,
            TimeProvider.System
        );
        Assert.False(plain.TextMode);
        Assert.Null(plain.Setup);

        var d72 = AprsIsPacketSourceFactory.SerialOptions(
            SourceSettings.KissSerialFor("/dev/ttyUSB0") with
            {
                TncType = TncSetup.KenwoodThD72.Id,
            },
            Quick,
            TimeProvider.System
        );
        Assert.Equal(TncSetup.KenwoodThD72, d72.Setup);

        var custom = AprsIsPacketSourceFactory.SetupOf(
            SourceSettings.KissSerialFor("/dev/ttyUSB0") with
            {
                TncType = SourceSettings.CustomTncType,
                EnterScript = "send KISS ON\nsend RESTART",
                LeaveScript = "bytes C0 FF C0",
            }
        )!;
        Assert.Equal(2, custom.EnterKiss.Steps.Count);
        Assert.Single(custom.LeaveKiss!.Steps);
    }

    [Theory]
    [InlineData("no-such-radio", null, EngineErrorCodes.TncScriptInvalid)]
    [InlineData(SourceSettings.CustomTncType, "", EngineErrorCodes.TncScriptInvalid)]
    [InlineData(SourceSettings.CustomTncType, "wiggle the antenna", EngineErrorCodes.TncScriptInvalid)]
    public async Task AnUnknownTncTypeOrABadScriptIsRefused(string? type, string? script, string code)
    {
        var good = SourceSettings.KissSerialFor("/dev/ttyUSB0");
        using var engine = Create(good);
        var settings = await engine.GetSettingsAsync(Ct);
        var changed = good with { TncType = type, EnterScript = script };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(settings with { Sources = [settings.SourceList[0], changed] }, Ct)
        );

        Assert.Equal(code, error.EngineError.Code);
    }

    [Fact]
    public async Task TheTncTypesAreThoseAprsNetKnows()
    {
        using var engine = Create(SourceSettings.KissSerialFor("/dev/ttyUSB0"));

        var types = await engine.GetTncTypesAsync(Ct);

        Assert.Contains(new TncTypeInfo(TncSetup.KenwoodThD72.Id, TncSetup.KenwoodThD72.Name), types);
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

    /// <summary>A TNC on a pseudo-terminal pair: what the "radio" end writes arrives as packets, heard by this station.</summary>
    [Fact]
    public async Task PacketsFromASerialTncArriveWithItsSource()
    {
        await using var pair = await PtyPair.StartAsync();
        // A pseudo-terminal has no modem lines, so DTR and RTS stay low.
        var tnc = SourceSettings.KissSerialFor(pair.Tnc) with
        {
            DtrEnable = false,
            RtsEnable = false,
        };
        using var engine = Create(tnc);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);

        await pair.WriteAsync(
            new KissFrame(
                0,
                KissCommand.Data,
                Ax25Frame.FromAprsPacket(AprsPacket.Parse("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>")).GetBytes()
            ).Encode()
        );

        await foreach (var packet in engine.WatchPacketsAsync(null, Ct))
        {
            Assert.Equal(("N0CAR-9", tnc.Id), (packet.Source, packet.SourceId));
            Assert.True(packet.Markers.HasFlag(PacketMarkers.HeardHere));
            break;
        }
        await cts.CancelAsync();
        await run;
    }

    /// <summary>A radio in its APRS mode prints what it hears as text lines, read as packets (F3.2 behavior 2).</summary>
    [Fact]
    public async Task ARadioInAprsModeIsReadWithNoSetup()
    {
        await using var pair = await PtyPair.StartAsync();
        var tnc = SourceSettings.AprsTextSerialFor(pair.Tnc) with { DtrEnable = false, RtsEnable = false };
        using var engine = Create(tnc);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);

        await pair.WriteAsync("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>\r\n"u8.ToArray());

        await foreach (var packet in engine.WatchPacketsAsync(null, Ct))
        {
            Assert.Equal(("N0CAR-9", tnc.Id), (packet.Source, packet.SourceId));
            Assert.True(packet.Markers.HasFlag(PacketMarkers.HeardHere));
            break;
        }
        await cts.CancelAsync();
        await run;
    }

    /// <summary>A custom TNC type's script runs when the port opens, and packets follow (F3.2 behavior 5).</summary>
    [Fact]
    public async Task ACustomTncTypesScriptRunsWhenThePortOpens()
    {
        await using var pair = await PtyPair.StartAsync();
        var tnc = SourceSettings.KissSerialFor(pair.Tnc) with
        {
            DtrEnable = false,
            RtsEnable = false,
            TncType = SourceSettings.CustomTncType,
            EnterScript = "send KISS ON\npause 50ms",
        };
        using var engine = Create(tnc);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        Assert.Equal("KISS ON\r", await pair.ReadAsync(8));
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);
        await pair.WriteAsync(
            new KissFrame(
                0,
                KissCommand.Data,
                Ax25Frame.FromAprsPacket(AprsPacket.Parse("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>")).GetBytes()
            ).Encode()
        );
        await foreach (var packet in engine.WatchPacketsAsync(null, Ct))
        {
            Assert.Equal("N0CAR-9", packet.Source);
            break;
        }
        await cts.CancelAsync();
        await run;
    }

    /// <summary>A TNC that never answers its type's script says why, and the source keeps trying (F3.2 behavior 6).</summary>
    [Fact]
    public async Task ATncThatDoesntAnswerItsScriptSaysItDidntSwitchToKiss()
    {
        await using var pair = await PtyPair.StartAsync();
        var tnc = SourceSettings.KissSerialFor(pair.Tnc) with
        {
            DtrEnable = false,
            RtsEnable = false,
            TncType = SourceSettings.CustomTncType,
            EnterScript = "send KISS ON\nexpect 200ms OK",
        };
        using var engine = Create(tnc);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { State: ConnectionState.Reconnecting } s
            && s.Error?.Code == EngineErrorCodes.KissSetupFailed
            && (s.Error.Arg(EngineErrorArgs.Reason) ?? "").Contains("KISS", StringComparison.Ordinal)
        );
        await cts.CancelAsync();
        await run;
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

/// <summary>
/// Two linked pseudo-terminals from socat: the engine opens <see cref="Tnc"/>, and the test plays the radio on the other
/// end. Skips the test where socat isn't installed.
/// </summary>
internal sealed class PtyPair : IAsyncDisposable
{
    private readonly Process _socat;
    private readonly DirectoryInfo _dir;
    private readonly FileStream _radio;

    private PtyPair(Process socat, DirectoryInfo dir, FileStream radio)
    {
        _socat = socat;
        _dir = dir;
        _radio = radio;
    }

    /// <summary>The path the engine opens.</summary>
    public string Tnc => $"{_dir.FullName}/tnc";

    public static async Task<PtyPair> StartAsync()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/socat"))
        {
            Assert.Skip("Needs Linux and socat for a pseudo-terminal pair.");
        }
        var dir = Directory.CreateTempSubdirectory("aprsmap-serial-");
        var socat = Process.Start(
            new ProcessStartInfo(
                "/usr/bin/socat",
                $"pty,raw,echo=0,link={dir.FullName}/tnc pty,raw,echo=0,link={dir.FullName}/radio"
            )
            {
                RedirectStandardError = true,
            }
        )!;
        for (var i = 0; i < 500 && !(File.Exists($"{dir.FullName}/tnc") && File.Exists($"{dir.FullName}/radio")); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        var radio = new FileStream(
            $"{dir.FullName}/radio",
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            1,
            useAsync: false
        );
        return new PtyPair(socat, dir, radio);
    }

    public async Task WriteAsync(byte[] bytes)
    {
        await _radio.WriteAsync(bytes, TestContext.Current.CancellationToken);
        await _radio.FlushAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Reads <paramref name="count"/> bytes the engine sent to the radio, as Latin-1.</summary>
    public async Task<string> ReadAsync(int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            read += await _radio.ReadAsync(buffer.AsMemory(read), TestContext.Current.CancellationToken);
        }
        return System.Text.Encoding.Latin1.GetString(buffer);
    }

    public async ValueTask DisposeAsync()
    {
        await _radio.DisposeAsync();
        _socat.Kill();
        await _socat.WaitForExitAsync(CancellationToken.None);
        _socat.Dispose();
        _dir.Delete(recursive: true);
    }
}
