// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

public class EngineTests
{
    private const string Position = "N0CALL-9>APRS,WIDE1-1:!4903.50N/07201.75W>Heading home";
    private const string Status = "N0CALL>APRS:>Net tonight";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Engine CreateEngine(
        FakeTimeProvider time,
        IPacketLineSourceFactory? sources = null,
        int capacity = 10
    ) =>
        new(
            Options.Create(
                new EngineOptions
                {
                    RecentPacketCapacity = capacity,
                    InitialRetryDelay = TimeSpan.FromSeconds(5),
                    MaxRetryDelay = TimeSpan.FromSeconds(20),
                }
            ),
            sources ?? new FakeSourceFactory(),
            time,
            NullLogger<Engine>.Instance
        );

    [Fact]
    public async Task AcceptCountsPacketsAndIgnoresLinesWithoutAHeader()
    {
        var engine = CreateEngine(new FakeTimeProvider());

        engine.Accept(Position);
        engine.Accept("# aprsc 2.1.19 13 Oct 2026 16:30:00 GMT T2TEST 1.2.3.4:14580");
        engine.Accept(Status);

        var status = await engine.GetStatusAsync(Ct);
        Assert.Equal(2, status.PacketsReceived);
        Assert.Equal(ConnectionState.Disconnected, status.Connection);
        Assert.False(string.IsNullOrEmpty(status.Version));
    }

    [Fact]
    public async Task WatchersGetRecentPacketsThenLiveOnes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 16, 30, 0, TimeSpan.Zero));
        var engine = CreateEngine(time);
        engine.Accept(Position);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var watch = engine.WatchPacketsAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("Position", watch.Current.Type);
        Assert.Equal(time.GetUtcNow(), watch.Current.ReceivedAt);

        engine.Accept(Status);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("Status", watch.Current.Type);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await watch.MoveNextAsync());
    }

    [Fact]
    public async Task RecentHistoryIsCapped()
    {
        var engine = CreateEngine(new FakeTimeProvider(), capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            engine.Accept($"N{i}CALL>APRS:>status {i}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var replay = new List<PacketView>();
        await foreach (var packet in engine.WatchPacketsAsync(cts.Token))
        {
            replay.Add(packet);
            if (replay.Count == 3)
            {
                await cts.CancelAsync();
                break;
            }
        }

        Assert.Equal(["N2CALL", "N3CALL", "N4CALL"], replay.Select(p => p.Source));
    }

    [Fact]
    public async Task RunConnectsReadsLinesAndReportsTheServer()
    {
        var sources = new FakeSourceFactory();
        var session = sources.Enqueue("T2TEST");
        var engine = CreateEngine(new FakeTimeProvider(), sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        await session.Write(Position);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).PacketsReceived == 1);

        var status = await engine.GetStatusAsync(Ct);
        Assert.Equal(ConnectionState.Connected, status.Connection);
        Assert.Equal("T2TEST", status.Server);

        await cts.CancelAsync();
        await run;
        Assert.Equal(ConnectionState.Disconnected, (await engine.GetStatusAsync(Ct)).Connection);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task RunBacksOffExponentiallyUpToTheCapThenReconnects()
    {
        var time = new FakeTimeProvider();
        var sources = new FakeSourceFactory();
        for (var i = 0; i < 4; i++)
        {
            sources.EnqueueFailure();
        }
        var good = sources.Enqueue("T2GOOD");
        var engine = CreateEngine(time, sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        var delays = new List<int>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await WaitUntil(async () =>
                sources.Attempts == attempt && (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Faulted
            );
            delays.Add(await AdvanceUntil(time, () => sources.Attempts > attempt));
        }
        await good.Connected.Task.WaitAsync(Ct);

        Assert.Equal([5, 10, 20, 20], delays);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task CancellingWhileWaitingToRetryStopsCleanly()
    {
        var sources = new FakeSourceFactory();
        sources.EnqueueFailure();
        var engine = CreateEngine(new FakeTimeProvider(), sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Faulted);
        await cts.CancelAsync();
        await run;

        Assert.Equal(ConnectionState.Disconnected, (await engine.GetStatusAsync(Ct)).Connection);
        Assert.Equal(1, sources.Attempts);
    }

    [Fact]
    public void GuardsAgainstNullArguments()
    {
        var engine = CreateEngine(new FakeTimeProvider());

        Assert.Throws<ArgumentNullException>(() => engine.Accept(null!));
        Assert.Throws<ArgumentNullException>(() =>
            new Engine(null!, new FakeSourceFactory(), TimeProvider.System, NullLogger<Engine>.Instance)
        );
    }

    [Fact]
    public async Task EachSessionIsTracedWithTheServerName()
    {
        // The listener is process-wide, so it also sees sessions from tests running in parallel (HostingTests).
        var stopped = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == EngineTelemetry.Name,
            Sample = (ref _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var sources = new FakeSourceFactory();
        var session = sources.Enqueue("T2TRACE");
        var engine = CreateEngine(new FakeTimeProvider(), sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        await cts.CancelAsync();
        await run;

        Assert.Single(
            stopped,
            a => a.OperationName == "aprs-is.session" && Equals(a.GetTagItem("aprs-is.server"), "T2TRACE")
        );
    }

    /// <summary>Steps the fake clock one second at a time until the condition holds; returns the seconds taken.</summary>
    private static async Task<int> AdvanceUntil(FakeTimeProvider time, Func<bool> condition)
    {
        // Let the engine's continuation reach its Task.Delay before the clock moves.
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
        for (var seconds = 1; seconds <= 120; seconds++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            for (var i = 0; i < 20 && !condition(); i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2), TimeProvider.System, Ct);
            }
            if (condition())
            {
                return seconds;
            }
        }
        Assert.Fail("The engine did not retry.");
        return -1;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Yield();
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
        }
        Assert.Fail("Condition was not met in time.");
    }

    /// <summary>Hands out scripted sources in order; once the script runs out, every attempt fails.</summary>
    private sealed class FakeSourceFactory : IPacketLineSourceFactory
    {
        private readonly Queue<FakeSource?> _script = new();
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public FakeSource Enqueue(string server)
        {
            var source = new FakeSource(server);
            _script.Enqueue(source);
            return source;
        }

        public void EnqueueFailure() => _script.Enqueue(null);

        public IPacketLineSource Create(EngineOptions options)
        {
            Interlocked.Increment(ref _attempts);
            return _script.TryDequeue(out var next) && next is not null ? next : new FakeSource(server: null);
        }
    }

    private sealed class FakeSource(string? server) : IPacketLineSource
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public ValueTask Write(string line) => _lines.Writer.WriteAsync(line, Ct);

        public Task<string> ConnectAsync(CancellationToken cancellationToken)
        {
            if (server is null)
            {
                throw new IOException("connection refused");
            }
            Connected.TrySetResult();
            return Task.FromResult(server);
        }

        public async IAsyncEnumerable<string> ReadLinesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await foreach (var line in _lines.Reader.ReadAllAsync(cancellationToken))
            {
                yield return line;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
