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
