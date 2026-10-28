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
    public async Task TheStatusSaysHowTheEngineIsInstalled()
    {
        // #453: an app connected to a gateway says which it reached.
        using var desktop = CreateEngine(new FakeTimeProvider());
        Assert.Equal(EngineInstall.Desktop, (await desktop.GetStatusAsync(Ct)).Install);
    }

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
    public async Task ADuplicateIsListedMarkedButDoesNotUpdateTheStation()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var engine = CreateEngine(time);
        var first = time.GetUtcNow();
        engine.Accept("N0CALL-9>APRS,WIDE1-1,qAR,N1GATE:!4903.50N/07201.75W>Heading home");

        time.Advance(TimeSpan.FromSeconds(10)); // the same packet through another IGate
        engine.Accept("N0CALL-9>APRS,N2DIGI*,qAR,N3GATE:!4903.50N/07201.75W>Heading home");
        Assert.Equal(first, (await engine.GetStationAsync("N0CALL-9", Ct))!.LastHeard);

        time.Advance(TimeSpan.FromSeconds(30)); // outside the 30 s window: a new report
        engine.Accept("N0CALL-9>APRS,WIDE1-1,qAR,N1GATE:!4903.50N/07201.75W>Heading home");
        Assert.Equal(time.GetUtcNow(), (await engine.GetStationAsync("N0CALL-9", Ct))!.LastHeard);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var markers = new List<PacketMarkers>();
        await foreach (var packet in engine.WatchPacketsAsync(null, cts.Token))
        {
            markers.Add(packet.Markers);
            if (markers.Count == 3)
            {
                break;
            }
        }
        Assert.Equal(
            [PacketMarkers.Direct, PacketMarkers.Duplicate | PacketMarkers.Digipeated, PacketMarkers.Direct],
            markers
        );
    }

    [Fact]
    public async Task StationsAreMeasuredFromTheActiveProfilesLocation()
    {
        var engine = CreateEngine(new FakeTimeProvider());
        engine.Accept("N0FAR>APRS:!4038.17N/10410.17W-");
        var settings = await engine.GetSettingsAsync(Ct);
        Assert.Null((await engine.GetStationAsync("N0FAR", Ct))!.DistanceKm);

        await engine.SaveSettingsAsync(
            settings with
            {
                Profiles =
                [
                    .. settings.Profiles.Select(p =>
                        p.Id == settings.ActiveProfileId ? p with { Latitude = 40, Longitude = -105 } : p
                    ),
                ],
            },
            Ct
        );

        var station = (await engine.GetStationAsync("N0FAR", Ct))!;
        Assert.Equal(100, station.DistanceKm!.Value, 0);
        Assert.Equal(45, station.BearingDegrees);
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
        await using var watch = engine.WatchStationsAsync(cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);
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

        // Then the tracks (F1.5), as resets.
        Assert.True(await watch.MoveNextAsync());
        var track = Assert.Single(watch.Current.Tracks!);
        Assert.Equal("N0CALL-9", track.Name);
        Assert.True(track.Reset);

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
        Assert.Equal(2, batch.Tracks!.Single(t => t.Name == "N0CALL-9").Added.Count);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await watch.MoveNextAsync());
    }

    [Fact]
    public async Task ObjectsAreKeyedByNameAndLeaveTheMapWhenKilled()
    {
        var time = new FakeTimeProvider();
        var engine = CreateEngine(time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var watch = engine.WatchStationsAsync(cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);

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
        await using var watch = engine.WatchStationsAsync(cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);
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
        var source = Assert.Single(settings.SourceList);
        Assert.Equal(
            (SourceKind.AprsIs, "rotate.aprs2.net", "r/40/-105/50"),
            (source.Kind, source.Name, source.Filter)
        );
        Assert.Equal([source.Id], profile.SourceIds);
        Assert.Equal(profile.Id, settings.ActiveProfileId);
        Assert.Same(settings, store.Stored);
        Assert.Same(settings, await engine.GetSettingsAsync(Ct)); // loaded once
    }

    [Fact]
    public async Task AFirstRunKissSourceWithNoServerIsATncOnlyStation()
    {
        var store = new InMemorySettingsStore();
        var engine = new Engine(
            Options.Create(
                new EngineOptions
                {
                    Callsign = "N0CALL-10",
                    Server = "",
                    Kiss = new KissSeedOptions
                    {
                        Host = " 10.0.0.2 ",
                        Port = 8002,
                        Name = "Pi TNC",
                    },
                }
            ),
            new FakeSourceFactory(),
            store,
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );

        var settings = await engine.GetSettingsAsync(Ct);

        var source = Assert.Single(settings.SourceList); // no APRS-IS (F1.26 behavior 14)
        Assert.Equal(
            (SourceKind.KissTcp, "10.0.0.2", 8002, "Pi TNC"),
            (source.Kind, source.Host, source.Port, source.Name)
        );
        Assert.Equal([source.Id], Assert.Single(settings.Profiles).SourceIds);
    }

    [Fact]
    public async Task AFirstRunUsbTncAndGpsdJoinTheFirstProfile()
    {
        // The gateway service's first-run station (docs/features/F1.37-gateway-service.md, behavior 4).
        var store = new InMemorySettingsStore();
        var engine = new Engine(
            Options.Create(
                new EngineOptions
                {
                    Callsign = "N0CALL-10",
                    Server = "",
                    KissSerial = new KissSerialSeedOptions
                    {
                        Port = "/dev/ttyUSB0",
                        Baud = 9600,
                        Name = "USB TNC",
                    },
                    Gpsd = new GpsdSeedOptions { Host = "localhost" },
                }
            ),
            new FakeSourceFactory(),
            store,
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );

        var settings = await engine.GetSettingsAsync(Ct);

        var tnc = settings.SourceList.Single(s => s.Kind == SourceKind.KissSerial);
        Assert.Equal(("/dev/ttyUSB0", 9600, "USB TNC"), (tnc.SerialPort, tnc.Baud, tnc.Name));
        var gps = settings.SourceList.Single(s => s.Kind == SourceKind.Gps);
        Assert.Equal((GpsLink.Gpsd, "localhost"), (gps.GpsLink, gps.Host));
        Assert.Equal([tnc.Id, gps.Id], Assert.Single(settings.Profiles).SourceIds);
    }

    [Fact]
    public async Task AFirstRunGpsAloneIsNoStation()
    {
        var engine = new Engine(
            Options.Create(
                new EngineOptions
                {
                    Callsign = "N0CALL-10",
                    Server = "",
                    Gpsd = new GpsdSeedOptions { Host = "localhost" },
                }
            ),
            new FakeSourceFactory(),
            new InMemorySettingsStore(),
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );

        Assert.Empty((await engine.GetSettingsAsync(Ct)).Profiles); // hears nothing: waits for setup
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
        await engine.SaveSettingsAsync(
            new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [InMemorySettingsStore.Rotate]),
            Ct
        );
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
        var awaySource = InMemorySettingsStore.Rotate with { Id = "away", Name = "away", Filter = "r/1/2/3" };
        var away = InMemorySettingsStore.Profile("N0AWAY") with { SourceIds = [awaySource.Id] };
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home, away],
                home.Id,
                UnitSettings.Metric,
                Sources: [InMemorySettingsStore.Rotate, awaySource]
            )
        );
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
        Assert.EndsWith("r/1/2/3", sources.LoginFilters.Last(), StringComparison.Ordinal);
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
                Sources = [InMemorySettingsStore.Rotate with { Server = "noam.aprs2.net:14580" }],
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

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    // KISS over serial isn't built yet (it waits for hardware testing of APRS.NET#126).
    private static readonly SourceSettings Gps = new("gps", "GPS", SourceKind.Gps);

    [Fact]
    public async Task APacketOnTwoSourcesIsListedOnceAndSeenOnTheSecond()
    {
        var sources = new FakeSourceFactory();
        var aprsIs = sources.Enqueue("T2TEST");
        var tnc = sources.EnqueueFor(Tnc.Id, "DIREWOLF");
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, Tnc.Id],
        };
        var store = new InMemorySettingsStore(
            new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [InMemorySettingsStore.Rotate, Tnc])
        );
        var engine = CreateEngine(new FakeTimeProvider(), sources, settings: store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await tnc.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Sources is { Count: 2 } all
            && all.All(s => s.State == ConnectionState.Connected)
        );

        const string Line = "N0RF-9>APRS,WIDE1-1:!4903.50N/07201.75W>Heard on RF";
        await tnc.Write(Line);
        await WaitUntil(async () => (await engine.GetStationAsync("N0RF-9", Ct)) is not null);
        await aprsIs.Write("N0RF-9>APRS,WIDE1-1,qAR,N1GATE:!4903.50N/07201.75W>Heard on RF");
        await WaitUntil(async () => (await engine.GetStationAsync("N0RF-9", Ct))?.SeenOn is { Count: 1 });

        var station = (await engine.GetStationAsync("N0RF-9", Ct))!;
        Assert.Equal(("tnc", "rotate"), (station.FirstHeardVia, Assert.Single(station.SeenOn!)));
        Assert.Equal(1, station.Packets); // not counted twice
        await using var watch = engine.WatchPacketsAsync(null, cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(("tnc", "rotate"), (watch.Current.SourceId, Assert.Single(watch.Current.SeenOn!)));
        Assert.Equal(1, (await engine.GetStatusAsync(Ct)).PacketsReceived); // listed once

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task EachSourceHasItsOwnDashboardAndTheSourcesCompare()
    {
        // #454: a copy heard on a second source counts for that source, and once for the whole feed.
        var sources = new FakeSourceFactory();
        var aprsIs = sources.Enqueue("T2TEST");
        var tnc = sources.EnqueueFor(Tnc.Id, "DIREWOLF");
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, Tnc.Id],
        };
        var store = new InMemorySettingsStore(
            new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [InMemorySettingsStore.Rotate, Tnc])
        );
        var engine = CreateEngine(new FakeTimeProvider(), sources, settings: store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await tnc.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Sources is { Count: 2 } all
            && all.All(s => s.State == ConnectionState.Connected)
        );

        await tnc.Write("N0RF-9>APRS,WIDE1-1:!4903.50N/07201.75W>Heard on RF");
        await WaitUntil(async () => (await engine.GetStationAsync("N0RF-9", Ct)) is not null);
        await aprsIs.Write("N0RF-9>APRS,WIDE1-1,qAR,N1GATE:!4903.50N/07201.75W>Heard on RF");
        await aprsIs.Write("N0NET>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W-Internet only");
        await WaitUntil(async () => (await engine.GetStationAsync("N0NET", Ct)) is not null);

        var all = await engine.GetDashboardAsync(DashboardRange.Hour, cancellationToken: Ct);
        var rf = await engine.GetSourceDashboardAsync(DashboardRange.Hour, Tnc.Id, cancellationToken: Ct);
        var net = await engine.GetSourceDashboardAsync(DashboardRange.Hour, "rotate", cancellationToken: Ct);

        Assert.Equal(2, all.Packets.Sum()); // the copy once
        Assert.Equal((1, 1, "tnc"), (rf.Packets.Sum(), rf.StationsHeardTotal, rf.SourceId));
        Assert.Equal((2, 2), (net.Packets.Sum(), net.StationsHeardTotal)); // the copy counts here
        Assert.Equal(
            [new DashboardSourceRow("rotate", 2, 2, 1), new DashboardSourceRow("tnc", 1, 1, 0)],
            all.Comparison
        );
        Assert.Null(rf.Comparison);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AListOfOneSourceGetsAPacketWhenItsHeardThereSecond()
    {
        var sources = new FakeSourceFactory();
        var aprsIs = sources.Enqueue("T2TEST");
        var tnc = sources.EnqueueFor(Tnc.Id, "DIREWOLF");
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, Tnc.Id],
        };
        var store = new InMemorySettingsStore(
            new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [InMemorySettingsStore.Rotate, Tnc])
        );
        var engine = CreateEngine(new FakeTimeProvider(), sources, settings: store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await tnc.Connected.Task.WaitAsync(Ct);
        await using var onlyTnc = engine
            .WatchPacketsAsync(new PacketFilter(SourceId: Tnc.Id), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        var next = onlyTnc.MoveNextAsync();

        await aprsIs.Write("N0NET-1>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W-Internet only");
        await aprsIs.Write("N0RF-9>APRS,WIDE1-1,qAR,N1GATE:!4903.50N/07201.75W>Heard on RF");
        await WaitUntil(async () => (await engine.GetStationAsync("N0RF-9", Ct)) is not null);
        Assert.False(next.IsCompleted); // nothing from the TNC yet
        await tnc.Write("N0RF-9>APRS,WIDE1-1:!4903.50N/07201.75W>Heard on RF");

        Assert.True(await next);
        Assert.Equal(("N0RF-9", "rotate"), (onlyTnc.Current.Source, onlyTnc.Current.SourceId));
        Assert.Equal(Tnc.Id, Assert.Single(onlyTnc.Current.SeenOn!));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task SwitchingBetweenProfilesThatShareASourceKeepsItOpen()
    {
        var sources = new FakeSourceFactory();
        sources.Enqueue("T2HOME");
        var away = sources.Enqueue("T2AWAY");
        var tnc = sources.EnqueueFor(Tnc.Id, "DIREWOLF");
        var home = InMemorySettingsStore.Profile("N0HOME") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, Tnc.Id],
        };
        var portable = InMemorySettingsStore.Profile("N0AWAY") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, Tnc.Id],
        };
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home, portable],
                home.Id,
                UnitSettings.Metric,
                Sources: [InMemorySettingsStore.Rotate, Tnc]
            )
        );
        var engine = CreateEngine(new FakeTimeProvider(), sources, settings: store);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await tnc.Connected.Task.WaitAsync(Ct);
        var settings = await engine.GetSettingsAsync(Ct);
        await engine.SaveSettingsAsync(settings with { ActiveProfileId = portable.Id }, Ct);
        await away.Connected.Task.WaitAsync(Ct); // APRS-IS logs in again, as the other callsign

        Assert.Equal(1, sources.Opens[Tnc.Id]); // the TNC stayed open
        Assert.False(tnc.Disposed);

        // A profile without it closes it.
        await engine.SaveSettingsAsync(
            settings with
            {
                ActiveProfileId = portable.Id,
                Profiles = [home, portable with { SourceIds = [InMemorySettingsStore.Rotate.Id] }],
            },
            Ct
        );
        await WaitUntil(() => Task.FromResult(tnc.Disposed));
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AKindNotBuiltYetSaysSoAndIsNotRetried()
    {
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, Gps.Id],
        };
        var engine = new Engine(
            Options.Create(new EngineOptions()),
            new NotBuiltFactory( // every kind is built now: one that says GPS isn't
                new AprsIsPacketSourceFactory(
                    Options.Create(new EngineOptions()),
                    NullLoggerFactory.Instance,
                    TimeProvider.System
                )
            ),
            new InMemorySettingsStore(
                new AppSettings(
                    [profile],
                    profile.Id,
                    UnitSettings.Metric,
                    Sources: [InMemorySettingsStore.Rotate with { Server = "127.0.0.1:1" }, Gps]
                )
            ),
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Sources?.FirstOrDefault(s => s.Id == Gps.Id) is { } tnc
            && tnc.Error?.Code == EngineErrorCodes.SourceKindNotBuilt
        );

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task MapViewsChangeTheFilterAtMostOnceInTheIntervalWithoutANewLogin()
    {
        var sources = new FakeSourceFactory();
        var session = sources.Enqueue("T2TEST");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var store = InMemorySettingsStore.WithProfile();
        var engine = CreateEngine(time, sources, settings: store);
        var settings = await engine.GetSettingsAsync(Ct);
        await engine.SaveSettingsAsync(
            settings with
            {
                Profiles = [settings.Profiles[0] with { Latitude = 40, Longitude = -105 }],
            },
            Ct
        );
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        Assert.Equal("r/40/-105/50", Assert.Single(sources.LoginFilters));
        Assert.Equal("r/40/-105/50", (await engine.GetStatusAsync(Ct)).Filter);

        time.Advance(TimeSpan.FromSeconds(10));
        await engine.SetMapViewAsync(new MapViewReport(39.1, -94.58, 80), Ct);
        await WaitUntil(() => Task.FromResult(session.Filters.Count == 1));
        Assert.Equal("r/40/-105/50 r/39.1/-94.58/80", session.Filters.Single());

        // Two more changes inside the interval are merged into one send when it ends.
        await engine.SetMapViewAsync(new MapViewReport(35, -90, 80), Ct);
        await engine.SetMapViewAsync(new MapViewReport(35, -90, 80, "W1AW-9"), Ct);
        Assert.Single(session.Filters);
        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntil(() => Task.FromResult(session.Filters.Count == 2));
        Assert.Equal("r/40/-105/50 r/35/-90/80 f/W1AW-9/50", session.Filters.Last());
        Assert.Equal(1, sources.Attempts); // never a new login

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
        await engine.SaveSettingsAsync(
            settings with
            {
                Sources = [InMemorySettingsStore.Rotate with { Filter = "r/40/-105/50" }],
            },
            Ct
        );

        Assert.EndsWith("r/40/-105/50", Assert.Single(session.Filters), StringComparison.Ordinal);
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
    [Fact]
    public async Task ADestinationsDistanceBearingAndLineAreFromMyLocation()
    {
        var store = InMemorySettingsStore.WithProfile();
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            new FakeSourceFactory(),
            store,
            TimeProvider.System,
            NullLogger<Engine>.Instance
        );
        Assert.Equal(DestinationInfo.Unknown, await engine.GetDestinationAsync(39.74, -104.99, Ct)); // no location yet
        var settings = await engine.GetSettingsAsync(Ct);
        await engine.SaveSettingsAsync(
            settings with
            {
                Profiles = [settings.Profiles[0] with { Latitude = 40.015, Longitude = -105.27 }],
            },
            Ct
        );

        var denver = await engine.GetDestinationAsync(39.74, -104.99, Ct); // Boulder to Denver (F1.18)

        Assert.InRange(denver.DistanceKm!.Value, 38, 40);
        Assert.InRange(denver.Bearing!.Value, 135, 145); // south-east
        Assert.Equal(Engine.DestinationSegments + 1, denver.Line.Count);
        Assert.Equal(
            (40.015, -105.27),
            (Math.Round(denver.Line[0].Latitude, 6), Math.Round(denver.Line[0].Longitude, 6))
        );
        Assert.Equal(39.74, denver.Line[^1].Latitude, 6);

        // A transatlantic line bends north of the straight one on the map (acceptance test 3).
        var london = await engine.GetDestinationAsync(51.5, -0.12, Ct);
        Assert.True(london.Line.Max(p => p.Latitude) > 51.5);
    }

    /// <summary>A factory that says GPS isn't built, as a kind still to come would.</summary>
    private sealed class NotBuiltFactory(IPacketSourceFactory inner) : IPacketSourceFactory
    {
        public IPacketSource Create(StationProfile profile, SourceSettings source, string filter) =>
            source.Kind == SourceKind.Gps
                ? throw new NotSupportedException("Not built.")
                : inner.Create(profile, source, filter);
    }

    internal sealed class FakeSourceFactory : IPacketSourceFactory
    {
        private readonly Queue<FakeSource?> _script = new();
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>The profile of every connection attempt, in order.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<StationProfile> Profiles { get; } = new();

        public FakeSource Enqueue(string server, bool verified = false)
        {
            var source = new FakeSource(server, verified: verified);
            _script.Enqueue(source);
            return source;
        }

        public void EnqueueFailure() => _script.Enqueue(null);

        public void EnqueueRefusal() => _script.Enqueue(new FakeSource(server: null, refuse: true));

        /// <summary>The filter each login was made with.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> LoginFilters { get; } = new();

        /// <summary>The connection each non-APRS-IS source opens, by source id.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FakeSource> _others = new();

        /// <summary>How many times each source was opened, by source id.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> Opens { get; } = new();

        /// <summary>The connection a TNC-like source with <paramref name="sourceId"/> opens.</summary>
        public FakeSource EnqueueFor(string sourceId, string server) => _others[sourceId] = new FakeSource(server);

        /// <summary>Any other source, such as a GPS, that <paramref name="sourceId"/> opens.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPacketSource> _given = new();

        /// <summary>Opens <paramref name="connection"/> for the source with <paramref name="sourceId"/>.</summary>
        public void Give(string sourceId, IPacketSource connection) => _given[sourceId] = connection;

        public IPacketSource Create(StationProfile profile, SourceSettings source, string filter)
        {
            Opens.AddOrUpdate(source.Id, 1, (_, n) => n + 1);
            if (_given.TryRemove(source.Id, out var given))
            {
                return given;
            }
            if (source.Kind != SourceKind.AprsIs)
            {
                return _others.TryGetValue(source.Id, out var other) ? other : new FakeSource(server: null);
            }
            Profiles.Enqueue(profile);
            LoginFilters.Enqueue(filter);
            Interlocked.Increment(ref _attempts);
            return _script.TryDequeue(out var next) && next is not null ? next : new FakeSource(server: null);
        }
    }

    internal sealed class FakeSource(string? server, bool refuse = false, bool verified = false)
        : IPacketSource,
            IPacketSender
    {
        /// <summary>The packets sent, as text.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Sent { get; } = new();

        public ValueTask SendAsync(AprsNet.Packets.AprsPacket packet, CancellationToken cancellationToken)
        {
            Sent.Enqueue(packet.ToString());
            return ValueTask.CompletedTask;
        }

        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private Action<PacketSourceEvent>? _stateChanged;

        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        /// <summary>The filters sent with <see cref="SetFilterAsync"/>.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Filters { get; } = new();

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

        public ValueTask SetFilterAsync(string filter, CancellationToken cancellationToken)
        {
            Filters.Enqueue(filter);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
