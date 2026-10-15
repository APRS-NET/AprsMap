// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsMap.Core;
using AprsNet.AprsIsTcpClient;
using Microsoft.Extensions.Logging;
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
        IPacketSourceFactory? sources = null,
        int capacity = 10,
        ILogger<Engine>? logger = null,
        ISettingsStore? settings = null
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
            settings ?? InMemorySettingsStore.WithProfile(),
            time,
            logger ?? NullLogger<Engine>.Instance
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
        await using var watch = engine.WatchPacketsAsync(null, cts.Token).GetAsyncEnumerator(cts.Token);

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
    public async Task FilteredWatchersGetOnlyMatchingPacketsHeldAndLive()
    {
        var engine = CreateEngine(new FakeTimeProvider());
        engine.Accept("N1CALL>APRS:>held, other station");
        engine.Accept("N2CALL>APRS:>held, matching");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var watch = engine
            .WatchPacketsAsync(new PacketFilter(Station: "n2call"), cts.Token)
            .GetAsyncEnumerator(cts.Token);

        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("N2CALL>APRS:>held, matching", watch.Current.Raw);

        engine.Accept("N1CALL>APRS:>live, other station");
        engine.Accept("N2CALL>APRS:>live, matching");
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("N2CALL>APRS:>live, matching", watch.Current.Raw);
        await cts.CancelAsync();
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
        await foreach (var packet in engine.WatchPacketsAsync(null, cts.Token))
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
    public async Task StationWatchersGetASnapshotThenCoalescedChanges()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var engine = CreateEngine(time);
        engine.Accept(Position);
        engine.Accept(Status); // no position: not a station

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var watch = engine.WatchStationsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await watch.MoveNextAsync());
        var station = Assert.Single(watch.Current.Changed);
        Assert.Equal(
            new StationState("N0CALL-9", time.GetUtcNow(), 49.0583, -72.0292, "/>"),
            station with
            {
                Latitude = Math.Round(station.Latitude, 4),
                Longitude = Math.Round(station.Longitude, 4),
            }
        );

        // Two moves and a new station inside one interval arrive as one batch with the latest state only.
        var next = watch.MoveNextAsync().AsTask();
        engine.Accept("N0CALL-9>APRS:!4904.00N/07202.00W>");
        engine.Accept("N0CALL-9>APRS:!4905.00N/07203.00W>");
        engine.Accept("N0NEW>APRS:!4000.00N/10500.00W-");
        await AdvanceBatchesUntil(time, () => next.IsCompleted);
        Assert.True(await next);
        var batch = watch.Current;
        Assert.Empty(batch.Removed);
        Assert.Equal(["N0CALL-9", "N0NEW"], batch.Changed.Select(s => s.Name).Order());
        Assert.Equal(49.0833, batch.Changed.Single(s => s.Name == "N0CALL-9").Latitude, 4);
        Assert.Equal("/-", batch.Changed.Single(s => s.Name == "N0NEW").Symbol);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await watch.MoveNextAsync());
    }

    [Fact]
    public async Task ObjectsAreKeyedByNameAndLeaveTheMapWhenKilled()
    {
        var time = new FakeTimeProvider();
        var engine = CreateEngine(time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var watch = engine.WatchStationsAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // An interval with no changes sends nothing.
        var next = watch.MoveNextAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
        time.Advance(new EngineOptions().StationBatchInterval);
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
        Assert.False(next.IsCompleted);

        engine.Accept("N0CALL>APRS:;LEADER   *092345z4903.50N/07201.75W>Leading");
        await AdvanceBatchesUntil(time, () => next.IsCompleted);
        Assert.True(await next);
        Assert.Equal("LEADER", Assert.Single(watch.Current.Changed).Name);

        next = watch.MoveNextAsync().AsTask();
        engine.Accept("N0CALL>APRS:;LEADER   _092345z4903.50N/07201.75W>Gone");
        await AdvanceBatchesUntil(time, () => next.IsCompleted);
        Assert.True(await next);
        Assert.Empty(watch.Current.Changed);
        Assert.Equal(["LEADER"], watch.Current.Removed);

        // Items work the same way; killing something the map never had sends nothing.
        next = watch.MoveNextAsync().AsTask();
        engine.Accept("N0CALL>APRS:)AID#2!4903.50N/07201.75W+");
        engine.Accept("N0CALL>APRS:;UNKNOWN  _092345z4903.50N/07201.75W>");
        await AdvanceBatchesUntil(time, () => next.IsCompleted);
        Assert.True(await next);
        Assert.Equal("AID#2", Assert.Single(watch.Current.Changed).Name);
        Assert.Empty(watch.Current.Removed);

        next = watch.MoveNextAsync().AsTask();
        engine.Accept("N0CALL>APRS:)AID#2_4903.50N/07201.75W+");
        await AdvanceBatchesUntil(time, () => next.IsCompleted);
        Assert.True(await next);
        Assert.Equal(["AID#2"], watch.Current.Removed);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ALargeSnapshotArrivesInSeveralBatches()
    {
        var engine = CreateEngine(new FakeTimeProvider());
        var count = Engine.SnapshotBatchSize + 5;
        for (var i = 0; i < count; i++)
        {
            engine.Accept($"N{i}>APRS:!4903.50N/07201.75W>");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var watch = engine.WatchStationsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(Engine.SnapshotBatchSize, watch.Current.Changed.Count);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(5, watch.Current.Changed.Count);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ALineThatIsNotLatin1IsDroppedWithoutStoppingTheEngine()
    {
        var logger = new ListLogger();
        var engine = CreateEngine(new FakeTimeProvider(), logger: logger);

        engine.Accept("N0CALL>APRS:>Snowman \u2603");
        engine.Accept(Status);

        Assert.Equal(1, (await engine.GetStatusAsync(Ct)).PacketsReceived);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.IsType<ArgumentException>(entry.Exception);
    }

    [Fact]
    public async Task FirstRunCreatesAProfileFromTheConfiguredDefaults()
    {
        var store = new InMemorySettingsStore();
        var engine = new Engine(
            Options.Create(new EngineOptions { Callsign = " n0call-9 ", Filter = "r/40/-105/50" }),
            new FakeSourceFactory(),
            store,
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );

        var settings = await engine.GetSettingsAsync(Ct);

        var profile = Assert.Single(settings.Profiles);
        Assert.Equal("N0CALL-9", profile.Callsign);
        Assert.Equal("N0CALL-9", profile.Name);
        Assert.Equal("r/40/-105/50", profile.Filter);
        Assert.Equal(profile.Id, settings.ActiveProfileId);
        Assert.Same(settings, store.Stored);
        Assert.Same(settings, await engine.GetSettingsAsync(Ct)); // loaded once
    }

    [Theory]
    [InlineData("")]
    [InlineData("N0CALL-TOOLONGSSID")]
    public async Task WithoutUsableDefaultsTheEngineWaitsForSetupThenConnects(string callsign)
    {
        var store = new InMemorySettingsStore();
        var sources = new FakeSourceFactory();
        var session = sources.Enqueue("T2SETUP");
        var engine = new Engine(
            Options.Create(new EngineOptions { Callsign = callsign }),
            sources,
            store,
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.NotConfigured);
        Assert.Empty((await engine.GetSettingsAsync(Ct)).Profiles);
        Assert.Null(store.Stored); // nothing to store yet
        Assert.Equal(0, sources.Attempts);

        var profile = InMemorySettingsStore.Profile("N0NEW");
        await engine.SaveSettingsAsync(new AppSettings([profile], profile.Id, UnitSettings.Metric), Ct);
        await session.Connected.Task.WaitAsync(Ct);

        var status = await engine.GetStatusAsync(Ct);
        Assert.Equal("N0NEW", status.Callsign);
        Assert.Equal(profile.Id, status.ProfileId);
        Assert.Equal("N0NEW", Assert.Single(sources.Profiles).Callsign);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task SwitchingProfilesReconnectsWithTheNewLogin()
    {
        var home = InMemorySettingsStore.Profile("N0HOME");
        var away = InMemorySettingsStore.Profile("N0AWAY") with { Filter = "r/1/2/3" };
        var store = new InMemorySettingsStore(new AppSettings([home, away], home.Id, UnitSettings.Metric));
        var sources = new FakeSourceFactory();
        var first = sources.Enqueue("T2HOME");
        var second = sources.Enqueue("T2AWAY");
        var engine = CreateEngine(new FakeTimeProvider(), sources, settings: store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await first.Connected.Task.WaitAsync(Ct);
        var settings = await engine.GetSettingsAsync(Ct);

        // Units and names don't touch the connection.
        await engine.SaveSettingsAsync(settings with { Units = UnitSettings.Imperial }, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
        Assert.Equal(1, sources.Attempts);

        await engine.SaveSettingsAsync(settings with { ActiveProfileId = away.Id }, Ct);
        await second.Connected.Task.WaitAsync(Ct);

        Assert.True(first.Disposed);
        Assert.Equal(["N0HOME", "N0AWAY"], sources.Profiles.Select(p => p.Callsign));
        Assert.Equal("r/1/2/3", sources.Profiles.Last().Filter);
        Assert.Equal(away.Id, store.Stored!.ActiveProfileId);
        Assert.Equal(UnitSettings.Metric, store.Stored.Units);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Server == "T2AWAY");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AProfileChangeWhileWaitingToRetryConnectsAtOnce()
    {
        var sources = new FakeSourceFactory();
        sources.EnqueueFailure();
        var fixedUp = sources.Enqueue("T2FIXED");
        var store = InMemorySettingsStore.WithProfile();
        var engine = CreateEngine(new FakeTimeProvider(), sources, settings: store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Reconnecting);
        var settings = await engine.GetSettingsAsync(Ct);
        await engine.SaveSettingsAsync(
            settings with
            {
                Profiles = [settings.Profiles[0] with { Server = "noam.aprs2.net:14580" }],
            },
            Ct
        );

        await fixedUp.Connected.Task.WaitAsync(Ct); // without advancing the fake clock past the retry delay
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task InvalidSettingsAreRejectedAndNotStored()
    {
        var store = InMemorySettingsStore.WithProfile();
        var engine = CreateEngine(new FakeTimeProvider(), settings: store);
        var settings = await engine.GetSettingsAsync(Ct);

        var ex = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(settings with { ActiveProfileId = "nobody" }, Ct)
        );
        Assert.Equal(EngineErrorCodes.ActiveProfileMissing, ex.EngineError.Code);

        Assert.Equal(0, store.Saves);
        Assert.Same(settings, await engine.GetSettingsAsync(Ct));
    }

    [Fact]
    public async Task RemovingEveryProfileDisconnectsUntilSetupAgain()
    {
        var sources = new FakeSourceFactory();
        var session = sources.Enqueue("T2TEST");
        var engine = CreateEngine(new FakeTimeProvider(), sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        await engine.SaveSettingsAsync(AppSettings.Empty, Ct);

        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.NotConfigured);
        Assert.True(session.Disposed);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunConnectsReadsPacketsAndReportsTheServer()
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
    public async Task ADroppedSessionIsReportedAndItsPacketsCarryOn()
    {
        var time = new FakeTimeProvider();
        var sources = new FakeSourceFactory();
        var session = sources.Enqueue("T2FIRST");
        var engine = CreateEngine(time, sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        await session.Write(Position);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).PacketsReceived == 1);

        var closed = new EngineError(EngineErrorCodes.AprsIsServerClosed, "closed");
        session.Report(
            new PacketSourceEvent(ConnectionState.Reconnecting, RetryIn: TimeSpan.FromSeconds(5), Error: closed)
        );
        var dropped = await engine.GetStatusAsync(Ct);
        Assert.Equal(ConnectionState.Reconnecting, dropped.Connection);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(5), dropped.RetryAt);
        Assert.Equal(closed, dropped.ConnectionError);
        session.Report(new PacketSourceEvent(ConnectionState.Connecting));
        session.Report(new PacketSourceEvent(ConnectionState.Connected, new PacketSourceLogin("T2SECOND", true)));
        await session.Write(Status);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).PacketsReceived == 2);

        var status = await engine.GetStatusAsync(Ct);
        Assert.Equal(ConnectionState.Connected, status.Connection);
        Assert.Equal("T2SECOND", status.Server);
        Assert.True(status.Verified);
        Assert.Equal(1, status.Reconnects);
        Assert.Null(status.ConnectionError);
        Assert.Equal(1, sources.Attempts); // the source reconnected itself; the engine kept it
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AFailedFirstLoginSaysWhyAndWhenItRetries()
    {
        var time = new FakeTimeProvider();
        var sources = new FakeSourceFactory();
        sources.EnqueueFailure();
        var engine = CreateEngine(time, sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Reconnecting);

        var status = await engine.GetStatusAsync(Ct);
        Assert.Equal(EngineErrorCodes.AprsIsUnreachable, status.ConnectionError?.Code);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(5), status.RetryAt);
        Assert.Equal("rotate.aprs2.net:14580", status.Server);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ARefusedLoginIsNotRetriedUntilReconnectIsAskedFor()
    {
        var time = new FakeTimeProvider();
        var sources = new FakeSourceFactory();
        sources.EnqueueRefusal();
        var good = sources.Enqueue("T2GOOD");
        var engine = CreateEngine(time, sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Refused);
        var refused = await engine.GetStatusAsync(Ct);
        Assert.Equal(EngineErrorCodes.AprsIsLoginRefused, refused.ConnectionError?.Code);
        Assert.Equal("Login by user not allowed", refused.ConnectionError?.Arg(EngineErrorArgs.Reason));
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
        Assert.Equal(1, sources.Attempts);

        await engine.ReconnectAsync(Ct);
        await good.Connected.Task.WaitAsync(Ct);

        Assert.Equal(2, sources.Attempts);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ARefusalAfterADropEndsTheSessionAndWaits()
    {
        var sources = new FakeSourceFactory();
        var session = sources.Enqueue("T2FIRST");
        var engine = CreateEngine(new FakeTimeProvider(), sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        var error = new EngineError(
            EngineErrorCodes.AprsIsLoginRefused,
            "refused",
            new Dictionary<string, string> { [EngineErrorArgs.Reason] = "Login by user not allowed" }
        );
        session.Report(new PacketSourceEvent(ConnectionState.Refused, Error: error));
        await WaitUntil(() => Task.FromResult(session.Disposed));

        var status = await engine.GetStatusAsync(Ct);
        Assert.Equal(ConnectionState.Refused, status.Connection);
        Assert.Equal(error, status.ConnectionError);
        Assert.Equal(1, sources.Attempts);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ReconnectNowClosesTheSessionAndConnectsAgain()
    {
        var sources = new FakeSourceFactory();
        var first = sources.Enqueue("T2FIRST");
        var second = sources.Enqueue("T2SECOND");
        var engine = CreateEngine(new FakeTimeProvider(), sources);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await first.Connected.Task.WaitAsync(Ct);
        await engine.ReconnectAsync(Ct);
        await second.Connected.Task.WaitAsync(Ct);

        Assert.True(first.Disposed);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Server == "T2SECOND");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ResetDeletesEverythingAndWaitsForSetupWithoutTheConfiguredDefaults()
    {
        var sources = new FakeSourceFactory();
        var session = sources.Enqueue("T2TEST");
        var store = InMemorySettingsStore.WithProfile();
        var engine = CreateEngine(new FakeTimeProvider(), sources, settings: store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        await session.Write(Position);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).PacketsReceived == 1);

        await engine.ResetSettingsAsync(Ct);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.NotConfigured);

        Assert.Null(store.Stored);
        Assert.True(session.Disposed);
        Assert.Empty((await engine.GetSettingsAsync(Ct)).Profiles);
        var status = await engine.GetStatusAsync(Ct);
        Assert.Equal(0, status.PacketsReceived);
        Assert.Null(status.ProfileId);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task TheSweepFadesAndRemovesStationsOnTheEnginesClock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var store = InMemorySettingsStore.WithProfile();
        var engine = CreateEngine(time, settings: store);
        await engine.SaveSettingsAsync(
            (await engine.GetSettingsAsync(Ct)) with
            {
                Aging = new StationAging(5, 10),
            },
            Ct
        );
        engine.Accept(Position);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await AdvanceMinutesUntil(time, () => engine.GetStationAsync("N0CALL-9", Ct).Result?.IsStale == true);
        await AdvanceMinutesUntil(time, () => engine.GetStationAsync("N0CALL-9", Ct).Result is null);

        Assert.Null(await engine.GetStationAsync("NOBODY", Ct));
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AFilterChangeIsSentWithoutANewLogin()
    {
        var sources = new FakeSourceFactory();
        var session = sources.Enqueue("T2TEST");
        var store = InMemorySettingsStore.WithProfile();
        var engine = CreateEngine(new FakeTimeProvider(), sources, settings: store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Connected);
        var settings = await engine.GetSettingsAsync(Ct);
        var filtered = settings.Profiles[0] with { Filter = "r/40/-105/50" };
        await engine.SaveSettingsAsync(settings with { Profiles = [filtered] }, Ct);

        Assert.Equal("r/40/-105/50", Assert.Single(session.Filters).Filter);
        Assert.Equal(1, sources.Attempts);
        Assert.False(session.Disposed);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AFailedFirstLoginBacksOffExponentiallyUpToTheCapThenConnects()
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
                sources.Attempts == attempt
                && (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Reconnecting
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
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Reconnecting);
        await cts.CancelAsync();
        await run;

        Assert.Equal(ConnectionState.Disconnected, (await engine.GetStatusAsync(Ct)).Connection);
        Assert.Equal(1, sources.Attempts);
    }

    [Fact]
    public async Task ThePasscodeCalculatorUsesTheBaseCallsign()
    {
        var engine = CreateEngine(new FakeTimeProvider());
        var expected = AprsIsPasscode.Compute("N0CALL");

        Assert.Equal(expected, await engine.ComputePasscodeAsync(" n0call-9 ", Ct));
        Assert.Equal(expected, await engine.ComputePasscodeAsync("N0CALL", Ct));
        var empty = await Assert.ThrowsAsync<EngineArgumentException>(() => engine.ComputePasscodeAsync(" ", Ct));
        var spaced = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.ComputePasscodeAsync("N0 CALL", Ct)
        );
        Assert.Equal("aprs.callsign-invalid-length", empty.EngineError.Code);
        Assert.Equal("aprs.callsign-invalid-character", spaced.EngineError.Code);
        Assert.Equal("2", spaced.EngineError.Arg(EngineErrorArgs.Index));
        Assert.IsType<AprsNet.Packets.AprsValidationException>(spaced.InnerException);
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.ComputePasscodeAsync(null!, Ct));
    }

    [Fact]
    public async Task WithoutAGeocoderAddressLookupIsOff()
    {
        var engine = CreateEngine(new FakeTimeProvider());

        Assert.Equal(GeocodingInfo.Disabled, await engine.GetGeocodingInfoAsync(Ct));
        var ex = await Assert.ThrowsAsync<EngineOperationException>(() => engine.GeocodeAsync("Boulder", null, Ct));
        Assert.Equal(EngineErrorCodes.GeocodingDisabled, ex.EngineError.Code);
    }

    [Fact]
    public async Task AddressLookupGoesToTheGeocoder()
    {
        var geocoder = new FakeGeocoder();
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            new FakeSourceFactory(),
            new InMemorySettingsStore(),
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance,
            geocoder
        );

        Assert.Same(geocoder.Info, await engine.GetGeocodingInfoAsync(Ct));
        var result = Assert.Single(await engine.GeocodeAsync("Boulder", "en-US", Ct));
        Assert.Equal("Boulder|en-US", result.DisplayName);
    }

    [Fact]
    public async Task ServerPresetsComeFromAprsNetWorldwideFirst()
    {
        var presets = await CreateEngine(new FakeTimeProvider()).GetServerPresetsAsync(Ct);

        Assert.Equal(AprsIsServerPreset.All.Count, presets.Count);
        Assert.Equal(new ServerPreset("rotate", "Worldwide", "rotate.aprs2.net:14580"), presets[0]);
        Assert.All(presets, p => Assert.EndsWith(".aprs2.net:14580", p.Server, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DN70", "DN70", 40.5, -105.0)]
    [InlineData(" dn70kb ", "DN70kb", 40.0625, -105.125)]
    public async Task ALocatorGivesTheCentreOfItsSquare(string text, string locator, double lat, double lon)
    {
        var info = await CreateEngine(new FakeTimeProvider()).ParseLocatorAsync(text, Ct);

        Assert.Equal(locator, info.Locator);
        Assert.Equal(lat, info.Latitude, 6);
        Assert.Equal(lon, info.Longitude, 6);
    }

    [Theory]
    [InlineData("", EngineErrorCodes.LocatorEmpty, null, null)]
    [InlineData("DN7", EngineErrorCodes.LocatorWrongLength, null, null)]
    [InlineData("DN70ZZ", EngineErrorCodes.LocatorInvalidCharacter, "4", "subsquare")]
    [InlineData("DNX0", EngineErrorCodes.LocatorInvalidCharacter, "2", "square")]
    public async Task ABadLocatorSaysWhyAndWhere(string text, string code, string? index, string? part)
    {
        var ex = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            CreateEngine(new FakeTimeProvider()).ParseLocatorAsync(text, Ct)
        );

        Assert.Equal(code, ex.EngineError.Code);
        Assert.Equal(text, ex.EngineError.Arg(EngineErrorArgs.Value));
        Assert.Equal(index, ex.EngineError.Arg(EngineErrorArgs.Index));
        Assert.Equal(part, ex.EngineError.Arg(EngineErrorArgs.Part));
    }

    [Fact]
    public async Task APositionGetsItsSixCharacterLocator()
    {
        var engine = CreateEngine(new FakeTimeProvider());

        Assert.Equal("DN70kb", await engine.GetLocatorAsync(40.0625, -105.125, Ct));
        var ex = await Assert.ThrowsAsync<EngineArgumentException>(() => engine.GetLocatorAsync(91, 0, Ct));
        Assert.Equal(EngineErrorCodes.PositionOffGlobe, ex.EngineError.Code);
        await Assert.ThrowsAsync<EngineArgumentException>(() => engine.GetLocatorAsync(0, double.NaN, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.ParseLocatorAsync(null!, Ct));
    }

    [Fact]
    public void GuardsAgainstNullArguments()
    {
        var engine = CreateEngine(new FakeTimeProvider());

        Assert.Throws<ArgumentNullException>(() => engine.Accept(null!));
        Assert.Throws<ArgumentNullException>(() =>
            new Engine(
                null!,
                new FakeSourceFactory(),
                new InMemorySettingsStore(),
                TimeProvider.System,
                NullLogger<Engine>.Instance
            )
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

    /// <summary>Steps the fake clock by one station batch interval until the condition holds.</summary>
    private static async Task AdvanceBatchesUntil(FakeTimeProvider time, Func<bool> condition)
    {
        for (var step = 0; step < 20 && !condition(); step++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
            time.Advance(new EngineOptions().StationBatchInterval);
            for (var i = 0; i < 20 && !condition(); i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
            }
        }
        Assert.True(condition(), "Condition was not met in time.");
    }

    /// <summary>Steps the fake clock a minute at a time, for the sweep, until the condition holds.</summary>
    private static async Task AdvanceMinutesUntil(FakeTimeProvider time, Func<bool> condition)
    {
        for (var minute = 0; minute < 30 && !condition(); minute++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
            time.Advance(TimeSpan.FromMinutes(1));
            for (var i = 0; i < 20 && !condition(); i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2), TimeProvider.System, Ct);
            }
        }
        Assert.True(condition(), "Condition was not met in time.");
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

    private sealed class FakeGeocoder : IGeocoder
    {
        public GeocodingInfo Info { get; } = new(true, "geo.example", null);

        public Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(
            string query,
            string? language,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<GeocodeResult>>([new($"{query}|{language}", 40, -105)]);
    }

    private sealed class ListLogger : ILogger<Engine>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, exception));
    }

    /// <summary>Hands out scripted sources in order; once the script runs out, every attempt fails.</summary>
    private sealed class FakeSourceFactory : IPacketSourceFactory
    {
        private readonly Queue<FakeSource?> _script = new();
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>The profile of every connection attempt, in order.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<StationProfile> Profiles { get; } = new();

        public FakeSource Enqueue(string server)
        {
            var source = new FakeSource(server);
            _script.Enqueue(source);
            return source;
        }

        public void EnqueueFailure() => _script.Enqueue(null);

        public void EnqueueRefusal() => _script.Enqueue(new FakeSource(server: null, refuse: true));

        public IPacketSource Create(StationProfile profile)
        {
            Profiles.Enqueue(profile);
            Interlocked.Increment(ref _attempts);
            return _script.TryDequeue(out var next) && next is not null ? next : new FakeSource(server: null);
        }
    }

    private sealed class FakeSource(string? server, bool refuse = false, bool verified = false) : IPacketSource
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private Action<PacketSourceEvent>? _stateChanged;

        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        /// <summary>The profiles sent with <see cref="SetFilterAsync"/>.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<StationProfile> Filters { get; } = new();

        public ValueTask Write(string line) => _lines.Writer.WriteAsync(line, Ct);

        /// <summary>Reports an event as the source would after the first login: a drop, a retry, a new login.</summary>
        public void Report(PacketSourceEvent e) => _stateChanged!(e);

        public Task<PacketSourceLogin> StartAsync(
            Action<PacketSourceEvent> stateChanged,
            CancellationToken cancellationToken
        )
        {
            if (refuse)
            {
                throw new LoginRejectedException("Login by user not allowed");
            }
            if (server is null)
            {
                throw new IOException("connection refused");
            }
            _stateChanged = stateChanged;
            Connected.TrySetResult();
            return Task.FromResult(new PacketSourceLogin(server, verified));
        }

        public async IAsyncEnumerable<AprsNet.Packets.AprsPacket> ReadPacketsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await foreach (var line in _lines.Reader.ReadAllAsync(cancellationToken))
            {
                yield return AprsNet.Packets.AprsPacket.Parse(line);
            }
        }

        public ValueTask SetFilterAsync(StationProfile profile, CancellationToken cancellationToken)
        {
            Filters.Enqueue(profile);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
