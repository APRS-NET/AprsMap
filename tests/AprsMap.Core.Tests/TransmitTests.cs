// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Own station and transmitting (docs/features/F2.1-own-station.md, acceptance tests 1–9).</summary>
public sealed class TransmitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate with { Transmit = true };

    private static readonly SourceSettings Tnc = new(
        "tnc",
        "Direwolf",
        SourceKind.KissTcp,
        Host: "127.0.0.1",
        Transmit: true
    );

    private static StationProfile Home(string callsign = "N0TEST", params SourceSettings[] sources) =>
        InMemorySettingsStore.Profile(callsign) with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Comment = "Home",
            Licensed = true,
            SourceIds = sources.Length == 0 ? [AprsIs.Id] : [.. sources.Select(s => s.Id)],
        };

    private sealed record Station(
        Engine Engine,
        EngineTests.FakeSourceFactory Sources,
        InMemorySettingsStore Store,
        FakeTimeProvider Time
    );

    private static Station Create(
        StationProfile? profile = null,
        StationToggles? toggles = null,
        bool receiveOnly = false,
        SourceSettings[]? sources = null
    )
    {
        var home = profile ?? Home();
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home, InMemorySettingsStore.Profile("N0TWO") with { Licensed = true }],
                home.Id,
                UnitSettings.Metric,
                Sources: sources ?? [AprsIs, Tnc],
                Toggles: toggles
            )
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z", null));
        var factory = new EngineTests.FakeSourceFactory();
        var engine = new Engine(
            Options.Create(new EngineOptions { ReceiveOnly = receiveOnly }),
            factory,
            store,
            time,
            NullLogger<Engine>.Instance
        );
        return new Station(engine, factory, store, time);
    }

    /// <summary>Runs the engine with APRS-IS connected (verified unless said otherwise) and, when given, the TNC open.</summary>
    private static async Task<(
        Task Run,
        CancellationTokenSource Cts,
        EngineTests.FakeSource AprsIs,
        EngineTests.FakeSource Tnc
    )> Run(Station station, bool verified = true, bool aprsIs = true)
    {
        var session = station.Sources.Enqueue("T2TEST", verified);
        var tnc = station.Sources.EnqueueFor(Tnc.Id, "Direwolf");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = station.Engine.RunAsync(cts.Token);
        if (aprsIs)
        {
            await session.Connected.Task.WaitAsync(Ct);
            await WaitUntil(async () =>
                (await station.Engine.GetStatusAsync(Ct)).Connection == ConnectionState.Connected
            );
        }
        if ((await station.Engine.GetSettingsAsync(Ct)).GetActiveProfile()!.SourceIds.Contains(Tnc.Id))
        {
            await tnc.Connected.Task.WaitAsync(Ct);
            await WaitUntil(async () =>
                (await station.Engine.GetStatusAsync(Ct)).Sources!.Any(s =>
                    s.Id == Tnc.Id && s.State == ConnectionState.Connected
                )
            );
        }
        return (run, cts, session, tnc);
    }

    private static async Task<string?> RefusalOf(Func<Task> act) =>
        (await Assert.ThrowsAnyAsync<InvalidOperationException>(act) as IHasEngineError)?.EngineError.Code;

    [Fact]
    public async Task TransmitIsOffInNewSettingsAndInSettingsStoredBeforeIt()
    {
        Assert.False(StationToggles.AllOn.Transmit);
        Assert.False(InMemorySettingsStore.Rotate.Transmit);
        Assert.False(InMemorySettingsStore.Profile("N0NEW").Licensed);
        Assert.False(InMemorySettingsStore.Profile("N0NEW").DisableTransmit);
        var station = Create(toggles: null);

        var status = await station.Engine.GetStatusAsync(Ct);

        Assert.False((await station.Engine.GetSettingsAsync(Ct)).EffectiveToggles.Transmit);
        Assert.False(status.Transmit!.On);
    }

    [Fact]
    public async Task SendPositionNowGoesOutOnAprsIsAndTheTncAndIsListedAsOurs()
    {
        var station = Create(Home(sources: [AprsIs, Tnc]));
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct);
            var status = await station.Engine.GetStatusAsync(Ct);
            Assert.True(status.Transmit!.On);
            Assert.Null(status.Transmit.Blocked);
            Assert.All(status.Sources!, s => Assert.True(s.CanTransmit));

            var sent = await station.Engine.SendPositionAsync(null, Ct);

            Assert.Equal($"N0TEST>{Brand.Tocall}:=4903.50N/07201.75W-Home", Assert.Single(aprsIs.Sent));
            Assert.Equal($"N0TEST>{Brand.Tocall},WIDE1-1,WIDE2-1:=4903.50N/07201.75W-Home", Assert.Single(tnc.Sent));
            Assert.Equal([AprsIs.Id, Tnc.Id], sent.Select(p => p.SourceId));
            Assert.All(sent, p => Assert.Equal(PacketMarkers.Sent, p.Markers));
            Assert.NotNull(await station.Engine.GetStationAsync("N0TEST", Ct));
            Assert.True(station.Store.Stored!.GetActiveProfile()!.PositionConfirmed); // the interlock (behavior 12)
            Assert.NotNull((await station.Engine.GetStatusAsync(Ct)).Transmit!.LastSent);

            await station.Engine.SendPositionAsync(Tnc.Id, Ct); // one source, from its pill
            Assert.Single(aprsIs.Sent);
            Assert.Equal(2, tnc.Sent.Count);

            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ASourceWithoutTxEnabledIsNeverSentOn()
    {
        var quiet = Tnc with { Transmit = false };
        var station = Create(Home(sources: [AprsIs, quiet]), sources: [AprsIs, quiet]);
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct);

            await station.Engine.SendPositionAsync(null, Ct);
            Assert.Equal(
                EngineErrorCodes.TransmitSourceDisabled,
                await RefusalOf(() => station.Engine.SendPositionAsync(quiet.Id, Ct))
            );
            Assert.Equal(
                EngineErrorCodes.TransmitSourceDisabled,
                await RefusalOf(() => station.Engine.TestSendAsync(quiet.Id, Ct))
            );

            Assert.Single(aprsIs.Sent);
            Assert.Empty(tnc.Sent);
            var status = (await station.Engine.GetStatusAsync(Ct)).Sources!.Single(s => s.Id == quiet.Id);
            Assert.False(status.TransmitEnabled);
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(false, false, EngineErrorCodes.TransmitNotLicensed)]
    [InlineData(true, true, EngineErrorCodes.TransmitProfileDisabled)]
    public async Task AProfileThatIsntLicensedOrHasDisableTxSendsNothing(bool licensed, bool disabled, string code)
    {
        var station = Create(Home(sources: [AprsIs, Tnc]) with { Licensed = licensed, DisableTransmit = disabled });
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            Assert.Equal(code, await RefusalOf(() => station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct)));
            Assert.Equal(code, (await station.Engine.GetStatusAsync(Ct)).Transmit!.Blocked!.Code);
            Assert.Empty(aprsIs.Sent);
            Assert.Empty(tnc.Sent);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AListenerIdNeverTransmitsEvenMarkedLicensed()
    {
        var station = Create(Home("SWL482913"));
        var (run, cts, _, _) = await Run(station);
        using (cts)
        {
            Assert.Equal(
                EngineErrorCodes.TransmitNotLicensed,
                await RefusalOf(() => station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct))
            );
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ListenerIdsAreSwlAndSixDigitsAndAreNotCallsigns()
    {
        var station = Create();

        var id = await station.Engine.NewListenerIdAsync(Ct);

        Assert.Matches("^SWL[0-9]{6}$", id);
        Assert.True(ListenerId.Is(id));
        Assert.False(ListenerId.Is("N0TEST"));
        Assert.False(ListenerId.Is("SWL12345A"));
    }

    [Fact]
    public async Task AnUnverifiedLoginCantSendOnAprsIsButTheTncStillCan()
    {
        var station = Create(Home(sources: [AprsIs, Tnc]));
        var (run, cts, aprsIs, tnc) = await Run(station, verified: false);
        using (cts)
        {
            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct); // the TNC can send

            await station.Engine.SendPositionAsync(null, Ct);

            Assert.Empty(aprsIs.Sent);
            Assert.Single(tnc.Sent);
            var status = await station.Engine.GetStatusAsync(Ct);
            Assert.Equal(
                EngineErrorCodes.TransmitNotVerified,
                status.Sources!.Single(s => s.Id == AprsIs.Id).TransmitBlocked!.Code
            );
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task APlaceholderCallsignCantTransmit()
    {
        var station = Create(Home("N0CALL-9"));
        var (run, cts, _, _) = await Run(station);
        using (cts)
        {
            Assert.Equal(
                EngineErrorCodes.TransmitPlaceholderCallsign,
                await RefusalOf(() => station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct))
            );
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AReceiveOnlyEngineNeverTransmits()
    {
        var station = Create(receiveOnly: true);
        var (run, cts, _, _) = await Run(station);
        using (cts)
        {
            Assert.Equal(
                EngineErrorCodes.TransmitReceiveOnly,
                await RefusalOf(() => station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct))
            );
            Assert.True((await station.Engine.GetStatusAsync(Ct)).Transmit!.ReceiveOnly);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task WithoutATxEnabledSourceOrAConnectionTransmitCantTurnOn()
    {
        var none = Create(sources: [InMemorySettingsStore.Rotate, Tnc with { Transmit = false }]);
        await none.Engine.GetSettingsAsync(Ct);
        Assert.Equal(
            EngineErrorCodes.TransmitNoSource,
            await RefusalOf(() => none.Engine.SetToggleAsync(Toggle.Transmit, true, Ct))
        );

        var offline = Create();
        await offline.Engine.GetSettingsAsync(Ct); // not running: never connected
        Assert.Equal(
            EngineErrorCodes.TransmitNotConnected,
            await RefusalOf(() => offline.Engine.SetToggleAsync(Toggle.Transmit, true, Ct))
        );
    }

    [Fact]
    public async Task SendingIsRefusedWhileTransmitIsOffOrHeldAndWithoutALocation()
    {
        var station = Create(Home() with { Latitude = null, Longitude = null });
        var (run, cts, session, _) = await Run(station);
        using (cts)
        {
            Assert.Equal(
                EngineErrorCodes.TransmitOff,
                await RefusalOf(() => station.Engine.SendPositionAsync(null, Ct))
            );
            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct);
            Assert.Equal(
                EngineErrorCodes.TransmitNoPosition,
                await RefusalOf(() => station.Engine.SendPositionAsync(null, Ct))
            );

            // The connection drops: the switch stays on, sending is held, and the status says why (behavior 7).
            session.Report(new PacketSourceEvent(ConnectionState.Reconnecting, RetryIn: TimeSpan.FromSeconds(5)));
            var status = await station.Engine.GetStatusAsync(Ct);
            Assert.True(status.Transmit!.On);
            Assert.Equal(EngineErrorCodes.TransmitNotConnected, status.Transmit.Blocked!.Code);
            Assert.Equal(
                EngineErrorCodes.TransmitNotConnected,
                await RefusalOf(() => station.Engine.SendPositionAsync(null, Ct))
            );
            Assert.Empty(session.Sent);

            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task MyLastPositionSaysHowItGotOutOnRfAndToAprsIs()
    {
        var station = Create(Home(sources: [AprsIs, Tnc]));
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct);
            await station.Engine.SendPositionAsync(null, Ct);
            await WaitUntil(() => Task.FromResult(!tnc.Sent.IsEmpty && !aprsIs.Sent.IsEmpty));
            var sent = tnc.Sent.First();
            var information = sent[(sent.IndexOf(':') + 1)..];

            var last = (await station.Engine.GetStatusAsync(Ct)).Transmit!.LastPosition!;
            Assert.True(last.Following);
            Assert.True(last.SentOnRf);
            Assert.Equal(2, last.Sources.Count);
            Assert.Equal([HeardBackKind.Direct], last.Heard.Select(h => h.Kind)); // sent to APRS-IS by this station

            await tnc.Write($"N0TEST>{Brand.Tocall},K0HQ-1*,WIDE2-1:{information}"); // a digipeater's copy, on my TNC
            await aprsIs.Write($"N0TEST>{Brand.Tocall},K0HQ-1*,WIDE2-1,qAR,N0IGT:{information}"); // gated by an IGate
            await WaitUntil(async () =>
                (await station.Engine.GetStatusAsync(Ct)).Transmit!.LastPosition!.Heard.Count == 3
            );
            last = (await station.Engine.GetStatusAsync(Ct)).Transmit!.LastPosition!;
            Assert.Equal(
                [
                    (HeardBackKind.Direct, AprsIs.Name),
                    (HeardBackKind.Repeated, "K0HQ-1"),
                    (HeardBackKind.Gated, "N0IGT"),
                ],
                last.Heard.Select(h => (h.Kind, h.By))
                    .OrderBy(h => h.Kind == HeardBackKind.Direct ? 0 : 1)
                    .ThenBy(h => h.By)
            ); // the two copies come on two sources, in either order

            station.Time.Advance(Engine.FollowFor + TimeSpan.FromSeconds(1));
            Assert.False((await station.Engine.GetStatusAsync(Ct)).Transmit!.LastPosition!.Following);

            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task MyLastPositionsRoutesOutGoThroughTheDigipeaterToTheIGate()
    {
        var station = Create(Home(sources: [AprsIs, Tnc]));
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            Assert.Empty(await station.Engine.GetRoutesOutAsync(Ct)); // nothing sent yet
            await aprsIs.Write("K0HQ-1>APRS,TCPIP*,qAC,T2TEST:!4910.00N/07210.00W#digi");
            await aprsIs.Write("N0IGT>APRS,TCPIP*,qAC,T2TEST:!4920.00N/07220.00W&igate");
            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct);
            await station.Engine.SendPositionAsync(null, Ct);
            await WaitUntil(() => Task.FromResult(!tnc.Sent.IsEmpty && !aprsIs.Sent.IsEmpty));
            Assert.Empty(await station.Engine.GetRoutesOutAsync(Ct)); // sent to APRS-IS directly: no route to draw
            var sent = tnc.Sent.First();
            var information = sent[(sent.IndexOf(':') + 1)..];

            await tnc.Write($"N0TEST>{Brand.Tocall},K0HQ-1*,WIDE2-1:{information}");
            await aprsIs.Write($"N0TEST>{Brand.Tocall},K0HQ-1*,WIDE2-1,qAR,N0IGT:{information}");
            await WaitUntil(async () => (await station.Engine.GetRoutesOutAsync(Ct)).Count == 2);

            var routes = await station.Engine.GetRoutesOutAsync(Ct);
            Assert.All(routes, r => Assert.Equal("N0TEST", r.Name));
            Assert.All(routes, r => Assert.NotNull(r.Latitude)); // from where it was sent
            Assert.Equal(
                ["K0HQ-1", "K0HQ-1 > N0IGT (IGate)"],
                routes
                    .Select(r => string.Join(" > ", r.Hops.Select(h => h.IsGateway ? $"{h.Name} (IGate)" : h.Name)))
                    .Order(StringComparer.Ordinal)
            ); // the RF copy ends at the digipeater, not back here
            Assert.All(routes.SelectMany(r => r.Hops), h => Assert.NotNull(h.Latitude));

            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task TestSendGoesOutOnOneSourceAndSaysWhoRepeatedIt()
    {
        var station = Create(Home(sources: [AprsIs, Tnc]));
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            Assert.Equal(EngineErrorCodes.TransmitOff, await RefusalOf(() => station.Engine.TestSendAsync(Tnc.Id, Ct)));
            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct);

            var test = station.Engine.TestSendAsync(Tnc.Id, Ct);
            await WaitUntil(() => Task.FromResult(!tnc.Sent.IsEmpty));
            var line = Assert.Single(tnc.Sent);
            Assert.Equal($"N0TEST>{Brand.Tocall},WIDE1-1,WIDE2-1:>{Brand.DisplayName} test", line);
            Assert.Empty(aprsIs.Sent);
            await tnc.Write($"N0TEST>{Brand.Tocall},K0HQ-1*,WIDE2-1:>{Brand.DisplayName} test"); // a digipeater's copy

            var result = await test.WaitAsync(Ct);

            Assert.Equal(("K0HQ-1", false), (result.HeardThrough, result.ByIgate));
            Assert.Equal(PacketMarkers.Sent, result.Sent.Markers);

            var quiet = station.Engine.TestSendAsync(Tnc.Id, Ct);
            await WaitUntil(() => Task.FromResult(tnc.Sent.Count == 2));
            station.Time.Advance(Engine.TestListenTime + TimeSpan.FromSeconds(1));
            Assert.Null((await quiet.WaitAsync(Ct)).HeardThrough); // not heard back

            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task SavingSettingsNeverTurnsTransmitOnAndSwitchingProfilesTurnsItOff()
    {
        var station = Create();
        var (run, cts, _, _) = await Run(station);
        using (cts)
        {
            var settings = await station.Engine.GetSettingsAsync(Ct);
            await station.Engine.SaveSettingsAsync(
                settings with
                {
                    Toggles = settings.EffectiveToggles with { Transmit = true },
                },
                Ct
            );
            Assert.False(station.Store.Stored!.EffectiveToggles.Transmit);

            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct);
            settings = await station.Engine.GetSettingsAsync(Ct);
            await station.Engine.SaveSettingsAsync(settings with { Units = UnitSettings.Imperial }, Ct);
            Assert.True(station.Store.Stored!.EffectiveToggles.Transmit); // another change keeps it

            await station.Engine.SaveSettingsAsync(settings with { ActiveProfileId = "N0TWO" }, Ct);
            Assert.False(station.Store.Stored!.EffectiveToggles.Transmit); // consent was for the other callsign

            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ChangingWhatASendSaysClearsTheConfirmationAndSavingCantSetIt()
    {
        var station = Create(Home() with { PositionConfirmed = true });
        var settings = await station.Engine.GetSettingsAsync(Ct);
        var home = settings.GetActiveProfile()!;

        await station.Engine.SaveSettingsAsync(settings with { Units = UnitSettings.Imperial }, Ct);
        Assert.True(station.Store.Stored!.GetActiveProfile()!.PositionConfirmed); // nothing sent changed

        await station.Engine.SaveSettingsAsync(
            settings with
            {
                Profiles = [home with { Latitude = 50 }, settings.Profiles[1] with { PositionConfirmed = true }],
            },
            Ct
        );
        Assert.False(station.Store.Stored!.GetActiveProfile()!.PositionConfirmed);
        Assert.False(station.Store.Stored!.Profiles[1].PositionConfirmed); // never set by a save
    }

    [Theory]
    [InlineData("A comment that is far too long to fit in a position report", null, null)]
    [InlineData("Café", null, null)]
    [InlineData(null, "/", null)]
    [InlineData(null, "ab", null)]
    [InlineData(null, null, "WIDE1-1,not a path")]
    public async Task ACommentSymbolOrTxPathThatCantBeSentIsRefused(string? comment, string? symbol, string? path)
    {
        var station = Create();
        var settings = await station.Engine.GetSettingsAsync(Ct);
        var home = settings.GetActiveProfile()! with { Comment = comment, Symbol = symbol };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            station.Engine.SaveSettingsAsync(
                settings with
                {
                    Profiles = [home, settings.Profiles[1]],
                    Sources = [AprsIs, Tnc with { TxPath = path }],
                },
                Ct
            )
        );

        Assert.Equal(
            path is not null ? EngineErrorCodes.SourceTxPathInvalid
                : comment is null ? EngineErrorCodes.ProfileSymbolInvalid
                : EngineErrorCodes.ProfileCommentInvalid,
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
