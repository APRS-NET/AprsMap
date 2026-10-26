// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Settings;
using AprsMap.UI.Updates;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>
/// Update and restart, and Restart gateway, on the Linux service gateway
/// (docs/features/F1.40-gateway-service-updates.md, behaviors 2, 4, 5).
/// </summary>
public sealed class ServiceUpdateUiTests
{
    private static readonly UpdateInfo Newer = new(
        "0.9.0",
        DateTimeOffset.Parse("2026-10-27T12:00:00Z", null),
        new Uri("https://github.com/APRS-NET/AprsMap/releases/tag/v0.9.0"),
        "- a change",
        null,
        []
    );

    private sealed class Session(AuthInfo info) : IOperatorSession
    {
        public Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(info);

        public Task SignInAsync(SignInRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static (UpdatesViewModel Updates, FakeEngine Engine) Service(bool canInstall = true)
    {
        var engine = new FakeEngine
        {
            UpdateStatus = new("0.8.0", UpdateChannel.PreReleases, Update: Newer, CanInstall: canInstall),
        };
        return (new UpdatesViewModel(engine, Localizer.Instance, _ => Task.CompletedTask) { Gateway = true }, engine);
    }

    [Fact]
    public async Task TheServicesDialogOffersUpdateAndRestartAndTheNoticeFollowsTheUpdater()
    {
        var (updates, engine) = Service();
        await updates.RefreshAsync();
        updates.OpenCommand.Execute(null);
        var dialog = updates.Dialog!;
        Assert.True(dialog.CanInstall);
        Assert.False(dialog.CanDownload);
        Assert.Null(dialog.PullText); // no docker pull for the service

        await dialog.InstallCommand.ExecuteAsync(null);

        Assert.Equal(["0.9.0"], engine.InstalledVersions);
        Assert.Equal("Updating the gateway to 0.9.0: starting…", updates.NoticeText);
        Assert.False(updates.CanInstall); // one at a time (behavior 7)
        Assert.False(updates.CanClear);

        engine.UpdateStatus = engine.UpdateStatus with
        {
            Install = new ServiceInstall(
                "0.9.0",
                ServiceInstallStep.Failed,
                "the download doesn't match its checksum."
            ),
        };
        await updates.RefreshAsync();
        Assert.Equal(
            "The update to 0.9.0 failed: the download doesn't match its checksum. The gateway is still on 0.8.0.",
            updates.NoticeText
        );
        Assert.True(updates.CanClear);
        updates.ClearCommand.Execute(null);
        Assert.Equal("Update available: 0.9.0", updates.NoticeText); // the update's own notice again
    }

    [Fact]
    public async Task AContainerGatewaysDialogStillSaysHowToPull()
    {
        var (updates, _) = Service(canInstall: false);
        await updates.RefreshAsync();
        updates.OpenCommand.Execute(null);

        Assert.False(updates.Dialog!.CanInstall);
    }

    private static async Task<(MainViewModel Vm, FakeEngine Engine)> Gateway(AuthInfo auth, bool service)
    {
        var engine = new FakeEngine
        {
            Settings = FakeEngine.TwoProfiles(),
            UpdateStatus = new("0.8.0", UpdateChannel.PreReleases, CanInstall: service),
        };
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            SharedStation = true,
            OperatorSession = new Session(auth),
        };
        vm.Show(engine.Settings);
        await vm.LoadAuthAsync();
        await vm.Updates.RefreshAsync();
        return (vm, engine);
    }

    [Fact]
    public async Task TheServicesOperatorRestartsItFromAboutAfterAskingFirst()
    {
        var (vm, engine) = await Gateway(new AuthInfo(true, "Derek"), service: true);

        await vm.OpenAboutAsync();
        var about = Assert.IsType<AboutViewModel>(vm.Dialog!.Content);
        about.RestartGatewayCommand!.Execute(null);
        var confirm = Assert.IsType<ConfirmViewModel>(vm.Dialog!.Content);
        Assert.Equal(0, engine.GatewayRestarts);
        confirm.ConfirmCommand.Execute(null);

        Assert.Equal(1, engine.GatewayRestarts);
        Assert.Null(vm.Dialog);
    }

    [Theory]
    [InlineData(true, false)] // the operator of a container gateway
    [InlineData(false, true)] // a viewer of the service
    public async Task NoOneElseIsOfferedRestart(bool signedIn, bool service)
    {
        var (vm, _) = await Gateway(signedIn ? new AuthInfo(true, "Derek") : AuthInfo.Viewer, service);

        await vm.OpenAboutAsync();

        Assert.Null(Assert.IsType<AboutViewModel>(vm.Dialog!.Content).RestartGatewayCommand);
    }
}
