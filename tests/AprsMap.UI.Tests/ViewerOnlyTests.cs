// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>A public page's viewer and its title (docs/features/F1.26-public-console.md, behaviors 9, 10).</summary>
public sealed class ViewerOnlyTests
{
    private sealed class Session(AuthInfo info) : IOperatorSession
    {
        public Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(info);

        public Task SignInAsync(SignInRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoActions : IMenuActions
    {
        public bool CanQuit => false;

        public bool CanFullScreen => false;

        public bool IsFullScreen => false;

        public void Quit() { }

        public void ToggleFullScreen() { }

        public Task SavePacketsAsync(AprsMap.UI.Packets.Export.PacketExporter exporter) => Task.CompletedTask;

        public Task OpenUriAsync(Uri uri) => Task.CompletedTask;
    }

    private static async Task<(MainViewModel Vm, List<string> Titles)> Browser(AuthInfo auth)
    {
        var engine = new FakeEngine { Settings = FakeEngine.TwoProfiles() with { ReadOnly = true } };
        var titles = new List<string>();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            SharedStation = true,
            OperatorSession = new Session(auth),
            SetPageTitle = titles.Add,
        };
        vm.Show(engine.Settings);
        await vm.LoadAuthAsync();
        return (vm, titles);
    }

    private static List<string> Ids(MainViewModel vm) =>
        [
            .. new AppMenus(vm, Localizer.Instance, new NoActions())
                .Menus.SelectMany(m => Flatten(m.Items()))
                .Select(c => c.Id),
        ];

    private static IEnumerable<CommandEntry> Flatten(IEnumerable<MenuEntry> entries) =>
        entries.SelectMany(e =>
            e switch
            {
                CommandEntry c => [c],
                SubmenuEntry s => Flatten(s.Items),
                _ => Enumerable.Empty<CommandEntry>(),
            }
        );

    [Fact]
    public async Task AViewerWhoCanNeverActSeesNoStationControls()
    {
        var (vm, _) = await Browser(AuthInfo.Viewer); // the public port: no sign-in offered
        Assert.True(vm.IsViewerOnly);
        Assert.False(vm.AprsIsSwitch.IsShown);

        var ids = Ids(vm);
        Assert.DoesNotContain(ids, id => id.StartsWith("station.internet", StringComparison.Ordinal));
        Assert.DoesNotContain("station.transmit", ids);
        Assert.DoesNotContain("tools.clear-data", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("file.profile.", StringComparison.Ordinal));
        Assert.Contains("tools.find", ids); // what's their own stays
        Assert.Contains("view.reset-layout", ids);
        Assert.Contains("station.destination.set", ids);

        await vm.OpenSettingsAsync();
        Assert.DoesNotContain(
            vm.Settings!.Sections,
            s => s.Id is "profiles" or "sources" or "beacons" or "units" or "data"
        );
        Assert.Contains(vm.Settings.Sections, s => s.Id == "appearance");
    }

    [Fact]
    public async Task AViewerWhoCouldSignInSeesThemGreyedWithTheReason()
    {
        var (vm, _) = await Browser(new AuthInfo(false, SignInOffered: true)); // the operator port
        Assert.False(vm.IsViewerOnly);
        Assert.True(vm.AprsIsSwitch.IsShown);
        Assert.Contains("station.transmit", Ids(vm));
    }

    [Fact]
    public async Task TheGatewaysTitleShowsBesideTheNameAndOnTheTab()
    {
        var (vm, titles) = await Browser(AuthInfo.Viewer);

        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0", Title: "What N0GATE hears"));

        Assert.Equal("What N0GATE hears", vm.StationTitle);
        Assert.Equal([$"What N0GATE hears · {Brand.DisplayName}"], titles);
    }
}
