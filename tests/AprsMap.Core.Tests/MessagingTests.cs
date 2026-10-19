// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Messaging (docs/features/F2.4-messaging.md, acceptance tests 1–6, 8).</summary>
public sealed class MessagingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    private sealed record Station(
        Engine Engine,
        EngineTests.FakeSourceFactory Sources,
        InMemoryMessageStore Messages,
        FakeTimeProvider Time
    ) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }

    private static Station Create(
        bool transmit = true,
        InMemoryMessageStore? messages = null,
        FakeTimeProvider? time = null
    )
    {
        var home = InMemorySettingsStore.Profile("N0TEST") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Licensed = true,
            SourceIds = [AprsIs.Id, Tnc.Id],
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
        time ??= new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z", null));
        messages ??= new InMemoryMessageStore();
        var factory = new EngineTests.FakeSourceFactory();
        var engine = new Engine(
            Options.Create(new EngineOptions()),
            factory,
            store,
            time,
            NullLogger<Engine>.Instance,
            messages: messages
        );
        return new Station(engine, factory, messages, time);
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

    [Fact]
    public async Task AMessageHeardOnTwoSourcesIsShownOnceAndAckedWhereItCameFirst()
    {
        using var station = Create();
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await aprsIs.Write("N1ABC>APRS,TCPIP*,qAC,T2TEST::N0TEST   :Hello{42");
            await WaitUntil(async () => (await station.Engine.GetConversationsAsync(Ct)).Count == 1); // first here
            await tnc.Write("N1ABC>APRS,WIDE1-1::N0TEST   :Hello{42");
            await WaitUntil(async () => (await station.Engine.GetStatusAsync(Ct)).PacketsReceived >= 2); // the copy is in

            await WaitUntil(() => Task.FromResult(!aprsIs.Sent.IsEmpty));
            Assert.Equal($"N0TEST>{Brand.Tocall}::N1ABC    :ack42", Assert.Single(aprsIs.Sent));
            var conversation = Assert.Single(await station.Engine.GetConversationsAsync(Ct));
            Assert.Equal(("N1ABC", 1), (conversation.Correspondent, conversation.Unread));
            Assert.Equal("Hello", conversation.Last!.Text);
            Assert.Equal(1, (await station.Engine.GetStatusAsync(Ct)).UnreadMessages);

            await station.Engine.MarkReadAsync("N1ABC", Ct);
            Assert.Equal(0, (await station.Engine.GetStatusAsync(Ct)).UnreadMessages);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task LongTextIsSplitAndEachMessageIsDeliveredWhenAcked()
    {
        using var station = Create();
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            var text = string.Join(' ', Enumerable.Repeat("seventeen chars!", 9)); // 152 characters
            var made = await station.Engine.SendMessageAsync("n1abc", text, Ct);

            Assert.Equal(3, made.Count);
            Assert.All(made, m => Assert.True(m.Text.Length <= 67));
            Assert.Equal(text, string.Join(' ', made.Select(m => m.Text)));

            // Over APRS-IS (not heard on RF), one at a time: the first goes now.
            await WaitUntil(() => Task.FromResult(!aprsIs.Sent.IsEmpty));
            var first = Assert.Single(aprsIs.Sent);
            Assert.StartsWith($"N0TEST>{Brand.Tocall}::N1ABC    :seventeen chars!", first);
            var id = first[(first.LastIndexOf('{') + 1)..];

            await aprsIs.Write($"N1ABC>APRS,TCPIP*,qAC,T2TEST::N0TEST   :ack{id}");

            await WaitUntil(async () =>
                (await station.Engine.GetConversationsAsync(Ct)).Single().Last is { } last && aprsIs.Sent.Count == 2
            );
            var states = await Collect(station.Engine, 3);
            Assert.Equal(ChatMessageState.Delivered, states[made[0].Id].State);
            Assert.Equal(ChatMessageState.Sent, states[made[1].Id].State);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AutomaticGoesOverRfToAStationTheTncHeard()
    {
        using var station = Create();
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("N1ABC-9>APRS,WIDE1-1:!4903.50N/07201.75W>"); // heard here, on RF
            await WaitUntil(async () => await station.Engine.GetStationAsync("N1ABC-9", Ct) is not null);

            await station.Engine.SendMessageAsync("N1ABC-9", "On RF", Ct);

            await WaitUntil(() => Task.FromResult(!tnc.Sent.IsEmpty));
            Assert.StartsWith($"N0TEST>{Brand.Tocall},WIDE1-1,WIDE2-1::N1ABC-9  :On RF{{", Assert.Single(tnc.Sent));
            Assert.Empty(aprsIs.Sent);
            Assert.Equal("Direwolf", Assert.Single(await station.Engine.GetConversationsAsync(Ct)).Route);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task WhenEveryRfTryGoesUnackedItIsSentOnceMoreOverAprsIs()
    {
        using var station = Create();
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("N1ABC-9>APRS,WIDE1-1:!4903.50N/07201.75W>");
            await WaitUntil(async () => await station.Engine.GetStationAsync("N1ABC-9", Ct) is not null);
            await station.Engine.SendMessageAsync("N1ABC-9", "Anyone?", Ct);
            await WaitUntil(() => Task.FromResult(tnc.Sent.Count == 1));

            // The default schedule: six tries, each when the message says it's next (30 s, 1, 2, 4 and 8 minutes apart).
            for (var tries = 2; tries <= 6; tries++)
            {
                var next = (await Collect(station.Engine, 1)).Values.Single(m => m.Outgoing).NextTry!.Value;
                station.Time.Advance(next - station.Time.GetUtcNow() + TimeSpan.FromSeconds(1));
                var expected = tries;
                await WaitUntil(() => Task.FromResult(tnc.Sent.Count == expected));
            }
            Assert.Empty(aprsIs.Sent);
            station.Time.Advance(TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(1)); // the last wait for an ack

            await WaitUntil(() =>
                Task.FromResult(aprsIs.Sent.Any(l => l.Contains("::N1ABC-9  :Anyone?{", StringComparison.Ordinal)))
            );
            Assert.Equal(6, tnc.Sent.Count); // no more on RF
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task WithTransmitOffMessagesWaitAndReceivedOnesAreNotAcked()
    {
        using var station = Create(transmit: false);
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            var made = Assert.Single(await station.Engine.SendMessageAsync("N1ABC", "Later", Ct));
            await aprsIs.Write("N1ABC>APRS,TCPIP*,qAC,T2TEST::N0TEST   :Are you there{7");
            await WaitUntil(async () => (await station.Engine.GetConversationsAsync(Ct)).Single().Unread == 1);

            Assert.Empty(aprsIs.Sent);
            var messages = await Collect(station.Engine, 2);
            Assert.Equal(ChatMessageState.Queued, messages[made.Id].State);
            Assert.True(messages.Values.Single(m => !m.Outgoing).NotAcknowledged);

            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct);
            await WaitUntil(() =>
                Task.FromResult(aprsIs.Sent.Any(l => l.Contains(":Later{", StringComparison.Ordinal)))
            );
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ConversationsAndMessageIdsCarryOnAfterARestart()
    {
        var store = new InMemoryMessageStore();
        string firstId;
        using (var station = Create(messages: store))
        {
            var (run, cts, aprsIs, _) = await Run(station);
            using (cts)
            {
                await station.Engine.SendMessageAsync("N1ABC", "One", Ct);
                await WaitUntil(() => Task.FromResult(!aprsIs.Sent.IsEmpty));
                firstId = aprsIs.Sent.Single()[(aprsIs.Sent.Single().LastIndexOf('{') + 1)..];
                await station.Engine.FlushMessagesAsync();
                await cts.CancelAsync();
                await run;
            }
        }

        using var again = Create(messages: store);
        var (run2, cts2, aprsIs2, _) = await Run(again);
        using (cts2)
        {
            var kept = Assert.Single(await again.Engine.GetConversationsAsync(Ct));
            Assert.Equal("One", kept.Last!.Text);
            Assert.Equal(ChatMessageState.Failed, kept.Last.State); // in flight when it closed: shown failed

            await again.Engine.SendMessageAsync("N1ABC", "Two", Ct);
            await WaitUntil(() => Task.FromResult(!aprsIs2.Sent.IsEmpty));
            var nextId = aprsIs2.Sent.Single()[(aprsIs2.Sent.Single().LastIndexOf('{') + 1)..];
            Assert.NotEqual(firstId, nextId); // IDs don't repeat
            await cts2.CancelAsync();
            await run2;
        }
    }

    [Theory]
    [InlineData("N1ABC", "Pipe | here", EngineErrorCodes.MessageTextInvalid)]
    [InlineData("N1ABC", "Café", EngineErrorCodes.MessageTextInvalid)]
    [InlineData("N1ABC", "", EngineErrorCodes.MessageTooLong)]
    [InlineData("not a call!", "Hi", EngineErrorCodes.MessageCallsignInvalid)]
    public async Task WhatCantBeSentIsRefused(string to, string text, string code)
    {
        using var station = Create();

        var error = await Assert.ThrowsAnyAsync<ArgumentException>(() => station.Engine.SendMessageAsync(to, text, Ct));

        Assert.Equal(code, ((IHasEngineError)error).EngineError.Code);
    }

    [Fact]
    public void TextIsSplitBetweenWords()
    {
        var parts = Messenger.Split(new string('a', 60) + " " + new string('b', 20)).ToList();

        Assert.Equal([new string('a', 60), new string('b', 20)], parts);
    }

    /// <summary>The latest state of each message the stream has, once it has <paramref name="count"/> of them.</summary>
    private static async Task<Dictionary<string, ChatMessage>> Collect(Engine engine, int count)
    {
        var latest = new Dictionary<string, ChatMessage>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var message in engine.WatchMessagesAsync(cts.Token))
            {
                latest[message.Id] = message;
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested) { }
        Assert.True(latest.Count >= count, $"{latest.Count} messages streamed");
        return latest;
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
