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

        var ex = await Assert.ThrowsAsync<EngineAccessException>(() =>
            remote.SaveSettingsAsync(settings with { Units = UnitSettings.Imperial }, Ct)
        );

        Assert.Equal(EngineErrorCodes.SettingsReadOnly, ex.EngineError.Code);
        Assert.Equal(EngineHub.SettingsReadOnlyMessage, ex.Message);
    }

    [Fact]
    public async Task BrowsersCannotUseAddressLookupOrThePasscodeCalculator()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        Assert.Equal(GeocodingInfo.Disabled, await remote.GetGeocodingInfoAsync(Ct));
        var lookup = await Assert.ThrowsAsync<EngineAccessException>(() => remote.GeocodeAsync("Boulder", "en", Ct));
        var passcode = await Assert.ThrowsAsync<EngineAccessException>(() => remote.ComputePasscodeAsync("N0CALL", Ct));

        Assert.Equal(EngineErrorCodes.OperatorOnly, lookup.EngineError.Code);
        Assert.Equal(EngineErrorCodes.OperatorOnly, passcode.EngineError.Code);
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

        var missing = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            remote.SaveSettingsAsync(settings with { ActiveProfileId = "nobody" }, Ct)
        );
        var badCall = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            remote.SaveSettingsAsync(
                settings with
                {
                    Profiles = [settings.Profiles[0] with { Callsign = "N0 GATE" }],
                },
                Ct
            )
        );

        Assert.Equal(EngineErrorCodes.ActiveProfileMissing, missing.EngineError.Code);
        Assert.Equal("The active profile must be one of the profiles.", missing.Message);
        Assert.Equal("aprs.callsign-invalid-character", badCall.EngineError.Code);
        Assert.Equal(settings.Profiles[0].Name, badCall.EngineError.Arg(EngineErrorArgs.Profile));
        Assert.Equal("N0 GATE", badCall.EngineError.Arg(EngineErrorArgs.Value));
        Assert.Equal("2", badCall.EngineError.Arg(EngineErrorArgs.Index));
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

        var ex = await Assert.ThrowsAsync<EngineOperationException>(() =>
            remote.GeocodeAsync(FakeGeocoder.FailingQuery, null, Ct)
        );
        Assert.Equal(EngineErrorCodes.GeocodingRefused, ex.EngineError.Code);
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
        var ex = await Assert.ThrowsAsync<EngineArgumentException>(() => remote.ComputePasscodeAsync("N0 CALL", Ct));
        Assert.Equal("aprs.callsign-invalid-character", ex.EngineError.Code);
        Assert.Equal(" ", ex.EngineError.Arg(EngineErrorArgs.Character));
        Assert.DoesNotContain("HubException", ex.Message, StringComparison.Ordinal);
    }
}

public class EngineErrorTests
{
    private static readonly EngineError Duplicate = new(
        EngineErrorCodes.ProfileNameDuplicate,
        "There is more than one profile named 'Home'.",
        new Dictionary<string, string> { [EngineErrorArgs.Profile] = "Home" }
    );

    [Fact]
    public void AnErrorSurvivesTheHubEvenBehindSignalRsOwnText()
    {
        var message =
            "An unexpected error occurred invoking 'SaveSettings' on the server. HubException: "
            + EngineHub.EncodeError(Duplicate);

        Assert.True(EngineHub.TryDecodeError(message, out var decoded));
        Assert.Equal(Duplicate.Code, decoded.Code);
        Assert.Equal(Duplicate.Message, decoded.Message);
        Assert.Equal("Home", decoded.Arg(EngineErrorArgs.Profile));
        Assert.Null(decoded.Arg(EngineErrorArgs.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Plain server error")]
    [InlineData("engine-error:{ not json")]
    [InlineData("engine-error:null")]
    [InlineData("engine-error:{\"code\":\"x\"}")]
    public void MessagesWithoutAnErrorAreLeftAlone(string? message) =>
        Assert.False(EngineHub.TryDecodeError(message, out _));

    [Theory]
    [InlineData(EngineErrorCodes.SettingsReadOnly, typeof(EngineAccessException))]
    [InlineData(EngineErrorCodes.OperatorOnly, typeof(EngineAccessException))]
    [InlineData(EngineErrorCodes.GeocodingUnreachable, typeof(EngineOperationException))]
    [InlineData(EngineErrorCodes.ProfileNameDuplicate, typeof(EngineArgumentException))]
    [InlineData("aprs.callsign-invalid-length", typeof(EngineArgumentException))]
    [InlineData("future.something-new", typeof(EngineArgumentException))]
    public void TheCodeDecidesTheExceptionType(string code, Type expected)
    {
        var inner = new InvalidOperationException("inner");
        var exception = EngineErrors.Create(code, "English", innerException: inner);

        Assert.IsType(expected, exception);
        Assert.Equal("English", exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Equal(code, ((IHasEngineError)exception).EngineError.Code);
    }

    [Fact]
    public void ExceptionsNeedAnError()
    {
        Assert.Throws<ArgumentNullException>(() => new EngineArgumentException(null!));
        Assert.Throws<ArgumentNullException>(() => new EngineOperationException(null!));
        Assert.Throws<ArgumentNullException>(() => new EngineAccessException(null!));
        Assert.Throws<ArgumentNullException>(() => EngineErrors.ToException(null!));
        Assert.Throws<ArgumentNullException>(() => EngineErrorCodes.KindOf(null!));
    }
}
