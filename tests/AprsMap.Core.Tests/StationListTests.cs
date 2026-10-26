// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Station lists (docs/features/F1.33-station-lists.md, acceptance tests 1–4).</summary>
public sealed class StationListTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private const string Roster = """
        # Skywarn spotters, net of 25 October
        KD0XYZ Sweep 2
        WA0RC*

        AID-3 first aid tent
        """;

    [Fact]
    public void ARosterWithHeadingsAndNotesIsReadAndABadLineIsPointedAt()
    {
        var bad = StationListText.Parse(Roster + "\nnot*valid as an entry");
        var error = Assert.Single(bad.Errors);
        Assert.Equal(6, error.Line);

        var read = StationListText.Parse(Roster);
        Assert.Empty(read.Errors);
        Assert.Equal(
            [new StationListEntry("KD0XYZ", "Sweep 2"), new("WA0RC*"), new("AID-3", "first aid tent")],
            read.Entries
        );
        Assert.Equal(Roster + "\nN0NEW-7 relief", StationListText.Append(Roster, "N0NEW-7", "relief"));
    }

    [Theory]
    [InlineData("KD0XYZ", "KD0XYZ-7", true)]
    [InlineData("KD0XYZ", "kd0xyz", true)]
    [InlineData("KD0XYZ", "KD0XYZA", false)]
    [InlineData("KD0XYZ-7", "KD0XYZ-9", false)]
    [InlineData("WA0RC*", "WA0RC-11", true)]
    [InlineData("WA0RC*", "WA0R", false)]
    [InlineData("AID-3", "AID-3", true)]
    [InlineData("AID-3", "AID-31", false)]
    public void EntriesMatchCallsignsEverySsidPrefixesAndNames(string pattern, string name, bool matches) =>
        Assert.Equal(matches, new StationListEntry(pattern).Matches(name));

    [Fact]
    public void OnlyTheListIsShownExceptAnEmergency()
    {
        var spotters = new StationList("spotters", "Spotters", Roster);
        var store = new StationStore
        {
            ListResolver = ids => ids.Contains("spotters") ? name => spotters.Entries.Any(e => e.Matches(name)) : null,
        };
        foreach (
            var line in new[]
            {
                "KD0XYZ-7>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W>",
                "WA0RC-11>APRS,TCPIP*,qAC,T2TEST:!4904.50N/07201.75W-",
                "N0CALL>APRS,TCPIP*,qAC,T2TEST:)AID-3!4905.50N/07201.75W+",
                "N0OTHER>APRS,TCPIP*,qAC,T2TEST:!4906.50N/07201.75W>",
                "N0SOS>APRS,TCPIP*,qAC,T2TEST:!4907.50N\\07201.75W!", // an emergency is always shown
            }
        )
        {
            store.Apply(AprsPacket.Parse(line), T0);
        }

        store.Subscribe(new StationFilter(Lists: ["spotters"]), out var shown, out _);

        Assert.Equal(
            ["AID-3", "KD0XYZ-7", "N0SOS", "WA0RC-11"],
            shown.Select(s => s.Name).Order(StringComparer.Ordinal)
        );
        store.Subscribe(new StationFilter(Lists: ["deleted"]), out var all, out _);
        Assert.Equal(5, all.Length); // a list that's gone doesn't hide everything
    }

    [Fact]
    public async Task AListShownJoinsTheAprsIsFilterAndLeavesWithIt()
    {
        var sources = new EngineTests.FakeSourceFactory();
        var session = sources.Enqueue("T2TEST");
        var store = InMemorySettingsStore.WithProfile();
        var engine = new Engine(
            Options.Create(new EngineOptions { FilterInterval = TimeSpan.Zero }),
            sources,
            store,
            new FakeTimeProvider(T0),
            NullLogger<Engine>.Instance
        );
        var settings = await engine.GetSettingsAsync(Ct);
        await engine.SaveSettingsAsync(
            settings with
            {
                Profiles =
                [
                    settings.Profiles[0] with
                    {
                        Latitude = 40,
                        Longitude = -105,
                        StationLists = [new StationList("spotters", "Spotters", Roster)],
                    },
                ],
            },
            Ct
        );
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);

        using var watching = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var watch = Task.Run(
            async () =>
            {
                await foreach (
                    var _ in engine.WatchStationsAsync(new StationFilter(Lists: ["spotters"]), watching.Token)
                ) { }
            },
            Ct
        );
        await WaitUntil(() => session.Filters.Any(f => f.Contains("b/KD0XYZ*/WA0RC*", StringComparison.Ordinal)));
        Assert.Contains("o/AID-3", session.Filters.Last(), StringComparison.Ordinal);

        await watching.CancelAsync();
        await watch.ContinueWith(_ => { }, TaskScheduler.Default);
        await WaitUntil(() => !session.Filters.Last().Contains("b/", StringComparison.Ordinal));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AListWithABadLineOrARepeatedNameIsRefused()
    {
        var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            new FakeTimeProvider(T0),
            NullLogger<Engine>.Instance
        );
        var settings = await engine.GetSettingsAsync(Ct);
        AppSettings With(params StationList[] lists) =>
            settings with
            {
                Profiles = [settings.Profiles[0] with { StationLists = lists }],
            };

        var bad = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(With(new StationList("a", "Spotters", "KD0XYZ\nnot*a*callsign")), Ct)
        );
        Assert.Equal(
            (EngineErrorCodes.StationListEntryInvalid, "2"),
            (bad.EngineError.Code, bad.EngineError.Arg(EngineErrorArgs.Line))
        );
        var twice = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(With(new("a", "Spotters", "KD0XYZ"), new("b", "spotters", "N0CALL")), Ct)
        );
        Assert.Equal(EngineErrorCodes.StationListNameTaken, twice.EngineError.Code);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(20, Ct);
        }
    }
}
