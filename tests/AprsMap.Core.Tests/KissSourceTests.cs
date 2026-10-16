// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Sockets;
using AprsMap.Contracts;
using AprsNet.Ax25;
using AprsNet.Kiss;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AprsMap.Core.Tests;

/// <summary>KISS over TCP sources (docs/features/F3.1-kiss-sources.md, acceptance tests 1, 2).</summary>
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

    private static (Engine Engine, SourceSettings Tnc) Create(int port)
    {
        var tnc = new SourceSettings("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1", Port: port);
        // The profile's APRS-IS server can't be reached: everything here comes from the TNC.
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, tnc.Id],
        };
        var engine = new Engine(
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
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var (engine, tnc) = Create(((IPEndPoint)listener.LocalEndpoint).Port);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        var first = await listener.AcceptTcpClientAsync(Ct);
        await WaitUntil(async () => (await StatusOf(engine, tnc.Id))?.State == ConnectionState.Connected);
        first.Dispose(); // Direwolf restarted

        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id)) is { State: ConnectionState.Reconnecting } s
            && s.Error?.Code is EngineErrorCodes.KissClosed or EngineErrorCodes.KissLinkFailed
        );
        using var second = await listener.AcceptTcpClientAsync(Ct); // and it comes back by itself
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
