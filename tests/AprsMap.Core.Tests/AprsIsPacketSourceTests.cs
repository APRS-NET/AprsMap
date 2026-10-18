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
    public void AReceiveOnlyEngineLogsInUnverifiedWhateverThePasscode()
    {
        var licensed = new StationProfile("h", "Home", "N0TEST", 12345, [], null, null, Licensed: true);

        var options = AprsIsPacketSourceFactory.ClientOptions(
            licensed,
            "rotate.aprs2.net:14580",
            "",
            TimeProvider.System,
            receiveOnly: true
        );

        Assert.Equal(-1, options.Passcode); // F1.26 behavior 7.2
        Assert.True(options.ListenOnly);
    }

    [Fact]
    public void AListenerIdLogsInListenOnlyAndACallsignDoesNot()
    {
        var listener = new StationProfile("l", "Listening", "SWL482913", 12345, [], null, null);
        var licensed = new StationProfile("h", "Home", "N0TEST", 12345, [], null, null, Licensed: true);

        var listening = AprsIsPacketSourceFactory.ClientOptions(
            listener,
            "rotate.aprs2.net:14580",
            "",
            TimeProvider.System
        );
        var sending = AprsIsPacketSourceFactory.ClientOptions(
            licensed,
            "rotate.aprs2.net:14580",
            "",
            TimeProvider.System
        );

        Assert.True(listening.ListenOnly);
        Assert.True(listening.IsReadOnly); // APRS.NET logs in with -1 whatever the profile holds
        Assert.False(sending.ListenOnly);
    }

    [Fact]
    public async Task LogsInThenYieldsParsedPackets()
    {
        var transport = ScriptedTransport.LoggedIn("T2TEST");
        var source = Source(TimeProvider.System, transport);

        Assert.Equal(new PacketSourceLogin("T2TEST", false), await source.StartAsync(_ => { }, Ct));

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

        Assert.Equal(string.Empty, (await source.StartAsync(_ => { }, Ct)).Server);
    }

    [Fact]
    public async Task ADroppedConnectionIsReportedThenLogsInAgain()
    {
        var time = new FakeTimeProvider();
        var first = ScriptedTransport.LoggedIn("T2FIRST");
        var second = ScriptedTransport.LoggedIn("T2SECOND", verified: true);
        await using var source = Source(time, first, second);
        var events = new System.Collections.Concurrent.ConcurrentQueue<PacketSourceEvent>();
        await source.StartAsync(events.Enqueue, Ct);

        first.Close();
        await AdvanceUntil(time, () => events.Any(e => e.Login?.Server == "T2SECOND"));

        Assert.Equal(
            [
                ConnectionState.Connecting,
                ConnectionState.Connected,
                ConnectionState.Reconnecting,
                ConnectionState.Connecting,
                ConnectionState.Connected,
            ],
            events.Select(e => e.State)
        );
        var dropped = events.ElementAt(2);
        Assert.Equal(EngineErrorCodes.AprsIsServerClosed, dropped.Error?.Code);
        Assert.NotNull(dropped.RetryIn);
        Assert.Equal(new PacketSourceLogin("T2SECOND", true), events.Last().Login);
    }

    [Fact]
    public async Task ARefusedReconnectIsReportedAsRefused()
    {
        var time = new FakeTimeProvider();
        var first = ScriptedTransport.LoggedIn("T2FIRST");
        var second = new ScriptedTransport();
        second.Server("# Login by user not allowed");
        await using var source = Source(time, first, second);
        var events = new System.Collections.Concurrent.ConcurrentQueue<PacketSourceEvent>();
        await source.StartAsync(events.Enqueue, Ct);

        first.Close();
        await AdvanceUntil(time, () => events.Any(e => e.State == ConnectionState.Refused));

        var refused = events.Single(e => e.State == ConnectionState.Refused);
        Assert.Equal(EngineErrorCodes.AprsIsLoginRefused, refused.Error?.Code);
        Assert.Equal("Login by user not allowed", refused.Error?.Arg(EngineErrorArgs.Reason));
    }

    [Theory]
    [InlineData(null, true, EngineErrorCodes.AprsIsServerClosed)]
    [InlineData("timeout", true, EngineErrorCodes.AprsIsIdle)]
    [InlineData("io", true, EngineErrorCodes.AprsIsLinkFailed)]
    [InlineData("io", false, EngineErrorCodes.AprsIsUnreachable)]
    [InlineData("login", false, EngineErrorCodes.AprsIsUnreachable)]
    [InlineData("kiss", false, EngineErrorCodes.AprsIsKissTnc)]
    [InlineData("kiss", true, EngineErrorCodes.AprsIsKissTnc)]
    public void EachEndOfAConnectionHasItsCode(string? error, bool wasConnected, string code)
    {
        Exception? exception = error switch
        {
            "timeout" => new TimeoutException("silent"),
            "io" => new IOException("reset"),
            "login" => new LoginException("No login response within 15s."),
            "kiss" => new KissTncLoginException(),
            _ => null,
        };

        Assert.Equal(code, AprsIsErrors.From(exception, wasConnected).Code);
    }

    private static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.True(condition(), "Condition was not met in time.");
    }

    /// <summary>In-memory APRS-IS server: queued lines are what the server sends.</summary>
    private sealed class ScriptedTransport : IAprsTransport
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

        public List<string> Written { get; } = [];

        public bool Disposed { get; private set; }

        public static ScriptedTransport LoggedIn(string server, bool verified = false)
        {
            var transport = new ScriptedTransport();
            transport.Server("# aprsc 2.1.19");
            transport.Server($"# logresp N0CALL {(verified ? "verified" : "unverified")}, server {server}");
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
