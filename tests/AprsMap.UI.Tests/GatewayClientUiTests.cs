// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Settings;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The desktop app as a gateway's client (docs/features/F1.39-desktop-gateway-client.md, acceptance tests 1–3).</summary>
public sealed class GatewayClientUiTests
{
    private static readonly Uri Gateway = new("http://127.0.0.1:8081/");

    /// <summary>Stands in for the desktop's saved choice; Restart only counts.</summary>
    private sealed class FakeHost(GatewayClientSettings settings, Uri? connected, bool canRestart = true)
        : IGatewayClientHost
    {
        public GatewayClientSettings Settings { get; private set; } = settings;

        public Uri? Connected { get; } = connected;

        public int Restarts { get; private set; }

        public Task SaveAsync(GatewayClientSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }

        public bool Restart()
        {
            Restarts += canRestart ? 1 : 0;
            return canRestart;
        }
    }

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

    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public async Task ApplyingAGatewaySavesItWithTheKeyAndRestarts()
    {
        var host = new FakeHost(new GatewayClientSettings(), connected: null);
        var section = new ConnectionSectionViewModel(host, English());
        Assert.True(section.ThisComputer);
        Assert.Equal("http://127.0.0.1:8081/", section.Address);
        Assert.Equal("Now showing this computer's own station.", section.StatusText);
        Assert.False(section.ApplyCommand.CanExecute(null));

        section.UseGateway = true;
        section.ApiKey = " the-key ";
        await section.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(new GatewayClientSettings(Gateway, "the-key"), host.Settings);
        Assert.Equal(1, host.Restarts);
    }

    [Fact]
    public async Task UsingThisComputerAgainKeepsTheKeyForNextTime()
    {
        var host = new FakeHost(new GatewayClientSettings(Gateway, "the-key"), Gateway);
        var section = new ConnectionSectionViewModel(host, English());
        Assert.True(section.UseGateway);
        Assert.Equal("the-key", section.ApiKey);

        section.ThisComputer = true;
        await section.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(new GatewayClientSettings(null, "the-key"), host.Settings);
    }

    [Fact]
    public async Task AnAddressThatIsntOneIsRefusedAndAnAppThatCantRestartSaysSo()
    {
        var host = new FakeHost(new GatewayClientSettings(), connected: null, canRestart: false);
        var section = new ConnectionSectionViewModel(host, English());

        section.UseGateway = true;
        section.Address = "gateway.local";
        Assert.Equal("Enter the gateway's address, starting with http:// or https://.", section.AddressError);
        Assert.False(section.ApplyCommand.CanExecute(null));

        section.Address = "https://gateway.local/aprs/";
        await section.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(new Uri("https://gateway.local/aprs/"), host.Settings.Gateway);
        Assert.Equal("Saved. Restart the app to switch.", section.Message);
    }

    [Fact]
    public void TheStatusSaysWhoThisAppIsOnTheGateway()
    {
        var section = new ConnectionSectionViewModel(new FakeHost(new(Gateway, "k"), Gateway), English());

        section.ShowAuth(new AuthInfo(true, "API key", ApiKey: true));
        Assert.Equal(
            "Now showing the gateway at http://127.0.0.1:8081/, as its operator with the API key.",
            section.StatusText
        );
        section.ShowAuth(new AuthInfo(false, ApiKey: true));
        Assert.True(section.HasProblem);
        Assert.StartsWith("Now watching the gateway at http://127.0.0.1:8081/: it refused", section.StatusText);
        section.ShowAuth(new AuthInfo(false, ApiKey: true, NeedsHttps: true)); // plain HTTP from another computer
        Assert.True(section.HasProblem);
        Assert.Contains("accepts the API key only over https://", section.StatusText, StringComparison.Ordinal);
        Assert.Contains("ssh -L 8081:127.0.0.1:8081", section.StatusText, StringComparison.Ordinal);
        section.ShowAuth(AuthInfo.Viewer);
        Assert.False(section.HasProblem);
        Assert.StartsWith("Now watching the gateway at http://127.0.0.1:8081/. Add its API key", section.StatusText);
    }

    private static async Task<(MainViewModel Vm, FakeEngine Gateway, FakeEngine Own)> Client(AuthInfo auth)
    {
        var gateway = new FakeEngine { Settings = FakeEngine.TwoProfiles() };
        var own = new FakeEngine();
        var vm = new MainViewModel(gateway, new FakeTimeProvider())
        {
            SharedStation = true,
            OperatorSession = new Session(auth),
            GatewayClient = new FakeHost(new(Gateway, "k"), Gateway),
            UpdateEngine = own,
        };
        vm.Show(gateway.Settings);
        await vm.LoadAuthAsync();
        return (vm, gateway, own);
    }

    [Fact]
    public async Task WithTheKeyItIsTheOperatorWithNoSignOut()
    {
        var (vm, _, _) = await Client(new AuthInfo(true, "API key", ApiKey: true));

        Assert.True(vm.IsOperator);
        Assert.Equal("Operator, with the gateway's API key", vm.SignedInText);
        var ids = new AppMenus(vm, Localizer.Instance, new NoActions())
            .Menus.SelectMany(m => m.Items())
            .OfType<CommandEntry>()
            .Select(c => c.Id)
            .ToList();
        Assert.Contains("file.signed-in", ids);
        Assert.DoesNotContain("file.sign-out", ids);
        await vm.OpenSettingsAsync();
        Assert.Contains(vm.Settings!.Sections, s => s.Id == "connection");
        Assert.StartsWith("Now showing the gateway", vm.Settings.Connection!.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItsUpdateChecksAreItsOwnNotTheGateways()
    {
        var (vm, gateway, own) = await Client(AuthInfo.Viewer);

        await vm.Updates.CheckCommand.ExecuteAsync(null);
        await vm.OpenSettingsAsync();

        Assert.Equal((0, 1), (gateway.UpdateChecks, own.UpdateChecks));
        Assert.False(vm.Updates.Gateway);
        Assert.NotNull(vm.Settings!.Data!.UpdateChecks); // even watching only: they're this app's updates
    }
}
