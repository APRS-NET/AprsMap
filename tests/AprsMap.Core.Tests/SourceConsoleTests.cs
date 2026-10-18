// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Sockets;
using System.Text;
using AprsMap.Contracts;
using AprsNet.Ax25;
using AprsNet.Kiss;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AprsMap.Core.Tests;

/// <summary>
/// The Source console (docs/features/F1.28-source-console.md, acceptance tests 1–3, 6) and typing to a TNC
/// (docs/features/F3.2-built-in-tncs.md, acceptance tests 4, 5).
/// </summary>
public sealed class SourceConsoleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EngineOptions Quick = new()
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(100),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
    };

    private static Engine Create(SourceSettings source, bool licensed = true, SourceSettings? aprsIs = null)
    {
        aprsIs ??= InMemorySettingsStore.Rotate with { Server = "127.0.0.1:1" };
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            SourceIds = [aprsIs.Id, source.Id],
            Licensed = licensed,
            Passcode = licensed ? 16201 : -1,
        };
        return new Engine(
            Options.Create(Quick),
            new AprsIsPacketSourceFactory(Options.Create(Quick), NullLoggerFactory.Instance, TimeProvider.System),
            new InMemorySettingsStore(
                new AppSettings(
                    [profile],
                    profile.Id,
                    UnitSettings.Metric,
                    Sources: source.Id == aprsIs.Id ? [aprsIs] : [aprsIs, source]
                )
            ),
            TimeProvider.System,
            NullLogger<Engine>.Instance
        );
    }

    private static byte[] Frame(string tnc2, int port = 0) =>
        new KissFrame(port, KissCommand.Data, Ax25Frame.FromAprsPacket(AprsPacket.Parse(tnc2)).GetBytes()).Encode();

    /// <summary>Reads entries from a watch into a list the test can poll.</summary>
    private static List<TrafficEntry> Collect(Engine engine, string sourceId, CancellationToken cancellationToken)
    {
        var entries = new List<TrafficEntry>();
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var entry in engine.WatchSourceTrafficAsync(sourceId, cancellationToken))
                    {
                        lock (entries)
                        {
                            entries.Add(entry);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            },
            CancellationToken.None
        );
        return entries;
    }

    private static List<TrafficEntry> Snapshot(List<TrafficEntry> entries)
    {
        lock (entries)
        {
            return [.. entries];
        }
    }

    [Fact]
    public async Task EachKindOfTrafficFromATncHasItsOwnLineAndIsKeptFromTheStart()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var tnc = new SourceSettings(
            "tnc",
            "Direwolf",
            SourceKind.KissTcp,
            Host: "127.0.0.1",
            Port: ((IPEndPoint)listener.LocalEndpoint).Port
        );
        using var engine = Create(tnc);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        using var client = await listener.AcceptTcpClientAsync(Ct);
        var stream = client.GetStream();

        var notAprs = Ax25Frame.FromAprsPacket(AprsPacket.Parse("N0NET>ID:x")).GetBytes();
        notAprs[^2] = 0xCF; // NET/ROM's protocol id, not APRS's
        await stream.WriteAsync(new KissFrame(0, KissCommand.Data, notAprs).Encode(), Ct);
        await stream.WriteAsync(Frame("N0OTH>APRS:>other port", port: 1), Ct);
        await stream.WriteAsync(new KissFrame(0, KissCommand.TxDelay, new byte[] { 30 }).Encode(), Ct);
        await stream.WriteAsync(new byte[] { 0xC0, 0x00, 0x41, 0xDB, 0x41, 0xC0 }, Ct); // a bad escape
        await stream.WriteAsync(Frame("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>"), Ct);
        await foreach (var _ in engine.WatchPacketsAsync(null, Ct))
        {
            break; // the good one has arrived, so all of them have
        }

        // Watched only now: what came since the TNC opened is there (behavior 7).
        var entries = Collect(engine, tnc.Id, cts.Token);
        await WaitUntil(() => Snapshot(entries).Any(e => e.Kind == TrafficKind.Packet));
        var kinds = Snapshot(entries).Select(e => e.Kind).ToList();
        Assert.Equal(
            [
                TrafficKind.NotAprs,
                TrafficKind.OtherPort,
                TrafficKind.KissCommand,
                TrafficKind.DroppedFrame,
                TrafficKind.Packet,
            ],
            kinds
        );
        var packet = Snapshot(entries).Single(e => e.Kind == TrafficKind.Packet);
        Assert.Equal(
            (TrafficDirection.In, "N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>", tnc.Id),
            (packet.Direction, packet.Text, packet.SourceId)
        );
        Assert.NotNull(Snapshot(entries).Single(e => e.Kind == TrafficKind.DroppedFrame).Detail);

        // Text from a TNC not in KISS mode comes a line at a time.
        // (Its first byte isn't a KISS command byte, so it reads as text after a frame.)
        await stream.WriteAsync("KISS OFF\r\n"u8.ToArray(), Ct);
        await WaitUntil(() => Snapshot(entries).Any(e => e.Kind == TrafficKind.Unframed));
        Assert.Contains(
            Snapshot(entries),
            e => e.Kind == TrafficKind.Unframed && e.Text!.Contains("KISS OFF", StringComparison.Ordinal)
        );

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AnUnknownSourceIsRefused()
    {
        using var engine = Create(SourceSettings.KissSerialFor("/dev/ttyUSB0"));

        var error = await Assert.ThrowsAsync<EngineArgumentException>(async () =>
        {
            await foreach (var _ in engine.WatchSourceTrafficAsync("nope", Ct)) { }
        });

        Assert.Equal(EngineErrorCodes.ConsoleSourceUnknown, error.EngineError.Code);
    }

    [Fact]
    public async Task InManualModeATypedLineGoesOverTheOpenConnectionAndShowsAsTyped()
    {
        await using var pair = await PtyPair.StartAsync();
        var tnc = SourceSettings.KissSerialFor(pair.Tnc) with { DtrEnable = false, RtsEnable = false };
        using var engine = Create(tnc);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);
        var entries = Collect(engine, tnc.Id, cts.Token);

        // Outside manual mode, typing is refused; Leave KISS isn't (F3.2 behaviors 9, 10).
        var refused = await Assert.ThrowsAsync<EngineOperationException>(() =>
            engine.SendTncCommandAsync(tnc.Id, new TncCommand(TncCommandKind.Line, "KISS ON"), Ct)
        );
        Assert.Equal(EngineErrorCodes.KissNotManual, refused.EngineError.Code);
        await engine.SendTncCommandAsync(tnc.Id, new TncCommand(TncCommandKind.LeaveKiss), Ct);
        Assert.Equal("ÀÿÀ", await pair.ReadAsync(3));

        await engine.SetTncManualAsync(tnc.Id, true, Ct);
        Assert.True((await StatusOf(engine, tnc.Id))!.Manual);
        await engine.SendTncCommandAsync(tnc.Id, new TncCommand(TncCommandKind.Line, "KISS ON"), Ct);
        Assert.Equal("KISS ON\r", await pair.ReadAsync(8));
        await engine.SendTncCommandAsync(tnc.Id, new TncCommand(TncCommandKind.Bytes, "c0 ff c0"), Ct);
        Assert.Equal("ÀÿÀ", await pair.ReadAsync(3));
        await WaitUntil(() => Snapshot(entries).Count(e => e.Kind == TrafficKind.Typed) >= 2);
        Assert.All(
            Snapshot(entries).Where(e => e.Kind == TrafficKind.Typed),
            e => Assert.Equal(TrafficDirection.Out, e.Direction)
        );

        // The TNC's answer shows as text.
        await pair.WriteAsync(Encoding.ASCII.GetBytes("cmd:"));
        await WaitUntil(() => Snapshot(entries).Any(e => e.Kind == TrafficKind.Unframed));
        Assert.False(Snapshot(entries).Last(e => e.Kind == TrafficKind.Unframed).EndsLine); // a prompt

        await engine.SetTncManualAsync(tnc.Id, false, Ct);
        Assert.False((await StatusOf(engine, tnc.Id))!.Manual);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task TypingNeedsALicensedProfileAndBytesMustBeHex()
    {
        await using var pair = await PtyPair.StartAsync();
        var tnc = SourceSettings.KissSerialFor(pair.Tnc) with { DtrEnable = false, RtsEnable = false };
        using var engine = Create(tnc, licensed: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);
        await engine.SetTncManualAsync(tnc.Id, true, Ct);

        var unlicensed = await Assert.ThrowsAsync<EngineOperationException>(() =>
            engine.SendTncCommandAsync(tnc.Id, new TncCommand(TncCommandKind.Line, "ID"), Ct)
        );
        Assert.Equal(EngineErrorCodes.TransmitNotLicensed, unlicensed.EngineError.Code);
        // Switching modes needs no licence (F3.2 behavior 7).
        await engine.SendTncCommandAsync(tnc.Id, new TncCommand(TncCommandKind.LeaveKiss), Ct);
        Assert.Equal("ÀÿÀ", await pair.ReadAsync(3));

        var hex = Assert.Throws<EngineArgumentException>(() => Engine.HexBytes("C0 F"));
        Assert.Equal(EngineErrorCodes.KissBytesInvalid, hex.EngineError.Code);
        Assert.Equal([0xC0, 0xFF, 0xC0], Engine.HexBytes("C0FF c0"));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AprsIsLinesAreKeptOnlyWhileWatchedWithThePasscodeMasked()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var aprsIs = InMemorySettingsStore.Rotate with
        {
            Server = $"127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}",
        };
        using var engine = Create(aprsIs, aprsIs: aprsIs);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        using var first = await server.AcceptTcpClientAsync(Ct);
        var reader = new StreamReader(first.GetStream(), Encoding.Latin1);
        var writer = new StreamWriter(first.GetStream(), Encoding.Latin1) { AutoFlush = true, NewLine = "\r\n" };
        await writer.WriteLineAsync("# aprsc 2.1.19");
        var login = await reader.ReadLineAsync(Ct);
        Assert.Contains("pass 16201", login, StringComparison.Ordinal);
        await writer.WriteLineAsync("# logresp N0TEST verified, server T2TEST");
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Connected);

        // Nothing was kept before anyone watched (behavior 7).
        var entries = Collect(engine, aprsIs.Id, cts.Token);
        await writer.WriteLineAsync("# keepalive");
        await WaitUntil(() => Snapshot(entries).Any(e => e.Kind == TrafficKind.ServerComment));
        Assert.DoesNotContain(Snapshot(entries), e => e.Kind == TrafficKind.LoginSent);

        // Reconnect while watched: the login is there, its passcode masked.
        await engine.ReconnectAsync(Ct);
        using var second = await server.AcceptTcpClientAsync(Ct);
        var writer2 = new StreamWriter(second.GetStream(), Encoding.Latin1) { AutoFlush = true, NewLine = "\r\n" };
        await writer2.WriteLineAsync("# aprsc 2.1.19");
        await WaitUntil(() => Snapshot(entries).Any(e => e.Kind == TrafficKind.LoginSent));
        var line = Snapshot(entries).First(e => e.Kind == TrafficKind.LoginSent).Text!;
        Assert.DoesNotContain("16201", line, StringComparison.Ordinal);

        await cts.CancelAsync();
        await run;
    }

    private static async Task<SourceStatus?> StatusOf(Engine engine, string id) =>
        (await engine.GetStatusAsync(Ct)).Sources?.FirstOrDefault(s => s.Id == id);

    private static async Task WaitUntil(Func<bool> condition) => await WaitUntil(() => Task.FromResult(condition()));

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
