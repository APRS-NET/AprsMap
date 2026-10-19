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

    private static readonly EngineStatus TwoSources = new FakeEngine().Status with
    {
        Sources =
        [
            new SourceStatus("rotate", "APRS-IS", SourceKind.AprsIs, ConnectionState.Connected, MaySend: true),
            new SourceStatus("tnc", "Direwolf", SourceKind.KissTcp, ConnectionState.Connected, MaySend: true),
            new SourceStatus("rx", "Receiver", SourceKind.KissTcp, ConnectionState.Connected),
        ],
    };

    [Fact]
    public async Task NewBulletinPostsOnTheSourcesTickedAndClosesTheForm()
    {
        var engine = new FakeEngine();
        var panel = new BulletinsViewModel(engine, Localizer.Instance, new FakeTimeProvider(At), () => TwoSources);

        panel.NewBulletinCommand.Execute(null);
        Assert.True(panel.IsComposing);
        Assert.Equal(["APRS-IS", "Direwolf"], panel.PostVia.Select(v => v.Name)); // TX enabled ones, all ticked
        panel.PostKind = panel.PostKinds[1];
        Assert.Equal("A", panel.PostId);
        panel.PostId = "q";
        panel.PostGroup = "club";
        panel.PostText = "Meeting Saturday";
        Assert.Equal("16/67", panel.PostCount);
        await panel.PostCommand.ExecuteAsync(null);
        Assert.False(panel.IsComposing);
        Assert.Equal(
            new BulletinPost(BulletinKind.Announcement, "q", "Meeting Saturday", "club"),
            engine.PostedBulletins[^1] with
            {
                SourceIds = null,
            }
        );
        Assert.Null(engine.PostedBulletins[^1].SourceIds); // all ticked: every TX enabled source

        panel.NewBulletinCommand.Execute(null);
        panel.PostVia[0].IsChecked = false;
        panel.PostText = "Net tonight";
        await panel.PostCommand.ExecuteAsync(null);
        Assert.Equal(["tnc"], engine.PostedBulletins[^1].SourceIds!);
    }

    [Fact]
    public async Task ARefusedPostKeepsTheFormOpenWithTheReason()
    {
        var engine = new FakeEngine
        {
            PostFailure = EngineErrors.ToException(
                new EngineError(EngineErrorCodes.BulletinTextInvalid, "A bulletin has 1 to 67 plain letters.")
            ),
        };
        var panel = new BulletinsViewModel(engine, Localizer.Instance, new FakeTimeProvider(At), () => TwoSources);
        panel.NewBulletinCommand.Execute(null);

        await panel.PostCommand.ExecuteAsync(null);

        Assert.True(panel.IsComposing);
        Assert.StartsWith("A bulletin has 1 to 67", panel.PostError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OursSayWhenTheyGoNextAndCanBeEditedOrStopped()
    {
        var engine = new FakeEngine();
        var panel = new BulletinsViewModel(engine, Localizer.Instance, new FakeTimeProvider(At), () => TwoSources);
        var ours = B("N0CALL", BulletinKind.Bulletin, "BLN1", "Net tonight") with
        {
            Mine = true,
            NextSend = At.AddMinutes(4),
        };

        panel.Apply(ours);
        var row = Assert.Single(panel.Rows);
        Assert.True(row.CanStop);
        Assert.StartsWith("Yours · next send", row.MineText, StringComparison.Ordinal);

        row.EditCommand.Execute(null);
        Assert.True(panel.IsComposing);
        Assert.Equal(("1", "Net tonight"), (panel.PostId, panel.PostText));
        await row.StopCommand.ExecuteAsync(null);
        Assert.Equal(["BLN1"], engine.StoppedBulletins);

        panel.Apply(ours with { NextSend = null });
        Assert.False(panel.Rows.Single().CanStop);
        Assert.Equal("Yours · no longer repeated", panel.Rows.Single().MineText);
    }

    [Fact]
    public async Task GroupsAreJoinedPostedToAndLeft()
    {
        var engine = new FakeEngine();
        var panel = new BulletinsViewModel(engine, Localizer.Instance, new FakeTimeProvider(At), () => TwoSources);
        await panel.LoadAsync();
        Assert.False(panel.HasGroups);

        panel.JoinName = "nzevent";
        await panel.JoinCommand.ExecuteAsync(null);
        Assert.Equal(["NZEVENT"], panel.Groups.Select(g => g.Name));
        Assert.Empty(panel.JoinName);
        Assert.Equal("NZEVENT", panel.PostTo!.Name);

        panel.GroupText = "Net at 7pm";
        await panel.PostToGroupCommand.ExecuteAsync(null);
        Assert.Equal([("NZEVENT", "Net at 7pm")], engine.GroupPosts);
        Assert.Empty(panel.GroupText);

        await panel.Groups[0].LeaveCommand.ExecuteAsync(null);
        Assert.Empty(panel.Groups);
        Assert.False(panel.HasGroups);
    }
}
