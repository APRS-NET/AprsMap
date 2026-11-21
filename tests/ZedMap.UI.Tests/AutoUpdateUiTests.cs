// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.Extensions.Time.Testing;
using ZedMap.Contracts;
using ZedMap.UI.Localization;
using ZedMap.UI.Platform;
using ZedMap.UI.Updates;
using ZedMap.UI.ViewModels;

namespace ZedMap.UI.Tests;

/// <summary>
/// Install updates automatically on desktop, Restart automatically and What's new
/// (docs/features/F4.15-updates.md, behaviors 11a–11c).
/// </summary>
public sealed class AutoUpdateUiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T01:00:00Z", null);

    private static readonly UpdateInfo Newer = new(
        "0.9.0",
        DateTimeOffset.Parse("2026-10-20T12:00:00Z", null),
        new Uri("https://github.com/APRS-NET/zedMap/releases/tag/v0.9.0"),
        "- a change",
        null,
        [new("releases.linux-x64.json", new Uri("https://example.org/v0.9.0/releases.linux-x64.json"), 100)]
    );

    private sealed class FakeInstaller : IAppInstaller
    {
        public TaskCompletionSource Download { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Downloads { get; } = [];
        public bool AppliesOnExit { get; private set; }
        public bool Restarted { get; private set; }

        public bool CanInstall => true;

        public async Task DownloadAsync(
            UpdateInfo update,
            IProgress<double> progress,
            CancellationToken cancellationToken
        )
        {
            Downloads.Add(update.Version);
            await Download.Task;
        }

        public void ApplyOnExit() => AppliesOnExit = true;

        public void RestartNow() => Restarted = true;
    }

    private static (UpdatesViewModel Updates, FakeEngine Engine, FakeInstaller Installer, FakeTimeProvider Time) Create(
        DateTimeOffset? autoInstallAt,
        bool client = false
    )
    {
        var engine = new FakeEngine
        {
            UpdateStatus = new("0.8.0", UpdateChannel.PreReleases, Update: Newer, AutoInstallAt: autoInstallAt),
        };
        var installer = new FakeInstaller();
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var updates = new UpdatesViewModel(engine, Localizer.Instance, _ => Task.CompletedTask, installer, time)
        {
            Client = client,
        };
        return (updates, engine, installer, time);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task ADueUpdateIsDownloadedByItselfAndGoesInWhenTheAppCloses()
    {
        var (updates, _, installer, _) = Create(autoInstallAt: Now.AddHours(-1));

        await updates.RefreshAsync();
        Assert.Null(updates.Dialog); // nothing asked
        Assert.Equal(["0.9.0"], installer.Downloads);
        installer.Download.SetResult();
        await WaitUntil(() => updates.IsReady);

        Assert.True(installer.AppliesOnExit);
        Assert.False(installer.Restarted); // never by itself unless chosen (behavior 11b)
        Assert.Null(updates.RestartAt);
        Assert.Equal("Restart to finish updating to 0.9.0", updates.NoticeText);
    }

    [Fact]
    public async Task WithTheSwitchOffNothingIsDownloaded()
    {
        var (updates, _, installer, _) = Create(autoInstallAt: null);

        await updates.RefreshAsync();

        Assert.Empty(installer.Downloads);
        Assert.Equal("Update available: 0.9.0", updates.NoticeText);
    }

    [Fact]
    public async Task AnUpdateNotDueYetIsDownloadedOnceItIs()
    {
        var (updates, _, installer, time) = Create(autoInstallAt: Now.AddMinutes(25));

        await updates.RefreshAsync();
        Assert.Empty(installer.Downloads);

        time.Advance(TimeSpan.FromMinutes(20));
        Assert.Empty(installer.Downloads);
        time.Advance(TimeSpan.FromMinutes(10));
        await WaitUntil(() => installer.Downloads.Count == 1);
    }

    [Fact]
    public async Task AGatewaysClientNeverDownloadsAVersionNewerThanTheGateway()
    {
        var (updates, _, installer, _) = Create(autoInstallAt: Now.AddHours(-1), client: true);

        await updates.RefreshAsync();
        Assert.Empty(installer.Downloads); // the gateway's version isn't known yet

        updates.GatewayVersion = "0.8.5+e86ba03";
        Assert.Empty(installer.Downloads);

        updates.GatewayVersion = "0.9.0+1a2b3c4"; // the gateway updated: the app follows
        await WaitUntil(() => installer.Downloads.Count == 1);
    }

    [Fact]
    public async Task WithRestartAutomaticallyTheAppRestartsAtTheChosenTime()
    {
        var (updates, engine, installer, time) = Create(autoInstallAt: Now.AddHours(-1));
        engine.Settings = engine.Settings with
        {
            Updates = engine.Settings.EffectiveUpdates with { AutoRestart = true, AutoRestartTime = "03:00" },
        };

        await updates.RefreshAsync();
        installer.Download.SetResult();
        await WaitUntil(() => updates.RestartAt is not null);

        Assert.Equal(DateTimeOffset.Parse("2026-10-22T03:00:00Z", null), updates.RestartAt);
        Assert.Equal(
            $"Update to 0.9.0 ready: restarts at {new DateTime(2026, 10, 22, 3, 0, 0).ToString("t", Localizer.Instance.Culture)}",
            updates.NoticeText
        );
        time.Advance(TimeSpan.FromMinutes(119));
        Assert.False(installer.Restarted);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(installer.Restarted);
    }

    [Fact]
    public async Task AnInstalledBuildsSettingsOfferAutomaticInstallsAndTheRestartTime()
    {
        var (updates, engine, _, _) = Create(autoInstallAt: null);
        await updates.RefreshAsync();
        var settings = new UpdateSettingsViewModel(
            engine,
            Localizer.Instance,
            updates,
            TimeZoneInfo.Utc,
            _ => { },
            installs: true
        );
        settings.Show(engine.Settings);
        Assert.True(settings.ShowsAutoInstall);
        Assert.True(settings.ShowsAutoRestart);
        Assert.StartsWith("The app downloads", settings.AutoInstallDescription, StringComparison.Ordinal);
        Assert.False(settings.AutoRestartOn); // off by default
        Assert.Equal(TimeSpan.FromHours(3), settings.RestartTime);

        settings.AutoInstallOn = true;
        await WaitUntil(() => engine.Settings.EffectiveUpdates.AutoInstall);
        settings.Show(engine.Settings);
        settings.AutoRestartOn = true;
        await WaitUntil(() => engine.Settings.EffectiveUpdates.AutoRestart);
        settings.Show(engine.Settings);
        settings.RestartTime = new TimeSpan(4, 30, 0);
        await WaitUntil(() => engine.Settings.EffectiveUpdates.AutoRestartTime == "04:30");
    }

    [Fact]
    public async Task AfterAnUpdateWhatsNewShowsOnceAndOkDismissesIt()
    {
        var (updates, engine, _, _) = Create(autoInstallAt: null);
        var older = Newer with { Version = "0.8.5", Notes = "- an earlier change" };
        engine.UpdateStatus = engine.UpdateStatus with { Update = null, WhatsNew = [Newer, older] };

        await updates.RefreshAsync();

        var news = Assert.IsType<WhatsNewViewModel>(updates.WhatsNew);
        Assert.Equal("Updated to 0.9.0", news.Heading);
        Assert.Equal(2, news.Releases.Count);
        Assert.StartsWith("0.8.5 · ", news.Releases[1].Heading, StringComparison.Ordinal);

        await news.OkCommand.ExecuteAsync(null);

        Assert.Null(updates.WhatsNew);
        Assert.Equal(1, engine.WhatsNewDismissed);
    }

    [Fact]
    public async Task WhatsNewShowsInTheDialogOverlayAndClosingItDismissesIt()
    {
        var engine = new FakeEngine { UpdateStatus = new("0.9.0", UpdateChannel.PreReleases, WhatsNew: [Newer]) };
        var vm = new MainViewModel(engine, new FakeTimeProvider(Now));
        vm.Show(engine.Settings);

        await vm.Updates.RefreshAsync();

        Assert.IsType<WhatsNewViewModel>(vm.Dialog?.Content);
        vm.Dialog!.CloseCommand.Execute(null);
        await WaitUntil(() => engine.WhatsNewDismissed == 1);
        Assert.Null(vm.Dialog);
    }
}
