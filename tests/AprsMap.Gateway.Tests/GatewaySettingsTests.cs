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
    public async Task BrowsersCannotListTheGatewaysSerialPorts()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        var ex = await Assert.ThrowsAsync<EngineAccessException>(() => remote.GetSerialPortsAsync(Ct));

        Assert.Equal(EngineErrorCodes.OperatorOnly, ex.EngineError.Code); // F3.1 behavior 12
    }

    [Fact]
    public async Task BrowsersCannotWatchASourcesTrafficOrTypeToItsTnc()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);
        var source = (await remote.GetSettingsAsync(Ct)).SourceList[0].Id;

        var watch = await Assert.ThrowsAsync<EngineAccessException>(async () =>
        {
            await foreach (var _ in remote.WatchSourceTrafficAsync(source, Ct)) { }
        });
        var manual = await Assert.ThrowsAsync<EngineAccessException>(() => remote.SetTncManualAsync(source, true, Ct));
        var line = await Assert.ThrowsAsync<EngineAccessException>(() =>
            remote.SendTncCommandAsync(source, new TncCommand(TncCommandKind.Line, "ID"), Ct)
        );
        var leave = await Assert.ThrowsAsync<EngineAccessException>(() =>
            remote.SendTncCommandAsync(source, new TncCommand(TncCommandKind.LeaveKiss), Ct)
        );

        // F1.28 behavior 11, F3.2 behavior 12.
        Assert.All(
            new[] { watch, manual, line, leave },
            e => Assert.Equal(EngineErrorCodes.OperatorOnly, e.EngineError.Code)
        );
    }

    [Fact]
    public async Task BrowsersCannotSeeExportOrClearCapturedPackets()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        var read = await Assert.ThrowsAsync<EngineAccessException>(() =>
            remote.GetCaptureAsync(new CaptureQuery(), Ct)
        );
        var export = await Assert.ThrowsAsync<EngineAccessException>(() =>
            remote.ExportCaptureAsync(new CaptureExport(), Ct)
        );
        var clear = await Assert.ThrowsAsync<EngineAccessException>(() => remote.ClearCaptureAsync(Ct));
        var history = await Assert.ThrowsAsync<EngineAccessException>(() => remote.GetHistoryStatusAsync(Ct));

        // F1.27 behavior 11; the history's status is the operator's too (F1.41 behavior 3).
        Assert.All(
            new[] { read, export, clear, history },
            e => Assert.Equal(EngineErrorCodes.OperatorOnly, e.EngineError.Code)
        );
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
    public async Task BrowsersCannotResetTheStation()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        var ex = await Assert.ThrowsAsync<EngineAccessException>(() => remote.ResetSettingsAsync(Ct));

        Assert.Equal(EngineErrorCodes.SettingsReadOnly, ex.EngineError.Code);
        Assert.NotEmpty((await remote.GetSettingsAsync(Ct)).Profiles);
    }

    [Fact]
    public async Task BrowsersCannotReconnectTheStation()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        var ex = await Assert.ThrowsAsync<EngineAccessException>(() => remote.ReconnectAsync(Ct));

        Assert.Equal(EngineErrorCodes.OperatorOnly, ex.EngineError.Code);
    }

    [Fact]
    public async Task BrowsersCannotChangeTheStationsToggles()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        var ex = await Assert.ThrowsAsync<EngineAccessException>(() => remote.SetToggleAsync(Toggle.AprsIs, false, Ct));

        Assert.Equal(EngineErrorCodes.OperatorOnly, ex.EngineError.Code);
    }

    [Fact]
    public async Task BrowsersCannotClearTheStation()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        var ex = await Assert.ThrowsAsync<EngineAccessException>(() => remote.ClearAsync(ClearScope.Stations, Ct));

        Assert.Equal(EngineErrorCodes.OperatorOnly, ex.EngineError.Code);
    }

    [Fact]
    public async Task AnyBrowserCanListServersAndReadLocators()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        Assert.Equal("rotate", (await remote.GetServerPresetsAsync(Ct))[0].Key);
        Assert.Equal("FN31pr", (await remote.ParseLocatorAsync("fn31pr", Ct)).Locator);
        Assert.Equal("FN31pr", await remote.GetLocatorAsync(41.7291667, -72.7083333, Ct));

        var bad = await Assert.ThrowsAsync<EngineArgumentException>(() => remote.ParseLocatorAsync("DN70ZZ", Ct));
        Assert.Equal(EngineErrorCodes.LocatorInvalidCharacter, bad.EngineError.Code);
        Assert.Equal("subsquare", bad.EngineError.Arg(EngineErrorArgs.Part));
        var off = await Assert.ThrowsAsync<EngineArgumentException>(() => remote.GetLocatorAsync(0, 200, Ct));
        Assert.Equal(EngineErrorCodes.PositionOffGlobe, off.EngineError.Code);
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
    public async Task OnATrustedNetworkBrowsersCanReconnect()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        await remote.ReconnectAsync(Ct);
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
