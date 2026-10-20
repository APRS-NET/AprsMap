// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>When message history is written (docs/features/F2.4-messaging.md, behavior 5).</summary>
public sealed class MessageSaveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Counts saves, keeping the last.</summary>
    private sealed class CountingStore : IMessageStore
    {
        private int _saves;

        public int Saves => Volatile.Read(ref _saves);

        public MessageArchive? Last { get; private set; }

        public Task<MessageArchive?> LoadAsync(string profileId, CancellationToken cancellationToken) =>
            Task.FromResult<MessageArchive?>(null);

        public Task SaveAsync(string profileId, MessageArchive archive, CancellationToken cancellationToken)
        {
            Last = archive;
            Interlocked.Increment(ref _saves);
            return Task.CompletedTask;
        }
    }

    private static (Engine Engine, CountingStore Store, FakeTimeProvider Time) Create()
    {
        var home = InMemorySettingsStore.Profile("NZ0P-10") with { Licensed = true };
        var store = new CountingStore();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T12:00:00Z", null));
        var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            new InMemorySettingsStore(new AppSettings([home], home.Id, UnitSettings.Metric)),
            time,
            NullLogger<Engine>.Instance,
            messages: store
        );
        return (engine, store, time);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task ABurstOfChangesIsOneSaveAMomentLater()
    {
        var (engine, store, time) = Create();
        using (engine)
        {
            await engine.GetSettingsAsync(Ct);

            await engine.SendMessageAsync("NZ0P-9", "one", Ct);
            await engine.SendMessageAsync("NZ0P-9", "two", Ct);
            await engine.SendMessageAsync("NZ0P-9", "three", Ct);
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            Assert.Equal(0, store.Saves); // waiting to group them

            time.Advance(Messenger.SaveDelay);
            await WaitUntil(() => store.Saves == 1);

            Assert.Equal(3, store.Last!.Messages.Count);
        }
    }

    [Fact]
    public async Task WhatsWaitingIsSavedWhenTheEngineStops()
    {
        var (engine, store, _) = Create();
        using (engine)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var run = engine.RunAsync(cts.Token);
            await engine.GetSettingsAsync(Ct);
            await engine.SendMessageAsync("NZ0P-9", "just before closing", Ct);
            Assert.Equal(0, store.Saves);

            await cts.CancelAsync();
            await run;

            Assert.Contains(store.Last!.Messages, m => m.Text == "just before closing");
        }
    }
}
