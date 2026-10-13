// SPDX-License-Identifier: MIT
using System.Threading.Channels;
using AprsMap.Core;
using AprsNet.AprsIsTcpClient;

namespace AprsMap.Core.Tests;

public class AprsIsLineSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LogsInThenYieldsPacketLinesUntilTheServerCloses()
    {
        var transport = new ScriptedTransport();
        transport.Server("# aprsc 2.1.19");
        transport.Server("# logresp N0CALL unverified, server T2TEST");
        var client = new AprsIsClient(
            new AprsIsClientOptions { Callsign = "N0CALL", Passcode = -1 },
            transport: transport
        );
        var source = new AprsIsLineSource(client);

        Assert.Equal("T2TEST", await source.ConnectAsync(Ct));

        transport.Server("N0CALL-9>APRS:>hello");
        transport.Close();
        var lines = new List<string>();
        await foreach (var line in source.ReadLinesAsync(Ct))
        {
            lines.Add(line);
        }

        Assert.Contains("N0CALL-9>APRS:>hello", lines);
        Assert.StartsWith("user N0CALL pass -1", Assert.Single(transport.Written), StringComparison.Ordinal);
        await source.DisposeAsync();
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task ALoginWithoutAServerNameReportsAnEmptyName()
    {
        var transport = new ScriptedTransport();
        transport.Server("# logresp N0CALL unverified");
        await using var source = new AprsIsLineSource(
            new AprsIsClient(new AprsIsClientOptions { Callsign = "N0CALL", Passcode = -1 }, transport: transport)
        );

        Assert.Equal(string.Empty, await source.ConnectAsync(Ct));
    }

    /// <summary>In-memory APRS-IS server: queued lines are what the server sends.</summary>
    private sealed class ScriptedTransport : IAprsTransport
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

        public List<string> Written { get; } = [];

        public bool Disposed { get; private set; }

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
