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

        /// <summary>What the next test finds: a gateway like this app that takes the key, unless set.</summary>
        public ConnectionTestOutcome Outcome { get; set; } =
            new(new GatewayTest(Brand.Slug, "0.8.0-alpha.2", EngineHub.ContractVersion, ApiKeyCheck.Accepted));

        public List<(Uri Gateway, string? Key)> Tests { get; } = [];

        public Task<ConnectionTestOutcome> TestAsync(
            Uri gateway,
            string? apiKey,
            CancellationToken cancellationToken = default
        )
        {
            Tests.Add((gateway, apiKey));
            return Task.FromResult(Outcome);
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
        Assert.False(section.ApplyCommand.CanExecute(null)); // not before a test (#419)
        await section.TestCommand.ExecuteAsync(null);
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
        await section.TestCommand.ExecuteAsync(null);
        await section.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(new Uri("https://gateway.local/aprs/"), host.Settings.Gateway);
        Assert.Equal("Saved. Restart the app to switch.", section.Message);
    }

    private static async Task<ConnectionSectionViewModel> TestedAsync(ConnectionTestOutcome outcome, string key = "k")
    {
        var host = new FakeHost(new GatewayClientSettings(), connected: null) { Outcome = outcome };
        var section = new ConnectionSectionViewModel(host, English(), appVersion: "0.8.0-alpha.5+abc");
        section.UseGateway = true;
        section.ApiKey = key;
        await section.TestCommand.ExecuteAsync(null);
        return section;
    }

    private static ConnectionTestOutcome Answer(
        ApiKeyCheck key,
        string version = "0.8.0-alpha.2",
        int? contract = null
    ) => new(new GatewayTest(Brand.Slug, version, contract ?? EngineHub.ContractVersion, key));

    [Fact]
    public async Task ATestThatPassesAllowsApplyUntilTheFieldsChange()
    {
        var section = await TestedAsync(Answer(ApiKeyCheck.Accepted));

        Assert.Equal(
            "Reached the gateway (0.8.0-alpha.2) with the API key: this app will be its operator.",
            section.TestText
        );
        Assert.False(section.TestFailed || section.TestWarned);
        Assert.True(section.ApplyCommand.CanExecute(null));

        section.ApiKey = "another";
        Assert.Null(section.TestText);
        Assert.False(section.ApplyCommand.CanExecute(null)); // test again
    }

    [Theory]
    [InlineData(ApiKeyCheck.Refused, "refused the API key")]
    [InlineData(ApiKeyCheck.NeedsHttps, "ssh -L 8081:127.0.0.1:8081")]
    [InlineData(ApiKeyCheck.NotAdminPort, "public address, which doesn't take API keys")]
    public async Task AKeyTheGatewayWontTakeStopsApplyAndSaysWhy(ApiKeyCheck key, string why)
    {
        var section = await TestedAsync(Answer(key));

        Assert.True(section.TestFailed);
        Assert.Contains(why, section.TestText, StringComparison.Ordinal);
        Assert.False(section.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task AComputerWaitingAfterWrongKeysIsToldHowLong()
    {
        // #450
        var section = await TestedAsync(
            new(new GatewayTest(Brand.Slug, "0.8.0-alpha.2", EngineHub.ContractVersion, ApiKeyCheck.Waiting, 12))
        );

        Assert.True(section.TestFailed);
        Assert.Contains(
            "isn't checking keys from this computer for 12 min",
            section.TestText,
            StringComparison.Ordinal
        );
        Assert.False(section.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithoutAKeyItMayApplyToWatch()
    {
        var section = await TestedAsync(Answer(ApiKeyCheck.None), key: "");

        Assert.EndsWith("this app will only watch.", section.TestText, StringComparison.Ordinal);
        Assert.True(section.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnotherContractStopsApplyAndAnotherMinorVersionOnlyWarns()
    {
        var incompatible = await TestedAsync(Answer(ApiKeyCheck.Accepted, "0.9.0", EngineHub.ContractVersion + 1));
        Assert.True(incompatible.TestFailed);
        Assert.Equal(
            "The gateway runs 0.9.0, which this app (0.8.0-alpha.5) can't work with. Update whichever of the two is older.",
            incompatible.TestText
        );
        Assert.False(incompatible.ApplyCommand.CanExecute(null));

        var older = await TestedAsync(Answer(ApiKeyCheck.Accepted, "0.7.3"));
        Assert.True(older.TestWarned);
        Assert.EndsWith(
            "Its version differs from this app's (0.8.0-alpha.5), so some features may be missing on one side.",
            older.TestText
        );
        Assert.True(older.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task NothingAnsweringOrSomethingElseAnsweringStopsApply()
    {
        var down = await TestedAsync(new ConnectionTestOutcome(null, Unreachable: true, "Connection refused"));
        Assert.StartsWith("Nothing answered at http://127.0.0.1:8081/: Connection refused", down.TestText);
        Assert.False(down.ApplyCommand.CanExecute(null));

        var other = await TestedAsync(new ConnectionTestOutcome(null));
        Assert.Contains("doesn't answer as a gateway", other.TestText, StringComparison.Ordinal);
        var stranger = await TestedAsync(
            new(new GatewayTest("other", "1.0.0", EngineHub.ContractVersion, ApiKeyCheck.None))
        );
        Assert.True(stranger.TestFailed);
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
        section.ShowAuth(new AuthInfo(false, ApiKey: true, WaitMinutes: 12)); // after too many wrong keys (#450)
        Assert.True(section.HasProblem);
        Assert.Contains(
            "isn't checking keys from this computer for 12 min",
            section.StatusText,
            StringComparison.Ordinal
        );
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

    [Theory]
    [InlineData(true, false, false, "127.0.0.1:8081 · operator", false)]
    [InlineData(false, false, false, "Viewing 127.0.0.1:8081", false)]
    [InlineData(false, true, false, "Viewing 127.0.0.1:8081", true)]
    [InlineData(false, true, true, "Viewing 127.0.0.1:8081", true)]
    public async Task TheStatusBarSaysWhichGatewayAndAsWhom(
        bool op,
        bool keySent,
        bool needsHttps,
        string text,
        bool warn
    )
    {
        // #428
        var (vm, _, _) = await Client(
            new AuthInfo(op, op ? "API key" : null, ApiKey: keySent || op, NeedsHttps: needsHttps)
        );

        Assert.Equal(text, vm.GatewayClientText);
        Assert.Equal(warn, vm.GatewayClientWarn);
        if (needsHttps)
        {
            Assert.StartsWith(
                "The gateway takes the API key only over https://",
                vm.GatewayClientTip,
                StringComparison.Ordinal
            );
        }
        await vm.OpenConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.Settings!.IsConnectionSection);
    }

    [Fact]
    public async Task TheStatusBarSaysHowLongAWaitingComputerWaits()
    {
        // #450
        var (vm, _, _) = await Client(new AuthInfo(false, ApiKey: true, WaitMinutes: 12));

        Assert.True(vm.GatewayClientWarn);
        Assert.StartsWith(
            "The gateway isn't checking keys from this computer for 12 min",
            vm.GatewayClientTip,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ItsOwnStationHasNoGatewayOnTheStatusBar()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider())
        {
            GatewayClient = new FakeHost(new GatewayClientSettings(), connected: null),
        };
        await vm.LoadAuthAsync();

        Assert.Null(vm.GatewayClientText);
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
