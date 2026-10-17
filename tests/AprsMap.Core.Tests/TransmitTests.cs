// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Own station and transmitting (docs/features/F2.1-own-station.md, acceptance tests 1–4, 6).</summary>
public sealed class TransmitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StationProfile Home(string callsign = "N0TEST") =>
        InMemorySettingsStore.Profile(callsign) with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Comment = "Home",
        };

    private static (Engine Engine, EngineTests.FakeSourceFactory Sources, InMemorySettingsStore Store) Create(
        StationProfile? profile = null,
        StationToggles? toggles = null,
        bool receiveOnly = false
    )
    {
        var home = profile ?? Home();
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home, InMemorySettingsStore.Profile("N0TWO")],
                home.Id,
                UnitSettings.Metric,
                Sources: [InMemorySettingsStore.Rotate],
                Toggles: toggles
            )
        );
        var sources = new EngineTests.FakeSourceFactory();
        var engine = new Engine(
            Options.Create(new EngineOptions { ReceiveOnly = receiveOnly }),
            sources,
            store,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z", null)),
            NullLogger<Engine>.Instance
        );
        return (engine, sources, store);
    }

    private static async Task<(Task Run, CancellationTokenSource Cts, EngineTests.FakeSource Session)> Connect(
        Engine engine,
        EngineTests.FakeSourceFactory sources,
        bool verified = true
    )
    {
        var session = sources.Enqueue("T2TEST", verified);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Connected);
        return (run, cts, session);
    }

    private static async Task<string?> RefusalOf(Func<Task> act) =>
        (await Assert.ThrowsAnyAsync<InvalidOperationException>(act) as IHasEngineError)?.EngineError.Code;

    [Fact]
    public async Task TransmitIsOffInNewSettingsAndInSettingsStoredBeforeIt()
    {
        Assert.False(StationToggles.AllOn.Transmit);
        var (engine, _, _) = Create(toggles: null);

        var status = await engine.GetStatusAsync(Ct);

        Assert.False((await engine.GetSettingsAsync(Ct)).EffectiveToggles.Transmit);
        Assert.False(status.Transmit!.On);
    }

    [Fact]
    public async Task TurningTransmitOnNeedsAVerifiedLoginAndThenSendsThePositionByHand()
    {
        var (engine, sources, store) = Create();
        var (run, cts, session) = await Connect(engine, sources);
        using (cts)
        {
            await engine.SetToggleAsync(Toggle.Transmit, true, Ct);
            var status = await engine.GetStatusAsync(Ct);
            Assert.True(status.Transmit!.On);
            Assert.Null(status.Transmit.Blocked);
            Assert.True(store.Stored!.EffectiveToggles.Transmit);

            var listed = new List<PacketView>();
            var watch = Task.Run(
                async () =>
                {
                    await foreach (var p in engine.WatchPacketsAsync(null, Ct))
                    {
                        listed.Add(p);
                        return;
                    }
                },
                Ct
            );
            var sent = await engine.SendPositionAsync(Ct);
            await watch.WaitAsync(Ct);

            var line = Assert.Single(session.Sent);
            Assert.Equal($"N0TEST>{Brand.Tocall}:!4903.50N/07201.75W-Home", line);
            Assert.Equal(PacketMarkers.Sent, sent.Markers);
            Assert.Equal(PacketMarkers.Sent, Assert.Single(listed).Markers);
            Assert.Equal(InMemorySettingsStore.Rotate.Id, sent.SourceId);
            Assert.NotNull(await engine.GetStationAsync("N0TEST", Ct)); // on the map as the network sees it
            Assert.True(store.Stored!.GetActiveProfile()!.PositionConfirmed); // the interlock (behavior 12)
            Assert.NotNull((await engine.GetStatusAsync(Ct)).Transmit!.LastSent);

            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AnUnverifiedLoginCantTurnTransmitOn()
    {
        var (engine, sources, store) = Create();
        var (run, cts, _) = await Connect(engine, sources, verified: false);
        using (cts)
        {
            Assert.Equal(
                EngineErrorCodes.TransmitNotVerified,
                await RefusalOf(() => engine.SetToggleAsync(Toggle.Transmit, true, Ct))
            );
            Assert.False(store.Stored!.EffectiveToggles.Transmit);
            Assert.Equal(
                EngineErrorCodes.TransmitNotVerified,
                (await engine.GetStatusAsync(Ct)).Transmit!.Blocked!.Code
            );
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task APlaceholderCallsignCantTransmit()
    {
        var (engine, sources, _) = Create(Home("N0CALL-9"));
        var (run, cts, _) = await Connect(engine, sources);
        using (cts)
        {
            Assert.Equal(
                EngineErrorCodes.TransmitPlaceholderCallsign,
                await RefusalOf(() => engine.SetToggleAsync(Toggle.Transmit, true, Ct))
            );
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AReceiveOnlyEngineNeverTransmits()
    {
        var (engine, sources, _) = Create(receiveOnly: true);
        var (run, cts, _) = await Connect(engine, sources);
        using (cts)
        {
            Assert.Equal(
                EngineErrorCodes.TransmitReceiveOnly,
                await RefusalOf(() => engine.SetToggleAsync(Toggle.Transmit, true, Ct))
            );
            Assert.True((await engine.GetStatusAsync(Ct)).Transmit!.ReceiveOnly);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task WithoutAprsIsOrAConnectionTransmitCantTurnOn()
    {
        var (engine, _, _) = Create(toggles: new StationToggles(AprsIs: false));
        await engine.GetSettingsAsync(Ct);
        Assert.Equal(
            EngineErrorCodes.TransmitNoAprsIs,
            await RefusalOf(() => engine.SetToggleAsync(Toggle.Transmit, true, Ct))
        );

        var (other, _, _) = Create();
        await other.GetSettingsAsync(Ct); // not running: never connected
        Assert.Equal(
            EngineErrorCodes.TransmitNotConnected,
            await RefusalOf(() => other.SetToggleAsync(Toggle.Transmit, true, Ct))
        );
    }

    [Fact]
    public async Task SendingIsRefusedWhileTransmitIsOffOrHeldAndWithoutALocation()
    {
        var (engine, sources, _) = Create(Home() with { Latitude = null, Longitude = null });
        var session = sources.Enqueue("T2TEST", verified: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Connected);

        Assert.Equal(EngineErrorCodes.TransmitOff, await RefusalOf(() => engine.SendPositionAsync(Ct)));
        await engine.SetToggleAsync(Toggle.Transmit, true, Ct);
        Assert.Equal(EngineErrorCodes.TransmitNoPosition, await RefusalOf(() => engine.SendPositionAsync(Ct)));

        // The connection drops: the switch stays on, sending is held, and the status says why (behavior 8).
        session.Report(new PacketSourceEvent(ConnectionState.Reconnecting, RetryIn: TimeSpan.FromSeconds(5)));
        var status = await engine.GetStatusAsync(Ct);
        Assert.True(status.Transmit!.On);
        Assert.Equal(EngineErrorCodes.TransmitNotConnected, status.Transmit.Blocked!.Code);
        Assert.Equal(EngineErrorCodes.TransmitNotConnected, await RefusalOf(() => engine.SendPositionAsync(Ct)));
        Assert.Empty(session.Sent);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task SavingSettingsNeverTurnsTransmitOnAndSwitchingProfilesTurnsItOff()
    {
        var (engine, sources, store) = Create();
        var (run, cts, _) = await Connect(engine, sources);
        using (cts)
        {
            var settings = await engine.GetSettingsAsync(Ct);
            await engine.SaveSettingsAsync(
                settings with
                {
                    Toggles = settings.EffectiveToggles with { Transmit = true },
                },
                Ct
            );
            Assert.False(store.Stored!.EffectiveToggles.Transmit);

            await engine.SetToggleAsync(Toggle.Transmit, true, Ct);
            settings = await engine.GetSettingsAsync(Ct);
            await engine.SaveSettingsAsync(settings with { Units = UnitSettings.Imperial }, Ct);
            Assert.True(store.Stored!.EffectiveToggles.Transmit); // another change keeps it

            await engine.SaveSettingsAsync(settings with { ActiveProfileId = "N0TWO" }, Ct);
            Assert.False(store.Stored!.EffectiveToggles.Transmit); // consent was for the other callsign

            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ChangingWhatASendSaysClearsTheConfirmationAndSavingCantSetIt()
    {
        var (engine, _, store) = Create(Home() with { PositionConfirmed = true });
        var settings = await engine.GetSettingsAsync(Ct);
        var home = settings.GetActiveProfile()!;

        await engine.SaveSettingsAsync(settings with { Units = UnitSettings.Imperial }, Ct);
        Assert.True(store.Stored!.GetActiveProfile()!.PositionConfirmed); // nothing sent changed

        await engine.SaveSettingsAsync(
            settings with
            {
                Profiles = [home with { Latitude = 50 }, settings.Profiles[1] with { PositionConfirmed = true }],
            },
            Ct
        );
        Assert.False(store.Stored!.GetActiveProfile()!.PositionConfirmed);
        Assert.False(store.Stored!.Profiles[1].PositionConfirmed); // never set by a save
    }

    [Theory]
    [InlineData("A comment that is far too long to fit in a position report", null)]
    [InlineData("Café", null)]
    [InlineData(null, "/")]
    [InlineData(null, "ab")]
    public async Task ACommentOrSymbolThatCantBeSentIsRefused(string? comment, string? symbol)
    {
        var (engine, _, _) = Create();
        var settings = await engine.GetSettingsAsync(Ct);
        var home = settings.GetActiveProfile()! with { Comment = comment, Symbol = symbol };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(settings with { Profiles = [home, settings.Profiles[1]] }, Ct)
        );

        Assert.Equal(
            comment is null ? EngineErrorCodes.ProfileSymbolInvalid : EngineErrorCodes.ProfileCommentInvalid,
            error.EngineError.Code
        );
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
