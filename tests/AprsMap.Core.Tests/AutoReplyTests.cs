// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The away message (docs/features/F2.10-auto-reply.md, acceptance tests 1–8).</summary>
public sealed class AutoReplyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    private static readonly AutoReplySettings On = AutoReplySettings.Off with { On = true };

    private sealed record Station(
        Engine Engine,
        EngineTests.FakeSourceFactory Sources,
        FakeTimeProvider Time,
        TextLogger Log
    ) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }

    private static Station Create(AutoReplySettings? autoReply, bool transmit = true)
    {
        var home = InMemorySettingsStore.Profile("NZ0P-10") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Licensed = true,
            SourceIds = [AprsIs.Id, Tnc.Id],
            AutoReply = autoReply,
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
        var log = new TextLogger();
        var engine = new Engine(Options.Create(new EngineOptions()), factory, store, time, log);
        return new Station(engine, factory, time, log);
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

    /// <summary>The auto-replies sent, once each: a retry is the same line again.</summary>
    private static IEnumerable<string> Replies(EngineTests.FakeSource source) =>
        source.Sent.Where(l => l.Contains(AutoReplySettings.DefaultText, StringComparison.Ordinal)).Distinct();

    /// <summary>Lets the delay pass: the messenger's pump looks every 5 seconds.</summary>
    private static async Task Wait(Station station, int seconds)
    {
        for (var i = 0; i < seconds; i += 5)
        {
            station.Time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
    }

    [Fact]
    public async Task AMessageOnTheTncGetsTheAwayMessageOnTheTncAfterTheDelay()
    {
        using var station = Create(On);
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :are you there?{5");
            await Collect(station.Engine, 2);
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            Assert.Empty(Replies(tnc)); // not before the delay (behavior 6)

            await Wait(station, 30);

            await WaitUntil(() => Task.FromResult(Replies(tnc).Any()));
            Assert.StartsWith(
                $"NZ0P-10>{Brand.Tocall},WIDE1-1,WIDE2-1::NZ0P-9   :{AutoReplySettings.DefaultText}{{",
                Assert.Single(Replies(tnc)),
                StringComparison.Ordinal
            );
            Assert.Empty(Replies(aprsIs)); // back the way it came (behavior 8)
            var messages = await Collect(station.Engine, 2);
            Assert.True(messages.Values.Single(m => m.Outgoing).Automatic); // behavior 9
            Assert.Equal(1, (await station.Engine.GetStatusAsync(Ct)).UnreadMessages); // still unread
            Assert.Contains(
                station.Log.Lines,
                l => l.StartsWith("Auto-reply to NZ0P-9 on tnc", StringComparison.Ordinal)
            );
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task OffByDefaultAMessageGetsNoReply()
    {
        using var station = Create(null);
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :are you there?{5");
            await Collect(station.Engine, 1);

            await Wait(station, 60);

            Assert.Single((await Collect(station.Engine, 1)).Values);
            Assert.Empty(Replies(tnc));
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task OnceAnHourPerCorrespondent()
    {
        using var station = Create(On with { DelaySeconds = 0 });
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :are you there?{5");
            await WaitUntil(() => Task.FromResult(Replies(tnc).Any()));
            station.Time.Advance(TimeSpan.FromMinutes(10));
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :hello?{6");
            await tnc.Write("WA0RC>APRS,WIDE1-1::NZ0P-10  :anyone?{7");

            await WaitUntil(() => Task.FromResult(Replies(tnc).Count() == 2));
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);

            Assert.Single(Replies(tnc), l => l.Contains("::NZ0P-9   :", StringComparison.Ordinal));
            Assert.Single(Replies(tnc), l => l.Contains("::WA0RC    :", StringComparison.Ordinal));
            Assert.Contains(station.Log.Lines, l => l == "No auto-reply to NZ0P-9: OncePer");
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task NoReplyWhileTheOperatorIsSendingByHand()
    {
        using var station = Create(On with { DelaySeconds = 0 });
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await station.Engine.SendMessageAsync("N0ABC", "back in a bit", Ct);
            station.Time.Advance(TimeSpan.FromMinutes(5));

            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :are you there?{5");
            await Collect(station.Engine, 2);
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);

            Assert.Empty(Replies(tnc));
            Assert.Contains(station.Log.Lines, l => l == "No auto-reply to NZ0P-9: NotAway");
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AWaitingReplyIsCancelledWhenTheOperatorAnswersByHand()
    {
        using var station = Create(On);
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :are you there?{5");
            await Collect(station.Engine, 2);
            station.Time.Advance(TimeSpan.FromSeconds(10));

            await station.Engine.SendMessageAsync("NZ0P-9", "yes, here", Ct);
            await Wait(station, 60);

            Assert.Empty(Replies(tnc));
            var reply = (await Collect(station.Engine, 3)).Values.Single(m => m.Automatic);
            Assert.Equal(ChatMessageState.Cancelled, reply.State);
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData("ANSRVR", "J QRP", "Service")]
    [InlineData("EMAIL-2", "Email sent", "Service")]
    [InlineData("NZ0P-9", "AprsMap ping", null)] // ping answers that (behavior 7.1)
    [InlineData("NZ0P-9", "AprsMap pong 2026-10-19 03:00:00Z", "AutomaticMessage")]
    [InlineData("NZ0P-9", "I'm away from the station and will reply when I'm back.", "AutomaticMessage")]
    public async Task ServicesPingsAndAutomaticMessagesGetNoReply(string from, string text, string? reason)
    {
        using var station = Create(On with { DelaySeconds = 0 });
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write($"{from}>APRS,WIDE1-1::NZ0P-10  :{text}{{5");
            await Collect(station.Engine, 1);
            await Wait(station, 10);

            Assert.Empty(Replies(tnc));
            if (reason is not null)
            {
                Assert.Contains(station.Log.Lines, l => l == $"No auto-reply to {from}: {reason}");
            }
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task WithTransmitOffNothingIsSentAndTheLogSaysWhy()
    {
        using var station = Create(On with { DelaySeconds = 0 }, transmit: false);
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :are you there?{5");
            await Collect(station.Engine, 1);
            await Wait(station, 10);

            Assert.Empty(Replies(tnc));
            Assert.Empty(Replies(aprsIs));
            Assert.Contains(station.Log.Lines, l => l == "No auto-reply to NZ0P-9: CantSend");
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData("away {back soon}", 15, 60, 30, EngineErrorCodes.AutoReplyTextInvalid)]
    [InlineData(
        "This away message is far too long to fit in the one APRS message it has to be",
        15,
        60,
        30,
        EngineErrorCodes.AutoReplyTextInvalid
    )]
    [InlineData(null, -1, 60, 30, EngineErrorCodes.AutoReplyLimitInvalid)]
    [InlineData(null, 15, 0, 30, EngineErrorCodes.AutoReplyLimitInvalid)]
    [InlineData(null, 15, 60, 601, EngineErrorCodes.AutoReplyLimitInvalid)]
    public async Task UnusableAutoReplySettingsAreRefused(string? text, int away, int oncePer, int delay, string code)
    {
        using var station = Create(null);
        var settings = await station.Engine.GetSettingsAsync(Ct);
        var profile = settings.Profiles[0] with { AutoReply = new AutoReplySettings(true, text, away, oncePer, delay) };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            station.Engine.SaveSettingsAsync(settings with { Profiles = [profile] }, Ct)
        );

        Assert.Equal(code, error.EngineError.Code);
    }

    private sealed class TextLogger : ILogger<Engine>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => _lines.Enqueue(formatter(state, exception));
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
