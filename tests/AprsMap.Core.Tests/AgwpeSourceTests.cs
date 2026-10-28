// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AprsMap.Contracts;
using AprsNet.Ax25;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AprsMap.Core.Tests;

/// <summary>AGWPE TNCs as sources (docs/features/F3.8-agwpe.md, acceptance tests 1–5).</summary>
public sealed class AgwpeSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EngineOptions Quick = new()
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(100),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
    };

    private const string Ports = "2;Port1 VHF 1200;Port2 HF 300;";

    /// <summary>
    /// An AGWPE server for one client: answers the version (2005.127) and the ports, takes the raw-frames toggle, and
    /// keeps what the client sends.
    /// </summary>
    private sealed class FakeAgwpe : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private NetworkStream? _stream;

        public FakeAgwpe(string ports = Ports)
        {
            _listener.Start();
            Serving = ServeAsync(ports);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task Serving { get; }

        public TaskCompletionSource RawOn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(int Port, char Kind, byte[] Data)> Received { get; } = [];

        private async Task ServeAsync(string ports)
        {
            using var client = await _listener.AcceptTcpClientAsync(Ct);
            _stream = client.GetStream();
            var header = new byte[36];
            try
            {
                while (true)
                {
                    await _stream.ReadExactlyAsync(header, Ct);
                    var data = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28, 4))];
                    await _stream.ReadExactlyAsync(data, Ct);
                    var kind = (char)header[4];
                    lock (Received)
                    {
                        Received.Add((header[0], kind, data));
                    }
                    switch (kind)
                    {
                        case 'R':
                            var version = new byte[8];
                            BinaryPrimitives.WriteInt32LittleEndian(version, 2005);
                            BinaryPrimitives.WriteInt32LittleEndian(version.AsSpan(4), 127);
                            await SendAsync(0, 'R', version);
                            break;
                        case 'G':
                            await SendAsync(0, 'G', Encoding.ASCII.GetBytes(ports));
                            break;
                        case 'k':
                            RawOn.TrySetResult();
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException) { }
        }

        public Task SendAsync(int port, char kind, byte[] data)
        {
            var frame = new byte[36 + data.Length];
            frame[0] = (byte)port;
            frame[4] = (byte)kind;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(28, 4), data.Length);
            data.CopyTo(frame, 36);
            return _stream!.WriteAsync(frame, Ct).AsTask();
        }

        /// <summary>A raw frame heard on <paramref name="port"/>: a command byte, then the AX.25 frame.</summary>
        public Task HearAsync(int port, string tnc2) =>
            SendAsync(port, 'K', [0, .. Ax25Frame.FromAprsPacket(AprsPacket.Parse(tnc2)).GetBytes()]);

        public void Dispose() => _listener.Stop();
    }

    private static (Engine Engine, SourceSettings Tnc) Create(int port, int radioPort)
    {
        var tnc = SourceSettings.AgwpeFor("127.0.0.1", port, radioPort) with { Id = "agw", Name = "Dire Wolf AGW" };
        var profile = InMemorySettingsStore.Profile("N0TEST") with { SourceIds = [tnc.Id] };
        var engine = new Engine(
            Options.Create(Quick),
            new AprsIsPacketSourceFactory(Options.Create(Quick), NullLoggerFactory.Instance, TimeProvider.System),
            new InMemorySettingsStore(new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [tnc])),
            TimeProvider.System,
            NullLogger<Engine>.Instance
        );
        return (engine, tnc);
    }

    private static async Task<SourceStatus?> StatusOf(Engine engine, string id) =>
        (await engine.GetStatusAsync(Ct)).Sources?.FirstOrDefault(s => s.Id == id);

    [Fact]
    public async Task FramesOnTheChosenRadioPortArriveHeardHereAndOthersArent()
    {
        using var server = new FakeAgwpe();
        var (engine, tnc) = Create(server.Port, radioPort: 1); // Port2, HF
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await server.RawOn.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await server.HearAsync(0, "N0VHF>APRS:!4903.50N/07201.75W>on the other radio");
        await server.HearAsync(1, "N0HF>APRS:!4903.50N/07201.75W>on this one");
        await WaitUntil(async () => await engine.GetStationAsync("N0HF", Ct) is not null);

        var status = await StatusOf(engine, tnc.Id);
        Assert.Equal((ConnectionState.Connected, (EngineError?)null), (status!.State, status.Warning));
        Assert.Null(await engine.GetStationAsync("N0VHF", Ct));
        await using var watch = engine.WatchPacketsAsync(null, cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("agw", watch.Current.SourceId);
        Assert.True(watch.Current.Markers.HasFlag(PacketMarkers.HeardHere)); // this station's own radio (F3.1 behavior 8)

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ARadioPortTheServerDoesntListIsReported()
    {
        using var server = new FakeAgwpe();
        var (engine, tnc) = Create(server.Port, radioPort: 3); // Port4: the server has two
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);

        await WaitUntil(async () =>
            (await StatusOf(engine, tnc.Id))?.Warning?.Code == EngineErrorCodes.AgwpePortMissing
        );

        Assert.Equal("4", (await StatusOf(engine, tnc.Id))!.Warning!.Arg(EngineErrorArgs.Port));
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task APacketSentGoesOutAsARawFrameOnTheRadioPort()
    {
        using var server = new FakeAgwpe();
        var settings = SourceSettings.AgwpeFor("127.0.0.1", server.Port, 1);
        var factory = new AprsIsPacketSourceFactory(
            Options.Create(Quick),
            NullLoggerFactory.Instance,
            TimeProvider.System
        );
        await using var source = factory.Create(InMemorySettingsStore.Profile("N0TEST"), settings, string.Empty);
        await source.StartAsync(_ => { }, Ct);
        await server.RawOn.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await ((IPacketSender)source).SendAsync(AprsPacket.Parse("N0TEST>APZAMP,WIDE1-1:>on the air"), Ct);
        await WaitUntil(() =>
        {
            lock (server.Received)
            {
                return Task.FromResult(server.Received.Any(f => f.Kind == 'K'));
            }
        });

        (int Port, char Kind, byte[] Data) sent;
        lock (server.Received)
        {
            sent = server.Received.Single(f => f.Kind == 'K');
        }
        Assert.Equal(1, sent.Port);
        Assert.Equal(0, sent.Data[0]); // the command byte, then the AX.25 frame
        Assert.True(settings.CanTransmit && settings.IsTnc); // gated as a KISS TNC (F2.1)
    }

    [Fact]
    public async Task ThePickerGetsTheServersVersionAndPorts()
    {
        using var server = new FakeAgwpe();
        var (engine, _) = Create(server.Port, 0);

        var answer = await engine.GetAgwpePortsAsync("127.0.0.1", server.Port, Ct);

        Assert.True(answer.Reached);
        Assert.Equal("2005.127", answer.Version);
        Assert.Equal([new AgwpeRadioPort(0, "VHF 1200"), new AgwpeRadioPort(1, "HF 300")], answer.Ports);
    }

    [Fact]
    public async Task NothingAnsweringIsUnreachedAndABadAddressToo()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closed = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var (engine, _) = Create(closed, 0);
        engine.AgwpeAnswerTimeout = TimeSpan.FromMilliseconds(300);

        Assert.Equal(AgwpeServer.Unreached, await engine.GetAgwpePortsAsync("127.0.0.1", closed, Ct));
        Assert.Equal(AgwpeServer.Unreached, await engine.GetAgwpePortsAsync("", 8000, Ct));
        Assert.Equal(AgwpeServer.Unreached, await engine.GetAgwpePortsAsync("127.0.0.1", 0, Ct));
    }

    [Fact]
    public async Task AnAgwpeSourceNeedsAHostAPortAndARadioPort()
    {
        var (engine, tnc) = Create(8000, 0);
        var settings = await engine.GetSettingsAsync(Ct);

        var refused = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(settings with { Sources = [tnc with { TncPort = 16 }] }, Ct)
        );

        Assert.Equal(EngineErrorCodes.AgwpeSourceInvalid, refused.EngineError.Code);
        Assert.Equal("127.0.0.1:8000 port 1", tnc.AgwpeEndpoint);
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
