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
    public async Task AGeneratedKeyCanBeCopiedThenSavedAndIsntShownAgain()
    {
        var engine = new FakeEngine();
        var section = await Section(engine);
        var copied = new List<string>();
        section.CopyToClipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        Assert.Equal("No API key: apps can only watch.", section.StatusText);
        Assert.False(section.On);
        Assert.False(section.ApplyCommand.CanExecute(null));

        await section.GenerateCommand.ExecuteAsync(null);
        await section.CopyCommand.ExecuteAsync(null);
        var key = section.Key;
        Assert.True(section.On); // generating turns it on
        await section.ApplyCommand.ExecuteAsync(null);

        Assert.Equal([key], copied);
        Assert.Equal([key], engine.SavedApiKeys);
        Assert.Empty(section.Key);
        Assert.False(section.CopyCommand.CanExecute(null));
        Assert.False(section.ApplyCommand.CanExecute(null)); // nothing left to apply
        Assert.True(section.IsSet);
        Assert.StartsWith("An API key is set, since 10/26/2026 12:00", section.StatusText, StringComparison.Ordinal);
        Assert.False(section.HasProblem);
    }

    [Fact]
    public async Task AShortKeySaysWhyAndApplyingOffRemovesTheKey()
    {
        var engine = new FakeEngine();
        var section = await Section(engine);

        section.On = true;
        section.Key = "too-short";
        await section.ApplyCommand.ExecuteAsync(null);
        Assert.True(section.HasProblem);
        Assert.Equal("An API key needs at least 32 characters, without spaces.", section.Message);

        section.Key = new string('k', 40);
        await section.ApplyCommand.ExecuteAsync(null);
        Assert.Equal([new string('k', 40)], engine.SavedApiKeys);
        section.On = false;
        Assert.True(section.ApplyCommand.CanExecute(null));
        await section.ApplyCommand.ExecuteAsync(null);

        Assert.False(section.IsSet);
        Assert.Equal(ApiKeyStatus.None, engine.ApiKey);
        Assert.Equal("Removed. Apps can only watch.", section.Message);
    }

    [Fact]
    public async Task TestSaysWhetherAKeyIsTheCurrentOneAndSavesNothing()
    {
        // #447
        var engine = new FakeEngine
        {
            ApiKey = new ApiKeyStatus(true, DateTimeOffset.UnixEpoch),
            CurrentApiKey = new string('k', 43),
        };
        var section = await Section(engine);
        Assert.False(section.TestCommand.CanExecute(null)); // nothing to test yet

        section.Key = new string('k', 43);
        await section.TestCommand.ExecuteAsync(null);
        Assert.Equal(("That's the gateway's current key.", false), (section.Message, section.HasProblem));

        section.Key = new string('x', 43);
        await section.TestCommand.ExecuteAsync(null);
        Assert.Equal(
            ("That isn't the gateway's current key. Apps using it can only watch.", true),
            (section.Message, section.HasProblem)
        );
        Assert.Empty(engine.SavedApiKeys);
    }

    [Fact]
    public async Task ReplacingAKeyOtherAppsUseAsksOnceMore()
    {
        // #450: those apps are refused until they have the new key, so the first Apply only says so.
        var engine = new FakeEngine { ApiKey = new ApiKeyStatus(true, DateTimeOffset.UnixEpoch, InUse: 2) };
        var section = await Section(engine);
        Assert.EndsWith("2 other apps are connected with it now.", section.StatusText, StringComparison.Ordinal);

        await section.GenerateCommand.ExecuteAsync(null);
        await section.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(engine.SavedApiKeys);
        Assert.True(section.HasProblem);
        Assert.StartsWith(
            "2 other apps are connected with the current key.",
            section.Message,
            StringComparison.Ordinal
        );
        Assert.EndsWith("Press Apply again to replace it.", section.Message, StringComparison.Ordinal);

        section.Key = new string('k', 43); // a change asks again
        await section.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(engine.SavedApiKeys);
        await section.ApplyCommand.ExecuteAsync(null);
        Assert.Single(engine.SavedApiKeys);

        engine.ApiKey = new ApiKeyStatus(true, DateTimeOffset.UnixEpoch, InUse: 1);
        await section.ShowAsync();
        section.On = false;
        await section.ApplyCommand.ExecuteAsync(null);
        Assert.StartsWith(
            "One other app is connected with the current key.",
            section.Message,
            StringComparison.Ordinal
        );
        engine.ApiKey = new ApiKeyStatus(true, DateTimeOffset.UnixEpoch); // it went meanwhile: no need to ask
        section.On = true;
        section.On = false;
        await section.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("Removed. Apps can only watch.", section.Message);
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
