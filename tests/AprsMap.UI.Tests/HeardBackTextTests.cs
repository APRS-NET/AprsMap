// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;

namespace AprsMap.UI.Tests;

/// <summary>How my last position got out, in words (docs/features/F3.3-rf-paths.md, behaviors 2, 4).</summary>
public sealed class HeardBackTextTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-18T14:05:00Z", null);
    private static readonly Localizer Text = new(CultureInfo.GetCultureInfo("en-US"));

    private static string Clock(DateTimeOffset at) => at.ToString("HH:mm", CultureInfo.InvariantCulture);

    [Fact]
    public void EachWayItGotOutIsSaid()
    {
        var last = new LastPositionHeard(
            At,
            ["rotate.aprs2.net", "Direwolf"],
            [
                new HeardBack(HeardBackKind.Direct, "rotate.aprs2.net", At),
                new HeardBack(HeardBackKind.Repeated, "K0HQ-1", At.AddSeconds(4)),
                new HeardBack(HeardBackKind.Gated, "N0IGT", At.AddSeconds(6)),
            ],
            Following: true
        )
        {
            SentOnRf = true,
        };

        Assert.Equal(
            "Sent 14:05 on rotate.aprs2.net, Direwolf. Sent to APRS-IS directly, repeated by K0HQ-1 (RF), gated by N0IGT (to APRS-IS).",
            HeardBackText.Describe(last, Text, Clock)
        );
        Assert.Equal(
            "Last position heard back via K0HQ-1, 3 min ago",
            HeardBackText.Tip(last, At.AddMinutes(3).AddSeconds(4), Text)
        );
    }

    [Fact]
    public void SentOnRfButNotHeardBackSaysSoOnceNoLongerListening()
    {
        var last = new LastPositionHeard(At, ["Direwolf"], [], Following: true) { SentOnRf = true };
        Assert.Equal("Sent 14:05 on Direwolf. Listening for copies on RF.", HeardBackText.Describe(last, Text, Clock));

        Assert.Equal(
            "Sent 14:05 on Direwolf. Not heard back on RF.",
            HeardBackText.Describe(last with { Following = false }, Text, Clock)
        );
        Assert.Null(HeardBackText.Tip(last, At, Text));
    }
}
