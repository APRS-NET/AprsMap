// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>The away message in Settings › Messaging (docs/features/F2.10-auto-reply.md, behaviors 1, 2, 4–6).</summary>
public sealed class AutoReplyUiTests
{
    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public async Task MessagingShowsTheActiveProfilesAutoReplyAndSavesIt()
    {
        var engine = new FakeEngine();
        var section = new MessagingSectionViewModel(engine, English(), _ => { });
        section.Show(engine.Settings);
        Assert.False(section.AutoReplyOn); // off by default
        Assert.Equal(("15", "60", "30"), (section.AwayMinutes, section.OncePerMinutes, section.DelaySeconds));
        Assert.StartsWith("Auto-reply for ", section.AutoReplyHeading, StringComparison.Ordinal);

        section.AutoReplyOn = true;
        section.AutoReplyText = "  On the road, back at 6  ";
        section.AwayMinutes = "0";
        section.DelaySeconds = "45";
        await section.SaveCommand.ExecuteAsync(null);

        var reply = Assert.Single(engine.Saved).GetActiveProfile()!.EffectiveAutoReply;
        Assert.Equal(new AutoReplySettings(true, "On the road, back at 6", 0, 60, 45), reply);
        Assert.False(engine.Saved[0].GetActiveProfile()!.EffectivePing.On); // the ping settings kept as they were
        Assert.Equal("Saved", section.SavedText);
    }

    [Fact]
    public async Task ATimeThatIsNotANumberIsRefusedBeforeSaving()
    {
        var engine = new FakeEngine();
        var section = new MessagingSectionViewModel(engine, English(), _ => { });
        section.Show(engine.Settings);

        section.OncePerMinutes = "an hour";
        await section.SaveCommand.ExecuteAsync(null);

        Assert.Empty(engine.Saved);
        Assert.StartsWith("Auto-reply times:", section.Error, StringComparison.Ordinal);
    }
}
