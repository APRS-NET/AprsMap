// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>
/// The APRS-IS feed apart from the connection (docs/features/F1.42-aprs-is-feed.md, acceptance tests 1–5): with the feed
/// off, the IGate and messages still use APRS-IS, but nothing from it is listed, plotted or counted.
/// </summary>
public sealed class AprsIsFeedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "NinoTNC", SourceKind.KissTcp, Host: "127.0.0.1");

    private const string Call = "NZ0P-4";

    private static (Engine Engine, FakeTimeProvider Time) Create(
        EngineTests.FakeSourceFactory factory,
        StationToggles toggles
    )
    {
        var home = InMemorySettingsStore.Profile(Call) with
        {
            Passcode = 12345,
            Latitude = 39.0,
            Longitude = -94.68,
            Licensed = true,
            PositionConfirmed = true,
            SourceIds = [AprsIs.Id, Tnc.Id],
            IGate = true,
        };
        var store = new InMemorySettingsStore(
            new AppSettings([home], home.Id, UnitSettings.Metric, Sources: [AprsIs, Tnc], Toggles: toggles)
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-17T18:00:00Z", null));
        return (
            new Engine(Options.Create(new EngineOptions()), factory, store, time, NullLogger<Engine>.Instance),
            time
        );
    }

    private static readonly StationToggles FeedOff = StationToggles.AllOn with { Transmit = true, AprsIsFeed = false };

    private static async Task<(
        Task Run,
        CancellationTokenSource Cts,
        EngineTests.FakeSource AprsIs,
        EngineTests.FakeSource Tnc
    )> Run(Engine engine, EngineTests.FakeSourceFactory factory)
    {
        var aprsIs = factory.Enqueue("T2TEST", verified: true);
        var tnc = factory.EnqueueFor(Tnc.Id, "NinoTNC");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await tnc.Connected.Task.WaitAsync(Ct);
        return (run, cts, aprsIs, tnc);
    }

    [Fact]
    public async Task WithTheFeedOffAprsIsPacketsArentListedButRfOnesAre()
    {
        var factory = new EngineTests.FakeSourceFactory();
        var (engine, _) = Create(factory, FeedOff);
        using (engine)
        {
            var (run, cts, aprsIs, tnc) = await Run(engine, factory);
            using (cts)
            {
                await aprsIs.Write("K0ABC-9>APRS,TCPIP*,qAC,T2TEST:!3900.00N/09440.00W>From the internet");
                await tnc.Write("W1AW-9>APRS,WIDE1-1:!3901.00N/09441.00W>Heard here");
                await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).PacketsReceived == 1);
                await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);

                Assert.Equal(1, (await engine.GetStatusAsync(Ct)).PacketsReceived); // only the RF one
                Assert.NotNull(await engine.GetStationAsync("W1AW-9", Ct));
                Assert.Null(await engine.GetStationAsync("K0ABC-9", Ct)); // not plotted
                await cts.CancelAsync();
                await run;
            }
        }
    }

    [Fact]
    public async Task WithTheFeedOffTheIGateAndMessagesStillWork()
    {
        var factory = new EngineTests.FakeSourceFactory();
        var (engine, _) = Create(factory, FeedOff);
        using (engine)
        {
            var (run, cts, aprsIs, tnc) = await Run(engine, factory);
            using (cts)
            {
                await tnc.Write("W1AW-9>APRS,WIDE1-1:!3901.00N/09441.00W>Heard here");
                await WaitUntil(() =>
                    Task.FromResult(aprsIs.Sent.Any(l => l.Contains(",qAO," + Call, StringComparison.Ordinal)))
                ); // RF to APRS-IS

                await aprsIs.Write("K0ABC>APRS,TCPIP*,qAC,T2TEST::" + Call.PadRight(9) + ":Hello there{1");
                await WaitUntil(async () =>
                    (await engine.GetConversationsAsync(Ct)).Any(c => c.Correspondent == "K0ABC")
                ); // a message to this station still arrives

                Assert.Null(await engine.GetStationAsync("K0ABC", Ct)); // the message's sender isn't plotted
                // Listed: the RF packet, and this station's own ack of the message, as sent; not the message itself.
                Assert.Equal(2, (await engine.GetStatusAsync(Ct)).PacketsReceived);
                await cts.CancelAsync();
                await run;
            }
        }
    }

    [Fact]
    public async Task TheFilterFollowsTheFeedWithoutANewLogin()
    {
        var factory = new EngineTests.FakeSourceFactory();
        var (engine, time) = Create(factory, FeedOff);
        using (engine)
        {
            var (run, cts, aprsIs, _) = await Run(engine, factory);
            using (cts)
            {
                Assert.Equal("b/" + Call, Assert.Single(factory.LoginFilters)); // no area

                time.Advance(TimeSpan.FromMinutes(1));
                await engine.SetToggleAsync(Toggle.AprsIsFeed, true, Ct);
                await WaitUntil(() => Task.FromResult(aprsIs.Filters.Count == 1));
                Assert.StartsWith("r/39/-94.68/", aprsIs.Filters.Single(), StringComparison.Ordinal);

                time.Advance(TimeSpan.FromMinutes(1));
                await engine.SetToggleAsync(Toggle.AprsIsFeed, false, Ct);
                await WaitUntil(() => Task.FromResult(aprsIs.Filters.Count == 2));
                Assert.Equal("b/" + Call, aprsIs.Filters.Last());
                Assert.Equal(1, factory.Attempts); // never a new login
                Assert.True((await engine.GetSettingsAsync(Ct)).EffectiveToggles.ConnectsAprsIs);
                await cts.CancelAsync();
                await run;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStationSavedBeforeTheFeedFollowsItsOldSwitch(bool aprsIs)
    {
        // Behavior 4: a station saved with APRS-IS off stays quiet; on, both are on.
        var toggles = StationToggles.AllOn with { AprsIs = aprsIs };

        Assert.Equal((aprsIs, aprsIs), (toggles.ConnectsAprsIs, toggles.ShowsAprsIs));
    }

    [Fact]
    public async Task ConnectingAQuietStationKeepsItsFeedOff()
    {
        var factory = new EngineTests.FakeSourceFactory();
        var (engine, _) = Create(factory, StationToggles.AllOn with { AprsIs = false }); // saved before the feed
        using (engine)
        {
            await engine.SetToggleAsync(Toggle.AprsIs, true, Ct);

            var toggles = (await engine.GetSettingsAsync(Ct)).EffectiveToggles;
            Assert.Equal((true, false), (toggles.ConnectsAprsIs, toggles.ShowsAprsIs)); // IGate on, map still quiet
        }
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
