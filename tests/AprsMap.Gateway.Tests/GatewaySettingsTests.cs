// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    [Fact]
    public async Task BrowsersCannotUseAddressLookupOrThePasscodeCalculator()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        Assert.Equal(GeocodingInfo.Disabled, await remote.GetGeocodingInfoAsync(Ct));
        var lookup = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            remote.GeocodeAsync("Boulder", "en", Ct)
        );
        var passcode = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            remote.ComputePasscodeAsync("N0CALL", Ct)
        );

        Assert.Equal(EngineHub.OperatorOnlyMessage, lookup.Message);
        Assert.Equal(EngineHub.OperatorOnlyMessage, passcode.Message);
        Assert.Empty(factory.Geocoder.Lookups);
    }

    [Fact]
    public void TheGeocodingSectionIsBound() =>
        Assert.Equal(
            "https://geo.example/",
            factory.Services.GetRequiredService<IOptions<GeocodingOptions>>().Value.Endpoint
        );
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

    [Fact]
    public async Task OnATrustedNetworkBrowsersCanLookUpAddresses()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        Assert.Equal(factory.Geocoder.Info, await remote.GetGeocodingInfoAsync(Ct));
        var result = Assert.Single(await remote.GeocodeAsync("Boulder", "en-US", Ct));
        Assert.Equal(new GeocodeResult("Boulder, Colorado", 40.015, -105.27), result);
        Assert.Contains(("Boulder", "en-US"), factory.Geocoder.Lookups);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            remote.GeocodeAsync(FakeGeocoder.FailingQuery, null, Ct)
        );
        Assert.Equal(FakeGeocoder.FailureMessage, ex.Message);
    }

    [Fact]
    public async Task OnATrustedNetworkBrowsersCanUseThePasscodeCalculator()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        Assert.Equal(
            AprsNet.AprsIsTcpClient.AprsIsPasscode.Compute("N0CALL"),
            await remote.ComputePasscodeAsync("n0call-9", Ct)
        );
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => remote.ComputePasscodeAsync("NOT A CALL", Ct));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain("HubException", ex.Message, StringComparison.Ordinal);
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
