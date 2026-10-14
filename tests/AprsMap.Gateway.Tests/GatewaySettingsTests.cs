// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace AprsMap.Gateway.Tests;

public sealed class GatewaySettingsTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BrowsersSeeReadOnlySettingsWithoutPasscodes()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        var settings = await remote.GetSettingsAsync(Ct);

        Assert.True(settings.ReadOnly);
        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(GatewayFactory.Callsign, profile.Callsign);
        Assert.Equal(-1, profile.Passcode);
    }

    [Fact]
    public async Task BrowsersCannotChangeSettings()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);
        var settings = await remote.GetSettingsAsync(Ct);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            remote.SaveSettingsAsync(settings with { Units = UnitSettings.Imperial }, Ct)
        );

        Assert.Equal(EngineHub.SettingsReadOnlyMessage, ex.Message);
    }
}

public sealed class TrustedGatewaySettingsTests(TrustedGatewayFactory factory) : IClassFixture<TrustedGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnATrustedNetworkBrowsersCanSwitchProfiles()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);
        var settings = await remote.GetSettingsAsync(Ct);
        Assert.False(settings.ReadOnly);
        Assert.Equal(GatewayFactory.Passcode, Assert.Single(settings.Profiles).Passcode);

        var portable = settings.Profiles[0] with { Id = "portable", Name = "Portable", Callsign = "N0GATE-7" };
        await remote.SaveSettingsAsync(
            settings with
            {
                Profiles = [.. settings.Profiles, portable],
                ActiveProfileId = portable.Id,
            },
            Ct
        );

        Assert.Equal("portable", (await remote.GetSettingsAsync(Ct)).ActiveProfileId);
        Assert.Equal("N0GATE-7", (await remote.GetStatusAsync(Ct)).Callsign);
    }

    [Fact]
    public async Task InvalidSettingsComeBackWithTheEnginesReason()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);
        var settings = await remote.GetSettingsAsync(Ct);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            remote.SaveSettingsAsync(settings with { ActiveProfileId = "nobody" }, Ct)
        );

        Assert.StartsWith("The active profile must be one of the profiles.", ex.Message, StringComparison.Ordinal);
    }
}

public class RemoteErrorMessageTests
{
    [Theory]
    [InlineData(
        "An unexpected error occurred invoking 'SaveSettings' on the server. HubException: Bad callsign.",
        "Bad callsign."
    )]
    [InlineData("Plain message", "Plain message")]
    public void TheServersMessageIsUnwrapped(string message, string expected) =>
        Assert.Equal(expected, RemoteEngineService.ServerMessage(new HubException(message)));
}
