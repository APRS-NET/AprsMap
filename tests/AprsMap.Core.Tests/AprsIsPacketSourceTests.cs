// SPDX-License-Identifier: MIT
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsMap.Core;
using AprsNet.AprsIsTcpClient;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

public class AprsIsPacketSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AprsIsPacketSource Source(TimeProvider time, params ScriptedTransport[] transports)
    {
        var next = new Queue<ScriptedTransport>(transports);
        var options = new AprsIsClientOptions
        {
            Callsign = "N0CALL",
            Passcode = -1,
            TimeProvider = time,
            ReconnectDelay = TimeSpan.FromSeconds(1),
        };
        return new AprsIsPacketSource(new AprsIsConnection(options, transportFactory: next.Dequeue));
    }

    [Fact]
    public async Task LogsInThenYieldsParsedPackets()
    {
        var transport = ScriptedTransport.LoggedIn("T2TEST");
        var source = Source(TimeProvider.System, transport);

        Assert.Equal("T2TEST", await source.StartAsync((_, _) => { }, Ct));

        transport.Server("N0CALL-9>APRS:>hello");
        await using var packets = source.ReadPacketsAsync(Ct).GetAsyncEnumerator(Ct);
        Assert.True(await packets.MoveNextAsync());
        Assert.Equal("N0CALL-9", packets.Current.Source?.Value);
        Assert.StartsWith("user N0CALL pass -1", Assert.Single(transport.Written), StringComparison.Ordinal);
        await source.DisposeAsync();
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task ALoginWithoutAServerNameReportsAnEmptyName()
    {
        var transport = new ScriptedTransport();
        transport.Server("# logresp N0CALL unverified");
        await using var source = Source(TimeProvider.System, transport);

        Assert.Equal(string.Empty, await source.StartAsync((_, _) => { }, Ct));
    }

    [Fact]
    public async Task ADroppedConnectionIsReportedThenLogsInAgain()
    {
        var time = new FakeTimeProvider();
        var first = ScriptedTransport.LoggedIn("T2FIRST");
        var second = ScriptedTransport.LoggedIn("T2SECOND");
        await using var source = Source(time, first, second);
        var states = new System.Collections.Concurrent.ConcurrentQueue<(ConnectionState, string?)>();
        await source.StartAsync((state, server) => states.Enqueue((state, server)), Ct);

        first.Close();
        for (var i = 0; i < 200 && !states.Contains((ConnectionState.Connected, "T2SECOND")); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(
            [
                (ConnectionState.Connecting, null),
                (ConnectionState.Connected, "T2FIRST"),
                (ConnectionState.Faulted, null),
                (ConnectionState.Connecting, null),
                (ConnectionState.Connected, "T2SECOND"),
            ],
            states
        );
    }

    /// <summary>In-memory APRS-IS server: queued lines are what the server sends.</summary>
    private sealed class ScriptedTransport : IAprsTransport
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

        public List<string> Written { get; } = [];

        public bool Disposed { get; private set; }

        public static ScriptedTransport LoggedIn(string server)
        {
            var transport = new ScriptedTransport();
            transport.Server("# aprsc 2.1.19");
            transport.Server($"# logresp N0CALL unverified, server {server}");
            return transport;
        }

        public void Server(string line) => _incoming.Writer.TryWrite(line);

        public void Close() => _incoming.Writer.TryComplete();

        public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _incoming.Reader.WaitToReadAsync(cancellationToken) && _incoming.Reader.TryRead(out var line)
                ? line
                : null;

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            Written.Add(line);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
