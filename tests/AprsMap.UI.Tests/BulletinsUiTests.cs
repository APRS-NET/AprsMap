// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Bulletins;
using AprsMap.UI.Localization;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The Bulletins panel (docs/features/F2.5-bulletins.md, behaviors 1–4).</summary>
public sealed class BulletinsUiTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-18T12:00:00Z", null);

    private static BulletinView B(
        string from,
        BulletinKind kind,
        string addressee,
        string text,
        string? group = null,
        int minutes = 0
    ) =>
        new(
            $"{from}|{kind}|{addressee}",
            kind,
            from,
            addressee,
            addressee[3..4],
            group,
            text,
            At,
            At.AddMinutes(minutes),
            "rotate"
        );

    private static BulletinsViewModel Create() => new(new FakeEngine(), Localizer.Instance, new FakeTimeProvider(At));

    [Fact]
    public void BulletinsShowNewestFirstEachVersionOnceAndGoWhenRemoved()
    {
        var panel = Create();
        Assert.Equal(
            "No bulletins heard yet. Bulletins and announcements stations send show here, the newest version of each, until they're not heard for a day.",
            panel.Note
        );

        panel.Apply(B("N0BUL", BulletinKind.Bulletin, "BLN3", "Net tonight at 8pm", minutes: 1));
        panel.Apply(B("N0ANN", BulletinKind.Announcement, "BLNQ", "Club meeting Saturday", minutes: 2));
        panel.Apply(
            B("N0BUL", BulletinKind.Bulletin, "BLN3", "Net tonight at 9pm", minutes: 3) with
            {
                Updated = true,
            }
        );

        Assert.Equal(["Net tonight at 9pm", "Club meeting Saturday"], panel.Rows.Select(r => r.Text));
        Assert.StartsWith("Updated", panel.Rows[0].Heard, StringComparison.Ordinal);
        Assert.Equal(2, panel.NewCount);

        panel.Apply(B("N0BUL", BulletinKind.Bulletin, "BLN3", "Net tonight at 9pm") with { Removed = true });
        Assert.Equal(["Club meeting Saturday"], panel.Rows.Select(r => r.Text));
    }

    [Fact]
    public void SeeingThePanelClearsTheNewMarksAndOnlyLaterOnesAreNew()
    {
        var panel = Create();
        panel.Apply(B("N0BUL", BulletinKind.Bulletin, "BLN3", "Net tonight"));
        panel.MarkSeen();
        Assert.Equal(0, panel.NewCount);
        Assert.False(panel.Rows[0].IsNew);

        panel.Apply(B("N0ANN", BulletinKind.Announcement, "BLNQ", "Meeting", minutes: 1));
        Assert.Equal(1, panel.NewCount);
        Assert.True(panel.Rows[0].IsNew);
    }

    [Fact]
    public async Task KindsGroupsAndTextFilterAndAGroupCanBeMuted()
    {
        var panel = Create();
        panel.Apply(B("N0BUL", BulletinKind.Bulletin, "BLN3", "Net tonight"));
        panel.Apply(B("N0WX", BulletinKind.Bulletin, "BLN4WX", "Frost tonight", group: "WX", minutes: 1));
        panel.Apply(B("N0NWS", BulletinKind.Nws, "NWS-WARN", "Severe thunderstorm warning", minutes: 2));

        Assert.Equal(["Everything", "Bulletin", "Announcement", "NWS", "WX"], panel.Filters.Select(f => f.Name));
        panel.SelectedFilter = panel.Filters.Single(f => f.Name == "NWS");
        Assert.Equal(["N0NWS"], panel.Rows.Select(r => r.From));
        panel.SelectedFilter = panel.Filters.Single(f => f.Name == "WX");
        Assert.Equal(["N0WX"], panel.Rows.Select(r => r.From));
        panel.SelectedFilter = panel.Filters[0];
        panel.Search = "frost";
        Assert.Equal(["N0WX"], panel.Rows.Select(r => r.From));
        panel.Search = "";

        await panel.Rows.Single(r => r.From == "N0WX").MuteGroupCommand.ExecuteAsync(null);
        Assert.DoesNotContain(panel.Rows, r => r.From == "N0WX");
        Assert.Equal("Muted: WX", panel.Note);
        await panel.UnmuteAllCommand.ExecuteAsync(null);
        Assert.Contains(panel.Rows, r => r.From == "N0WX");
    }
}
