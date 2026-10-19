// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The ping responder (docs/features/F2.13-ping.md, acceptance tests 1–8).</summary>
public sealed class PingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    private sealed record Station(Engine Engine, EngineTests.FakeSourceFactory Sources, FakeTimeProvider Time)
        : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }

    private static Station Create(PingSettings? ping, bool transmit = true)
    {
        var home = InMemorySettingsStore.Profile("NZ0P-10") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Licensed = true,
            SourceIds = [AprsIs.Id, Tnc.Id],
            Ping = ping,
        };
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home],
                home.Id,
                UnitSettings.Metric,
                Sources: [AprsIs, Tnc],
                Toggles: StationToggles.AllOn with
                {
                    Transmit = transmit,
                }
            )
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T03:12:45Z", null));
        var factory = new EngineTests.FakeSourceFactory();
        var engine = new Engine(Options.Create(new EngineOptions()), factory, store, time, NullLogger<Engine>.Instance);
        return new Station(engine, factory, time);
    }

    private static async Task<(
        Task Run,
        CancellationTokenSource Cts,
        EngineTests.FakeSource AprsIs,
        EngineTests.FakeSource Tnc
    )> Run(Station station)
    {
        var aprsIs = station.Sources.Enqueue("T2TEST", verified: true);
        var tnc = station.Sources.EnqueueFor(Tnc.Id, "Direwolf");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = station.Engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await tnc.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () =>
            (await station.Engine.GetStatusAsync(Ct)).Sources!.All(s => s.State == ConnectionState.Connected)
        );
        return (run, cts, aprsIs, tnc);
    }

    private static IEnumerable<string> Pongs(EngineTests.FakeSource source) =>
        source.Sent.Where(l => l.Contains(" pong ", StringComparison.Ordinal));

    [Fact]
    public async Task APingOnTheTncIsAnsweredOnTheTncWithThePongAndTheUtcTime()
    {
        using var station = Create(PingSettings.Off with { On = true });
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :aprsmap PING from the car{5");

            await WaitUntil(() => Task.FromResult(Pongs(tnc).Any()));
            var pong = Assert.Single(Pongs(tnc));
            Assert.StartsWith(
                $"NZ0P-10>{Brand.Tocall},WIDE1-1,WIDE2-1::NZ0P-9   :{Brand.DisplayName} pong 2026-10-19 03:12:45Z{{",
                pong,
                StringComparison.Ordinal
            );
            Assert.Empty(Pongs(aprsIs)); // back the way it came (behavior 9)
            var messages = await Collect(station.Engine, 2);
            var ping = messages.Values.Single(m => !m.Outgoing);
            Assert.Equal(PingResult.Answered, ping.Ping);
            Assert.True(ping.Read); // not unread (behavior 11)
            Assert.True(messages.Values.Single(m => m.Outgoing).Automatic);
            Assert.Equal(0, (await station.Engine.GetStatusAsync(Ct)).UnreadMessages);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task APingOverAprsIsIsAnsweredOverAprsIs()
    {
        using var station = Create(PingSettings.Off with { On = true });
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await aprsIs.Write("N0ABC>APRS,TCPIP*,qAC,T2TEST::NZ0P-10  :AprsMap ping{1");

            await WaitUntil(() => Task.FromResult(Pongs(aprsIs).Any()));
            Assert.Empty(Pongs(tnc));
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(false, true, "AprsMap ping", PingResult.Off)] // off by default (behavior 1)
    [InlineData(true, false, "AprsMap ping", PingResult.CantSend)] // Transmit off (behavior 2)
    [InlineData(true, true, "did my AprsMap ping work?", null)] // starts with it, or isn't one (behavior 4)
    [InlineData(true, true, "AprsMap pong 2026-10-19 03:00:00Z", null)] // a pong is never answered (behavior 5)
    public async Task OnlyAPingIsAnsweredAndOnlyWhenItMayBe(bool on, bool transmit, string text, PingResult? result)
    {
        using var station = Create(PingSettings.Off with { On = on }, transmit);
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write($"NZ0P-9>APRS,WIDE1-1::NZ0P-10  :{text}{{5");

            var received = (await Collect(station.Engine, 1)).Values.Single(m => !m.Outgoing);
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);

            Assert.Equal(result, received.Ping);
            Assert.Empty(Pongs(tnc));
            Assert.Empty(Pongs(aprsIs));
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(PingWho.MyCallsign, null, "NZ0P-9", PingResult.Answered)]
    [InlineData(PingWho.MyCallsign, null, "N0ABC", PingResult.NotAllowed)]
    [InlineData(PingWho.Callsigns, "WA0RC*", "WA0RC-7", PingResult.Answered)]
    [InlineData(PingWho.Callsigns, "K0K", "K0K-1", PingResult.Answered)]
    [InlineData(PingWho.Callsigns, "K0K-2", "K0K-1", PingResult.NotAllowed)]
    public async Task WhoMayPingIsTheOperatorsChoice(PingWho who, string? callsigns, string from, PingResult result)
    {
        using var station = Create(
            PingSettings.Off with
            {
                On = true,
                Who = who,
                Callsigns = callsigns is null ? null : [callsigns],
            }
        );
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write($"{from}>APRS,WIDE1-1::NZ0P-10  :AprsMap ping{{5");

            var received = (await Collect(station.Engine, 1)).Values.First(m => !m.Outgoing);

            Assert.Equal(result, received.Ping);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task OnePongAnIntervalForTheWholeStation()
    {
        using var station = Create(PingSettings.Off with { On = true, IntervalSeconds = 10 });
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :AprsMap ping{1");
            await WaitUntil(() => Task.FromResult(Pongs(tnc).Any()));
            station.Time.Advance(TimeSpan.FromSeconds(5));
            await tnc.Write("WA0RC>APRS,WIDE1-1::NZ0P-10  :AprsMap ping{2"); // another station, still too soon

            var messages = (await Collect(station.Engine, 3))
                .Values.Where(m => !m.Outgoing)
                .OrderBy(m => m.At)
                .ToList();

            Assert.Equal([PingResult.Answered, PingResult.TooSoon], messages.Select(m => m.Ping));
            Assert.Single(Pongs(tnc));
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData("ping{x", null, 60, EngineErrorCodes.PingPhraseInvalid)]
    [InlineData("AprsMap ping", "AprsMap ping back", 60, EngineErrorCodes.PingPhrasesSame)]
    [InlineData(null, null, 5, EngineErrorCodes.PingIntervalTooShort)]
    public async Task UnusablePingSettingsAreRefused(string? ping, string? pong, int interval, string code)
    {
        using var station = Create(null);
        var settings = await station.Engine.GetSettingsAsync(Ct);
        var profile = settings.Profiles[0] with
        {
            Ping = new PingSettings(true, ping, pong, IntervalSeconds: interval),
        };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            station.Engine.SaveSettingsAsync(settings with { Profiles = [profile] }, Ct)
        );

        Assert.Equal(code, error.EngineError.Code);
    }

    private static async Task<Dictionary<string, ChatMessage>> Collect(Engine engine, int count)
    {
        for (var i = 0; i < 40; i++)
        {
            var latest = new Dictionary<string, ChatMessage>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            try
            {
                await foreach (var message in engine.WatchMessagesAsync(cts.Token))
                {
                    latest[message.Id] = message;
                }
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested) { }
            if (latest.Count >= count)
            {
                return latest;
            }
        }
        Assert.Fail($"Fewer than {count} messages streamed.");
        return [];
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
