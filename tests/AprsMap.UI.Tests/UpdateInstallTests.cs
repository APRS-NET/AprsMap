// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Updates;

namespace AprsMap.UI.Tests;

/// <summary>Installing an update in an installed build (docs/features/F4.15-updates.md, behavior 11).</summary>
public sealed class UpdateInstallTests
{
    private static readonly UpdateInfo Newer = new(
        "0.5.0-alpha.1",
        DateTimeOffset.Parse("2026-10-20T12:00:00Z", null),
        new Uri("https://github.com/APRS-NET/AprsMap/releases/tag/v0.5.0-alpha.1"),
        "- a change",
        null,
        [new("releases.linux-x64.json", new Uri("https://example.org/v0.5.0-alpha.1/releases.linux-x64.json"), 100)]
    );

    private sealed class FakeInstaller : IAppInstaller
    {
        public TaskCompletionSource Download { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<double>? Progress { get; private set; }
        public UpdateInfo? Downloaded { get; private set; }
        public bool AppliesOnExit { get; private set; }
        public bool Restarted { get; private set; }

        public bool CanInstall => true;

        public async Task DownloadAsync(
            UpdateInfo update,
            IProgress<double> progress,
            CancellationToken cancellationToken
        )
        {
            Progress = progress;
            using (cancellationToken.Register(() => Download.TrySetCanceled(cancellationToken)))
            {
                await Download.Task;
            }
            Downloaded = update;
        }

        public void ApplyOnExit() => AppliesOnExit = true;

        public void RestartNow() => Restarted = true;
    }

    private static async Task<(UpdatesViewModel Updates, FakeInstaller Installer)> CreateAsync(bool gateway = false)
    {
        var engine = new FakeEngine { UpdateStatus = new("0.4.0", UpdateChannel.PreReleases, Update: Newer) };
        var installer = new FakeInstaller();
        var updates = new UpdatesViewModel(engine, Localizer.Instance, _ => Task.CompletedTask, installer)
        {
            Gateway = gateway,
        };
        await updates.CheckAsync();
        return (updates, installer);
    }

    [Fact]
    public async Task AnInstalledBuildOffersUpdateAndRestartInsteadOfDownload()
    {
        var (updates, _) = await CreateAsync();

        Assert.True(updates.Dialog!.CanInstall);
        Assert.False(updates.Dialog.CanDownload);
    }

    [Fact]
    public async Task ItDownloadsWithProgressInTheNoticeThenGoesInWhenTheAppCloses()
    {
        var (updates, installer) = await CreateAsync();

        var installing = updates.Dialog!.InstallCommand.ExecuteAsync(null);
        Assert.Null(updates.Dialog); // the progress is in the notice
        Assert.True(updates.IsDownloading);
        installer.Progress!.Report(0.42);
        Assert.Equal(
            $"Downloading 0.5.0-alpha.1: {0.42.ToString("P0", Localizer.Instance.Culture)}",
            updates.NoticeText
        );

        installer.Download.SetResult();
        await installing;

        Assert.Equal(Newer, installer.Downloaded);
        Assert.True(updates.IsReady);
        Assert.True(installer.AppliesOnExit); // never interrupts the session by itself
        Assert.False(installer.Restarted);
        Assert.Equal("Restart to finish updating to 0.5.0-alpha.1", updates.NoticeText);
    }

    [Fact]
    public async Task RestartNowRestartsOnTheDownloadedUpdate()
    {
        var (updates, installer) = await CreateAsync();
        var installing = updates.Dialog!.InstallCommand.ExecuteAsync(null);
        installer.Download.SetResult();
        await installing;

        updates.OpenCommand.Execute(null); // the notice reopens the dialog on what's downloaded
        updates.Dialog!.RestartNowCommand.Execute(null);

        Assert.True(installer.Restarted);
        Assert.Null(updates.Dialog);
    }

    [Fact]
    public async Task CancelStopsTheDownloadAndTheNoticeGoesBackToTheUpdate()
    {
        var (updates, installer) = await CreateAsync();
        var installing = updates.Dialog!.InstallCommand.ExecuteAsync(null);

        updates.OpenCommand.Execute(null);
        Assert.True(updates.Dialog!.Owner.IsDownloading);
        updates.Dialog.CancelCommand.Execute(null);
        await installing;

        Assert.Equal(InstallState.None, updates.Install);
        Assert.False(installer.AppliesOnExit);
        Assert.Equal("Update available: 0.5.0-alpha.1", updates.NoticeText);
    }

    [Fact]
    public async Task AFailedDownloadSaysSoAndOffersDownloadToo()
    {
        var (updates, installer) = await CreateAsync();
        var installing = updates.Dialog!.InstallCommand.ExecuteAsync(null);
        installer.Download.SetException(new IOException("the hash didn't match"));
        await installing;

        Assert.True(updates.Failed);
        Assert.False(installer.AppliesOnExit);
        updates.OpenCommand.Execute(null);
        Assert.True(updates.Dialog!.CanInstall); // try again
        Assert.True(updates.Dialog.CanDownload); // or the release page
    }

    [Fact]
    public async Task AGatewayNeverInstalls()
    {
        var (updates, _) = await CreateAsync(gateway: true);

        Assert.False(updates.CanInstall);
        Assert.False(updates.Dialog!.CanInstall);
    }
}
