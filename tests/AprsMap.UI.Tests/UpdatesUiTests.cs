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

    [Fact]
    public async Task AFailedCheckWarnsTriesAgainWhenClickedAndCanBeCleared()
    {
        var (updates, engine, _) = Create();
        var failedAt = DateTimeOffset.Parse("2026-10-22T09:00:00Z", null);
        engine.UpdateStatus = new(
            "0.4.0",
            UpdateChannel.PreReleases,
            failedAt,
            new EngineError(EngineErrorCodes.UpdatesUnreachable, "No route to host")
        );
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var watching = updates.WatchAsync(cts.Token);
        await Task.Yield();

        Assert.True(updates.IsWarning);
        Assert.Equal("Couldn't check for updates", updates.NoticeText);
        Assert.Equal("The release list couldn't be reached. Click to try again.", updates.NoticeTip);
        Assert.True(updates.CanClear);

        await updates.OpenCommand.ExecuteAsync(null); // tries again
        Assert.Equal(1, engine.UpdateChecks);
        Assert.Null(updates.Dialog);

        updates.ClearCommand.Execute(null);
        Assert.Null(updates.NoticeText);

        await engine.CheckForUpdateAsync(Ct);
        engine.UpdateStatus = engine.UpdateStatus with { LastChecked = failedAt.AddHours(2) }; // the next one fails too
        await updates.RefreshAsync();
        Assert.True(updates.IsWarning); // a new failure shows again

        engine.UpdateStatus = engine.UpdateStatus with { LastError = null, Update = Newer };
        await updates.RefreshAsync();
        Assert.False(updates.IsWarning);
        Assert.Equal("Update available: 0.5.0-alpha.1", updates.NoticeText);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
    }

    [Fact]
    public async Task AnUpdatesNoticeCanBeClearedUntilTheNextStart()
    {
        var (updates, _, _) = Create();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var watching = updates.WatchAsync(cts.Token);
        await Task.Yield();

        Assert.True(updates.CanClear);
        updates.ClearCommand.Execute(null);

        Assert.Null(updates.NoticeText);
        Assert.False(updates.CanClear);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
    }

    [Fact]
    public async Task SettingsTurnChecksOffChooseTheChannelAndOfferASkippedVersionAgain()
    {
        var (updates, engine, _) = Create();
        engine.Settings = engine.Settings with { Updates = new UpdateSettings(SkippedVersion: "0.5.0-alpha.1") };
        var settings = new UpdateSettingsViewModel(engine, Localizer.Instance, updates, TimeZoneInfo.Utc, _ => { });
        settings.Show(engine.Settings);

        Assert.True(settings.CheckOn);
        Assert.Equal("Skipped: 0.5.0-alpha.1", settings.SkippedText);
        Assert.Null(settings.Channel.Value); // the build's own

        settings.CheckOn = false;
        await WaitUntil(() => !engine.Settings.EffectiveUpdates.Check);

        settings.Channel = settings.ChannelOptions.Single(o => o.Value == UpdateChannel.Stable);
        await WaitUntil(() => engine.Settings.EffectiveUpdates.Channel == UpdateChannel.Stable);

        settings.CheckOn = true;
        await WaitUntil(() => engine.Settings.EffectiveUpdates.Check);
        var checks = engine.UpdateChecks;
        await settings.OfferAgainCommand.ExecuteAsync(null);
        Assert.Null(engine.Settings.EffectiveUpdates.SkippedVersion);
        Assert.Null(settings.SkippedText);
        Assert.Equal(checks + 1, engine.UpdateChecks); // checked again, so it's offered if it's still the newest
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        Assert.True(condition());
    }
}
