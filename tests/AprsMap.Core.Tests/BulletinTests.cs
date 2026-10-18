// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Bulletins heard (docs/features/F2.5-bulletins.md, behaviors 1–3; acceptance test 1).</summary>
public sealed class BulletinTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BulletinsAnnouncementsGroupsAndNwsAreKeptNewestVersionAndGoWhenNotHeard()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T12:00:00Z", null));
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            time,
            NullLogger<Engine>.Instance
        );
        engine.Accept("N0BUL>APRS::BLN3     :Net tonight at 8pm");
        engine.Accept("N0ANN>APRS::BLNQ     :Club meeting Saturday");
        engine.Accept("N0WX>APRS::BLN4WX   :Frost tonight");
        engine.Accept("N0NWS>APRS::NWS-WARN :Severe thunderstorm warning{AB12");
        engine.Accept("N0OK>APRS:!4903.50N/07201.75W>not a bulletin");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seen = new List<BulletinView>();
        var watch = Task.Run(
            async () =>
            {
                await foreach (var b in engine.WatchBulletinsAsync(cts.Token))
                {
                    lock (seen)
                    {
                        seen.Add(b);
                    }
                }
            },
            CancellationToken.None
        );
        await WaitUntil(() => Count(seen) == 4);
        List<BulletinView> now;
        lock (seen)
        {
            now = [.. seen];
        }
        Assert.Equal(
            [BulletinKind.Bulletin, BulletinKind.Bulletin, BulletinKind.Announcement, BulletinKind.Nws],
            now.Select(b => b.Kind).Order()
        );
        Assert.Contains(now, b => b is { From: "N0WX", Group: "WX", Id: "4", Text: "Frost tonight" });

        time.Advance(TimeSpan.FromMinutes(5));
        engine.Accept("N0BUL>APRS::BLN3     :Net tonight at 9pm instead");
        await WaitUntil(() => Last(seen) is { Updated: true });
        Assert.Equal("Net tonight at 9pm instead", Last(seen)!.Text);

        time.Advance(TimeSpan.FromDays(1)); // the board's expiry: not heard for a day
        await WaitUntil(() => Count(seen, removed: true) == 4);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
    }

    private static int Count(List<BulletinView> seen, bool removed = false)
    {
        lock (seen)
        {
            return seen.Count(b => b.Removed == removed);
        }
    }

    private static BulletinView? Last(List<BulletinView> seen)
    {
        lock (seen)
        {
            return seen.LastOrDefault();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
