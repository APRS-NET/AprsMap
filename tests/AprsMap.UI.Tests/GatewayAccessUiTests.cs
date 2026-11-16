// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Settings › Gateway access (docs/features/F1.38-gateway-api-key.md, behaviors 1, 6).</summary>
public sealed class GatewayAccessUiTests
{
    private sealed class Session(AuthInfo info) : IOperatorSession
    {
        public Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(info);

        public Task SignInAsync(SignInRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    private static async Task<GatewayAccessSectionViewModel> Section(FakeEngine engine)
    {
        var section = new GatewayAccessSectionViewModel(engine, English(), TimeZoneInfo.Utc);
        await section.ShowAsync();
        return section;
    }

    [Fact]
    public async Task AddingAnAppShowsItsKeyOnceToCopyAndListsIt()
    {
        var engine = new FakeEngine();
        var section = await Section(engine);
        var copied = new List<string>();
        section.CopyToClipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        Assert.Equal("No app has a key: apps can only watch.", section.StatusText);
        Assert.False(section.CreateCommand.CanExecute(null)); // it needs a name

        section.NewName = "Derek's laptop";
        await section.CreateCommand.ExecuteAsync(null);
        await section.CopyCommand.ExecuteAsync(null);

        var key = engine.ApiKeyValues["id1"];
        Assert.Equal([key], copied);
        Assert.Equal(key, section.CreatedKey);
        Assert.Equal("Key for Derek's laptop: copy it into the app now. It won't be shown again.", section.CreatedText);
        Assert.Empty(section.NewName);
        var row = Assert.Single(section.Keys);
        Assert.Equal(("Derek's laptop", "Operator · not used yet"), (row.Name, row.Details));
        Assert.Equal("One app has a key.", section.StatusText);

        section.DoneCommand.Execute(null);
        Assert.False(section.HasCreated);
        Assert.Null(section.CreatedKey); // gone for good
    }

    [Fact]
    public async Task AWatchOnlyKeyAndAUsedKeySayWhatTheyAre()
    {
        var engine = new FakeEngine();
        engine.ApiKeysList.Add(
            new ApiKeyInfo(
                "a",
                "Shack PC",
                ApiKeyAccess.Operator,
                DateTimeOffset.UnixEpoch,
                new DateTimeOffset(2026, 11, 15, 15, 40, 0, TimeSpan.Zero),
                "10.10.20.130",
                InUse: 1
            )
        );
        var section = await Section(engine);
        section.NewName = "Wall display";
        section.NewAccess = section.AccessOptions.Single(o => o.Value == ApiKeyAccess.Watch);

        await section.CreateCommand.ExecuteAsync(null);

        var time = new DateTime(2026, 11, 15, 15, 40, 0).ToString("g", CultureInfo.GetCultureInfo("en-US")); // 11/15/2026 3:40 PM
        Assert.Equal(
            [$"Operator · last used {time} from 10.10.20.130 · connected", "Watch only · not used yet"],
            section.Keys.Select(k => k.Details)
        );
        Assert.Equal("2 apps have keys.", section.StatusText);
    }

    [Fact]
    public async Task ANameAlreadyUsedSaysSo()
    {
        var engine = new FakeEngine();
        var section = await Section(engine);
        section.NewName = "Phone";
        await section.CreateCommand.ExecuteAsync(null);
        section.DoneCommand.Execute(null);

        section.NewName = "phone";
        await section.CreateCommand.ExecuteAsync(null);

        Assert.True(section.HasProblem);
        Assert.StartsWith("Give the key a name", section.Message, StringComparison.Ordinal);
        Assert.Single(engine.ApiKeysList);
    }

    [Fact]
    public async Task RevokingOneKeyLeavesTheOthersAndAsksFirstWhenItsAppIsConnected()
    {
        var engine = new FakeEngine();
        engine.ApiKeysList.Add(
            new ApiKeyInfo("a", "Laptop", ApiKeyAccess.Operator, DateTimeOffset.UnixEpoch, InUse: 1)
        );
        engine.ApiKeysList.Add(new ApiKeyInfo("b", "Phone", ApiKeyAccess.Operator, DateTimeOffset.UnixEpoch));
        var section = await Section(engine);

        await section.Keys[1].RevokeCommand.ExecuteAsync(null); // not connected: straight away
        Assert.Equal("Revoked the key for Phone. The other apps are unaffected.", section.Message);
        Assert.Equal(["Laptop"], section.Keys.Select(k => k.Name));

        await section.Keys[0].RevokeCommand.ExecuteAsync(null);
        Assert.Single(engine.ApiKeysList); // asked first
        Assert.True(section.HasProblem);
        Assert.StartsWith("Laptop is connected now.", section.Message, StringComparison.Ordinal);
        await section.Keys[0].RevokeCommand.ExecuteAsync(null);
        Assert.Empty(engine.ApiKeysList);
        Assert.Equal("No app has a key: apps can only watch.", section.StatusText);
    }

    [Fact]
    public async Task TestSaysWhetherAKeyIsOneOfTheGatewaysAndSavesNothing()
    {
        // #447
        var engine = new FakeEngine();
        var section = await Section(engine);
        section.NewName = "Laptop";
        await section.CreateCommand.ExecuteAsync(null);
        Assert.False(section.TestCommand.CanExecute(null)); // nothing to test yet

        section.TestKey = " " + engine.ApiKeyValues["id1"] + " ";
        await section.TestCommand.ExecuteAsync(null);
        Assert.Equal(("That's one of the gateway's keys.", false), (section.Message, section.HasProblem));

        section.TestKey = new string('x', 43);
        await section.TestCommand.ExecuteAsync(null);
        Assert.Equal(
            ("That isn't one of the gateway's keys. Apps using it can only watch.", true),
            (section.Message, section.HasProblem)
        );
        Assert.Single(engine.ApiKeysList);
    }

    [Fact]
    public async Task OnlyASignedInGatewayOperatorHasTheSection()
    {
        static async Task<bool> Offered(bool shared, AuthInfo? auth)
        {
            var engine = new FakeEngine { Settings = FakeEngine.TwoProfiles() };
            var vm = new MainViewModel(engine, new FakeTimeProvider())
            {
                SharedStation = shared,
                OperatorSession = auth is null ? null : new Session(auth),
            };
            vm.Show(engine.Settings);
            await vm.LoadAuthAsync();
            await vm.OpenSettingsAsync();
            return vm.Settings!.Sections.Any(s => s.Id == "gateway-access");
        }

        Assert.True(await Offered(true, new AuthInfo(true, "Derek")));
        Assert.False(await Offered(true, new AuthInfo(false, SignInOffered: true)));
        Assert.False(await Offered(false, null)); // the desktop's own engine has no key
    }
}
