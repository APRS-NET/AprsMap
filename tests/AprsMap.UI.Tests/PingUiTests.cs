// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Messages;
using AprsMap.UI.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The ping responder in the UI (docs/features/F2.13-ping.md, behaviors 1, 3, 6, 7, 11).</summary>
public sealed class PingUiTests
{
    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public async Task MessagingShowsTheActiveProfilesPingAndSavesIt()
    {
        var engine = new FakeEngine();
        var section = new MessagingSectionViewModel(engine, English(), _ => { });
        section.Show(engine.Settings);
        Assert.False(section.On); // off by default
        Assert.True(section.WhoAnyone);
        Assert.Equal("60", section.IntervalSeconds);
        Assert.Equal(Brand.DisplayName + " ping", MessagingSectionViewModel.DefaultPing);

        section.On = true;
        section.WhoAnyone = false;
        section.WhoCallsigns = true;
        section.Callsigns = "wa0rc*\nK0K";
        section.IntervalSeconds = "30";
        await section.SaveCommand.ExecuteAsync(null);

        var ping = Assert.Single(engine.Saved).GetActiveProfile()!.EffectivePing;
        Assert.Equal((true, PingWho.Callsigns, 30), (ping.On, ping.Who, ping.IntervalSeconds));
        Assert.Equal(["WA0RC*", "K0K"], ping.CallsignList);
        Assert.Null(ping.Ping); // the default phrase, from the brand
        Assert.Equal("Saved", section.SavedText);
    }

    [Theory]
    [InlineData(PingResult.Answered, "Ping, answered")]
    [InlineData(PingResult.TooSoon, "Ping, not answered: too soon after the last pong")]
    [InlineData(PingResult.Off, "Ping, not answered: Answer pings is off")]
    public void APingSaysWhatCameOfItAndAPongIsAnAutomaticReply(PingResult result, string said)
    {
        var messages = new MessagesViewModel(new FakeEngine(), English(), new FakeTimeProvider(), () => null);
        var at = DateTimeOffset.Parse("2026-10-19T03:12:45Z", CultureInfo.InvariantCulture);

        messages.Apply(
            new ChatMessage(
                "a",
                "NZ0P-9",
                false,
                "AprsMap ping",
                at,
                ChatMessageState.Received,
                Read: true,
                Ping: result
            )
        );
        messages.Apply(
            new ChatMessage(
                "b",
                "NZ0P-9",
                true,
                "AprsMap pong 2026-10-19 03:12:45Z",
                at,
                ChatMessageState.Delivered,
                Automatic: true
            )
        );
        messages.Selected = messages.Conversations.Single();

        Assert.EndsWith(said, messages.Thread[0].StateText, StringComparison.Ordinal);
        Assert.EndsWith("Automatic reply · Delivered", messages.Thread[1].StateText, StringComparison.Ordinal);
        Assert.Equal(0, messages.UnreadTotal);
    }

    [Fact]
    public async Task AnsweringQueriesIsOnByDefaultAndSavedWithTheSection()
    {
        var engine = new FakeEngine();
        var section = new MessagingSectionViewModel(engine, English(), _ => { });
        section.Show(engine.Settings);
        Assert.True(section.AnswerQueries); // docs/features/F2.8-queries.md, decision 1

        section.AnswerQueries = false;
        await section.SaveCommand.ExecuteAsync(null);

        var profile = Assert.Single(engine.Saved).GetActiveProfile()!;
        Assert.False(profile.EffectiveAnswerQueries);
    }
}
