// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Updates;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Updates in the app (docs/features/F4.15-updates.md, acceptance tests 5, 6).</summary>
public sealed class UpdatesUiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly UpdateInfo Newer = new(
        "0.5.0-alpha.1",
        DateTimeOffset.Parse("2026-10-20T12:00:00Z", null),
        new Uri("https://github.com/APRS-NET/AprsMap/releases/tag/v0.5.0-alpha.1"),
        string.Join('\n', Enumerable.Range(1, 50).Select(i => $"- change {i}")),
        "ghcr.io/aprs-net/aprsmap:0.5.0-alpha.1",
        []
    );

    private static (UpdatesViewModel Updates, FakeEngine Engine, List<Uri> Opened) Create(bool gateway = false)
    {
        var engine = new FakeEngine { UpdateStatus = new("0.4.0", UpdateChannel.PreReleases, Update: Newer) };
        var opened = new List<Uri>();
        var updates = new UpdatesViewModel(
            engine,
            Localizer.Instance,
            uri =>
            {
                opened.Add(uri);
                return Task.CompletedTask;
            }
        )
        {
            Gateway = gateway,
        };
        return (updates, engine, opened);
    }

    [Fact]
    public async Task AnUpdateShowsAQuietNoticeWhoseDialogDownloadsFromTheReleasePage()
    {
        var (updates, _, opened) = Create();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var watching = updates.WatchAsync(cts.Token);
        await Task.Yield();

        Assert.Equal("Update available: 0.5.0-alpha.1", updates.NoticeText);
        updates.OpenCommand.Execute(null);
        var dialog = updates.Dialog!;
        Assert.StartsWith("0.5.0-alpha.1 · ", dialog.Heading, StringComparison.Ordinal);
        Assert.Equal(UpdatesViewModel.NotesLines, dialog.Notes.Split('\n').Length); // the first 40 lines
        Assert.True(dialog.MoreNotes);
        Assert.True(dialog.CanDownload);
        Assert.Null(dialog.PullText);

        await dialog.DownloadCommand.ExecuteAsync(null);
        Assert.Equal([Newer.ReleasePage], opened);
        Assert.Null(updates.Dialog);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching); // as the app's loop ends it
    }

    [Fact]
    public async Task LaterHidesItUntilTheNextStartAndSkipForGood()
    {
        var (updates, engine, _) = Create();
        await updates.CheckAsync(); // Check now opens the dialog when there's one
        Assert.Equal(1, engine.UpdateChecks);

        updates.Dialog!.LaterCommand.Execute(null);
        Assert.Null(updates.NoticeText);
        Assert.Null(updates.Dialog);

        var (again, engine2, _) = Create();
        await again.CheckAsync();
        await again.Dialog!.SkipCommand.ExecuteAsync(null);
        Assert.Equal(["0.5.0-alpha.1"], engine2.SkippedVersions);
        Assert.Null(again.NoticeText);
    }

    [Fact]
    public async Task AGatewaysOperatorIsToldWhichImageToPullAndNotOfferedADownload()
    {
        var (updates, _, _) = Create(gateway: true);
        await updates.CheckAsync();

        var dialog = updates.Dialog!;
        Assert.False(dialog.CanDownload);
        Assert.Contains("ghcr.io/aprs-net/aprsmap:0.5.0-alpha.1", dialog.PullText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutSaysWhereTheCheckStandsAndOffersCheckNow()
    {
        var engine = new FakeEngine
        {
            UpdateStatus = new("0.4.0", UpdateChannel.PreReleases, DateTimeOffset.Parse("2026-10-20T12:00:00Z", null)),
        };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        await vm.Updates.CheckAsync();

        await vm.OpenAboutAsync();

        var about = Assert.IsType<AprsMap.UI.Settings.AboutViewModel>(vm.Dialog!.Content);
        Assert.StartsWith("Updates: Pre-releases · checked ", about.UpdatesText, StringComparison.Ordinal);
        Assert.EndsWith("· up to date", about.UpdatesText, StringComparison.Ordinal);
        Assert.NotNull(about.CheckUpdatesCommand);
    }

    [Fact]
    public async Task OpeningTheUpdateShowsItInTheDialogOverlay()
    {
        var engine = new FakeEngine { UpdateStatus = new("0.4.0", UpdateChannel.PreReleases, Update: Newer) };
        var vm = new MainViewModel(engine, new FakeTimeProvider());

        await vm.Updates.CheckAsync();

        Assert.Equal("Update", vm.Dialog!.Title);
        Assert.IsType<UpdateDialogViewModel>(vm.Dialog.Content);
        vm.Dialog.CloseCommand.Execute(null);
        Assert.Null(vm.Dialog);
    }
}
