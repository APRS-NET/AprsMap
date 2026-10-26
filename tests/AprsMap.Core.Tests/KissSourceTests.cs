// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Sockets;
using AprsMap.Contracts;
using AprsNet.Ax25;
using AprsNet.Kiss;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>KISS over TCP sources (docs/features/F3.1-kiss-sources.md, acceptance tests 1, 2, and behavior 13).</summary>
public sealed class KissSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EngineOptions Quick = new()
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(100),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
    };

    /// <summary>One KISS data frame carrying <paramref name="tnc2"/> as an AX.25 UI frame.</summary>
    private static byte[] Frame(string tnc2) =>
        new KissFrame(0, KissCommand.Data, Ax25Frame.FromAprsPacket(AprsPacket.Parse(tnc2)).GetBytes()).Encode();

    private static (Engine Engine, SourceSettings Tnc) Create(int port, TimeProvider? tncTime = null)
    {
        var tnc = new SourceSettings("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1", Port: port);
        // The profile's APRS-IS server can't be reached: everything here comes from the TNC.
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, tnc.Id],
        };
        var engine = new Engine(
            Options.Create(Quick),
            new AprsIsPacketSourceFactory(
                Options.Create(Quick),
                NullLoggerFactory.Instance,
                tncTime ?? TimeProvider.System
            ),
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
        return (engine, tnc);
    }

    private static async Task<SourceStatus?> StatusOf(Engine engine, string id) =>
        (await engine.GetStatusAsync(Ct)).Sources?.FirstOrDefault(s => s.Id == id);

    [Fact]
    public async Task PacketsFromTheTncArriveWithItsSourceAndOtherFramesAreDropped()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var (engine, tnc) = Create(((IPEndPoint)listener.LocalEndpoint).Port);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        using var client = await listener.AcceptTcpClientAsync(Ct);
        var stream = client.GetStream();
        // Not APRS: a NET/ROM-style frame with protocol 0xCF instead of 0xF0.
        var other = Ax25Frame.FromAprsPacket(AprsPacket.Parse("N0NET>ID:x")).GetBytes();
        other[^2] = 0xCF;
        await stream.WriteAsync(new KissFrame(0, KissCommand.Data, other).Encode(), Ct);
        await stream.WriteAsync(Frame("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>"), Ct);

        var heard = new List<PacketView>();
        await foreach (var packet in engine.WatchPacketsAsync(null, Ct))
        {
            heard.Add(packet);
            break;
        }

        var only = Assert.Single(heard);
        Assert.Equal(("N0CAR-9", tnc.Id), (only.Source, only.SourceId));
        Assert.Equal(PacketMarkers.HeardHere | PacketMarkers.Direct, only.Markers); // heard by this station's TNC
        Assert.NotNull(await engine.GetStationAsync("N0CAR-9", Ct));
        Assert.Equal(ConnectionState.Connected, (await StatusOf(engine, tnc.Id))!.State);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ATncThatClosesIsReconnectedAndItsPillSaysSo()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var (engine, tnc) = Create(port);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        var first = await listener.AcceptTcpClientAsync(Ct);
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);
        // Direwolf stops: not listening while it's down, so the pill holds Reconnecting long enough to be seen (with a
        // listener up, the OS accepts the reconnect at once and Reconnecting can pass between two polls).
        listener.Dispose();
        first.Dispose();

        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { State: ConnectionState.Reconnecting } s
            && s.Error?.Code
                is EngineErrorCodes.KissClosed
                    or EngineErrorCodes.KissLinkFailed
                    or EngineErrorCodes.KissUnreachable
        );
        using var restarted = new TcpListener(IPAddress.Loopback, port);
        restarted.Start(); // Direwolf is back
        using var second = await restarted.AcceptTcpClientAsync(Ct); // and the source comes back by itself
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ATncThatCantBeReachedSaysSo()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port; // free, and nothing listens once it's stopped
        }
        var (engine, tnc) = Create(port);
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
    public async Task ATncSendingTextInsteadOfKissFramesWarnsUntilFramesCome()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var (engine, tnc) = Create(((IPEndPoint)listener.LocalEndpoint).Port);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        using var client = await listener.AcceptTcpClientAsync(Ct);
        var stream = client.GetStream();

        // A radio's built-in TNC left in its APRS mode prints what it hears as text.
        var text = string.Concat(
            Enumerable.Repeat("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile on the highway\r\n", 4)
        );
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(text), Ct);
        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { State: ConnectionState.Connected } s
            && s.Warning?.Code == EngineErrorCodes.KissTextNotKiss
        );

        await stream.WriteAsync(Frame("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>"), Ct); // switched to KISS
        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { State: ConnectionState.Connected, Warning: null }
        );

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ATcpTncThatGoesQuietWarnsUntilItSendsAgain()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (engine, tnc) = Create(((IPEndPoint)listener.LocalEndpoint).Port, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        using var client = await listener.AcceptTcpClientAsync(Ct);
        var stream = client.GetStream();
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id)) is { State: ConnectionState.Connected });

        // A TNC server (Dire Wolf, tnc share) keeps the link up while the radio behind it is switched off.
        time.Advance(AprsIsPacketSourceFactory.TncQuietWarning);
        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { State: ConnectionState.Connected } s
            && s.Warning?.Code == EngineErrorCodes.KissQuiet
        );

        await stream.WriteAsync(Frame("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>"), Ct); // the radio's back
        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { State: ConnectionState.Connected, Warning: null }
        );

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AnAprsIsServerThatIsAKissTncSaysSoAndIsntRetried()
    {
        // Direwolf's KISS port given as an APRS-IS server by mistake (G32).
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = InMemorySettingsStore.Profile("N0TEST");
        var engine = new Engine(
            Options.Create(Quick),
            new AprsIsPacketSourceFactory(Options.Create(Quick), NullLoggerFactory.Instance, TimeProvider.System),
            new InMemorySettingsStore(
                new AppSettings(
                    [profile],
                    profile.Id,
                    UnitSettings.Metric,
                    Sources: [InMemorySettingsStore.Rotate with { Server = $"127.0.0.1:{port}" }]
                )
            ),
            TimeProvider.System,
            NullLogger<Engine>.Instance
        );
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        using var client = await listener.AcceptTcpClientAsync(Ct);
        await client.GetStream().WriteAsync(Frame("N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>"), Ct);
        await client.GetStream().WriteAsync("\r\n"u8.ToArray(), Ct);

        await WaitUntil(async () =>
            await engine.GetStatusAsync(Ct) is { Connection: ConnectionState.Refused } s
            && s.ConnectionError?.Code == EngineErrorCodes.AprsIsKissTnc
        );
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromMilliseconds(500)); // several retry delays: no second attempt
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await listener.AcceptTcpClientAsync(wait.Token)
        );

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task SwitchingATncOffStopsItAndOnOpensItAgainAndReconnectNowReconnects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var (engine, tnc) = Create(((IPEndPoint)listener.LocalEndpoint).Port);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        using var first = await listener.AcceptTcpClientAsync(Ct);
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);

        await engine.SetSourceEnabledAsync(tnc.Id, false, Ct);
        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { Enabled: false, State: ConnectionState.Disconnected }
        );
        Assert.False((await engine.GetSettingsAsync(Ct)).EffectiveToggles.IsSourceOn(tnc.Id)); // saved
        Assert.Equal(0, await first.GetStream().ReadAsync(new byte[1], Ct)); // closed by the app

        await engine.SetSourceEnabledAsync(tnc.Id, true, Ct);
        using var second = await listener.AcceptTcpClientAsync(Ct);
        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { Enabled: true, State: ConnectionState.Connected }
        );

        await engine.ReconnectSourceAsync(tnc.Id, Ct);
        Assert.Equal(0, await second.GetStream().ReadAsync(new byte[1], Ct)); // dropped
        using var third = await listener.AcceptTcpClientAsync(Ct); // and connected again straight away
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);

        await cts.CancelAsync();
        await run;
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
