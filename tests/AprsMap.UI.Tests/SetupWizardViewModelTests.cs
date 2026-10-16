// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.ViewModels;

namespace AprsMap.UI.Tests;

public class SetupWizardViewModelTests
{
    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    private static async Task<(SetupWizardViewModel Wizard, FakeEngine Engine, Localizer Text)> Start(
        FakeEngine? engine = null
    )
    {
        engine ??= new FakeEngine { Settings = AppSettings.Empty };
        var text = English();
        var wizard = new SetupWizardViewModel(engine, text);
        await wizard.LoadAsync(TestContext.Current.CancellationToken);
        return (wizard, engine, text);
    }

    private static async Task<SetupWizardViewModel> AtStep(SetupStep step, FakeEngine? engine = null)
    {
        var (wizard, _, _) = await Start(engine);
        wizard.Callsign = "AB1CD-9";
        while (wizard.Step < step)
        {
            await wizard.NextCommand.ExecuteAsync(null);
        }
        return wizard;
    }

    [Fact]
    public async Task ItStartsAtTheCallsignWithTheWorldwideServer()
    {
        var (wizard, _, _) = await Start();

        Assert.Equal(SetupStep.Callsign, wizard.Step);
        Assert.Equal("Step 1 of 4", wizard.StepText);
        Assert.Equal("Set up " + Brand.DisplayName, wizard.Title);
        Assert.Equal("rotate", wizard.SelectedServer?.Key);
        Assert.True(wizard.ReceiveOnly);
        Assert.True(wizard.CanLookUpAddresses);
        Assert.False(wizard.HasInput);
    }

    [Fact]
    public async Task TheCallsignIsTidiedAsItIsTyped()
    {
        var (wizard, _, _) = await Start();

        wizard.Callsign = "n0abc-9 ";

        Assert.Equal("N0ABC-9", wizard.Callsign);
        Assert.Equal("N0ABC", wizard.BaseCallsign);
    }

    [Theory]
    [InlineData("N0CALL")]
    [InlineData("nocall-5")]
    public async Task ThePlaceholderCallsignsAreRefused(string callsign)
    {
        var (wizard, _, text) = await Start();
        Assert.False(wizard.NextCommand.CanExecute(null)); // nothing typed yet

        wizard.Callsign = callsign;
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupStep.Callsign, wizard.Step);
        Assert.Equal(text["Wizard.Callsign.Placeholder"], wizard.CallsignError);
        wizard.Callsign = "N0ABC";
        Assert.Null(wizard.CallsignError);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.Location, wizard.Step);
    }

    [Fact]
    public async Task ALocatorGivesTheCentreOfItsSquare()
    {
        var wizard = await AtStep(SetupStep.Location);

        wizard.LocatorText = "dn70kb";
        await wizard.UseLocatorCommand.ExecuteAsync(null);

        Assert.Equal(40.0625, wizard.Latitude);
        Assert.Equal(-105.125, wizard.Longitude);
        Assert.Equal("Location: 40.0625, -105.1250 · DN70kb", wizard.LocationText);
    }

    [Fact]
    public async Task ABadLocatorSaysWhyInTheViewersLanguage()
    {
        var (wizard, _, text) = await Start();
        wizard.Callsign = "AB1CD";
        await wizard.NextCommand.ExecuteAsync(null);

        wizard.LocatorText = "DN7";
        await wizard.UseLocatorCommand.ExecuteAsync(null);

        Assert.False(wizard.HasLocation);
        Assert.Equal(
            text.Format(
                new EngineError(
                    EngineErrorCodes.LocatorWrongLength,
                    "x",
                    new Dictionary<string, string> { ["value"] = "DN7" }
                )
            ),
            wizard.LocationError
        );
    }

    [Fact]
    public async Task CoordinatesAreReadAndGetTheirLocator()
    {
        var (wizard, _, text) = await Start();

        wizard.CoordinatesText = "91, 0";
        await wizard.UseCoordinatesCommand.ExecuteAsync(null);
        Assert.Equal(text["Wizard.Location.Coordinates.OutOfRange"], wizard.LocationError);

        wizard.CoordinatesText = "Boulder";
        await wizard.UseCoordinatesCommand.ExecuteAsync(null);
        Assert.Equal(text["Wizard.Location.Coordinates.Unreadable"], wizard.LocationError);

        wizard.CoordinatesText = "40 00.90 N 105 16.23 W";
        await wizard.UseCoordinatesCommand.ExecuteAsync(null);
        Assert.Null(wizard.LocationError);
        Assert.Equal(40.015, wizard.Latitude!.Value, 5);
        Assert.EndsWith("· DN70kb", wizard.LocationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NextUsesALocatorTypedButNotYetConfirmed()
    {
        var wizard = await AtStep(SetupStep.Location);

        wizard.LocatorText = "DN7";
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.Location, wizard.Step); // can't be read: stays, with the reason

        wizard.LocatorText = "FN31pr";
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.Server, wizard.Step);
        Assert.Equal(41.7291667, wizard.Latitude);
    }

    [Fact]
    public async Task SwitchingWaysKeepsTheConfirmedLocation()
    {
        var wizard = await AtStep(SetupStep.Location);
        wizard.LocatorText = "DN70";
        await wizard.UseLocatorCommand.ExecuteAsync(null);

        wizard.IsCoordinatesMethod = true;
        wizard.IsLocatorMethod = false; // unticking never changes the way

        Assert.Equal(LocationMethod.Coordinates, wizard.Method);
        Assert.Equal(40.5, wizard.Latitude);
        await wizard.NextCommand.ExecuteAsync(null); // nothing typed in the new way: keeps DN70
        Assert.Equal(40.5, wizard.Latitude);
    }

    private sealed class FakeDeviceLocation(Func<DeviceFix> fix) : IDeviceLocation
    {
        public int Calls { get; private set; }

        public Task<DeviceFix> GetAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            try
            {
                return Task.FromResult(fix());
            }
            catch (DeviceLocationException ex)
            {
                return Task.FromException<DeviceFix>(ex);
            }
        }
    }

    [Fact]
    public async Task WithoutADeviceLocationTheChoiceIsHidden()
    {
        var (wizard, _, _) = await Start();

        Assert.False(wizard.CanUseDeviceLocation);
        Assert.False(wizard.UseDeviceLocationCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheDevicesLocationIsUsedAndSaidToBeTheDevices()
    {
        var device = new FakeDeviceLocation(() => new DeviceFix(38.9991481, -94.6818162, 30));
        var wizard = new SetupWizardViewModel(new FakeEngine { Settings = AppSettings.Empty }, English(), device);
        await wizard.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(wizard.CanUseDeviceLocation);

        wizard.IsDeviceMethod = true;
        await wizard.UseDeviceLocationCommand.ExecuteAsync(null);

        Assert.Equal(LocationMethod.Device, wizard.Method);
        Assert.Equal(1, device.Calls);
        Assert.Equal(38.9991481, wizard.Latitude);
        Assert.Equal(
            "This device's location: 38.9991, -94.6818 · DM79xx. Check that it's where the station is.",
            wizard.LocationText
        );
        Assert.False(wizard.IsLocating);

        // Another way's location is no longer the device's.
        wizard.IsLocatorMethod = true;
        wizard.LocatorText = "DN70";
        await wizard.UseLocatorCommand.ExecuteAsync(null);
        Assert.StartsWith("Location: ", wizard.LocationText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeviceLocationError.Denied)]
    [InlineData(DeviceLocationError.Unavailable)]
    [InlineData(DeviceLocationError.Timeout)]
    [InlineData(DeviceLocationError.Insecure)]
    [InlineData(DeviceLocationError.Unsupported)]
    public async Task EachReasonTheDeviceCantSayIsExplained(DeviceLocationError error)
    {
        var device = new FakeDeviceLocation(() => throw new DeviceLocationException(error));
        var text = English();
        var wizard = new SetupWizardViewModel(new FakeEngine { Settings = AppSettings.Empty }, text, device);
        await wizard.LoadAsync(TestContext.Current.CancellationToken);
        wizard.IsDeviceMethod = true;

        await wizard.UseDeviceLocationCommand.ExecuteAsync(null);

        Assert.Equal(text["Wizard.Location.Device." + error], wizard.LocationError);
        Assert.DoesNotContain("⟦", wizard.LocationError, StringComparison.Ordinal);
        Assert.False(wizard.HasLocation);
        Assert.False(wizard.IsLocating);
        Assert.Equal(error.ToString(), new DeviceLocationException(error).Message);
    }

    [Fact]
    public async Task SkippingLeavesNoLocation()
    {
        var wizard = await AtStep(SetupStep.Location);
        wizard.LocatorText = "DN70";
        await wizard.UseLocatorCommand.ExecuteAsync(null);

        wizard.SkipLocationCommand.Execute(null);

        Assert.Equal(SetupStep.Server, wizard.Step);
        Assert.False(wizard.HasLocation);
        Assert.Null(wizard.LocationText);
    }

    [Fact]
    public async Task AnAddressIsLookedUpOnceAndASingleMatchIsPicked()
    {
        var engine = new FakeEngine
        {
            Settings = AppSettings.Empty,
            Geocoder = q => q == "nowhere" ? [] : [new GeocodeResult("Boulder, Colorado", 40.015, -105.27)],
        };
        var wizard = await AtStep(SetupStep.Location, engine);
        wizard.IsAddressMethod = true;
        Assert.False(wizard.LookUpCommand.CanExecute(null));
        Assert.Equal("What you type here is sent to nominatim.example to find it.", wizard.LookupNotice);
        Assert.Equal(new Uri("https://nominatim.example/policy"), wizard.LookupPolicyUri);

        wizard.AddressText = "Boulder";
        await wizard.LookUpCommand.ExecuteAsync(null);

        Assert.Equal(("Boulder", "en-US"), Assert.Single(engine.Lookups));
        Assert.Same(wizard.AddressResults[0], wizard.SelectedAddress);
        await WaitUntil(() => wizard.HasLocation);
        Assert.Equal(40.015, wizard.Latitude);

        wizard.AddressText = "nowhere";
        await wizard.LookUpCommand.ExecuteAsync(null);
        Assert.True(wizard.NoAddressMatch);
        Assert.Empty(wizard.AddressResults);
        Assert.Equal(40.015, wizard.Latitude); // a failed lookup keeps the confirmed location
    }

    [Fact]
    public async Task ALookupFailureIsShownAndOtherWaysStayAvailable()
    {
        var engine = new FakeEngine
        {
            Settings = AppSettings.Empty,
            Geocoder = _ => throw EngineErrors.Create(EngineErrorCodes.GeocodingRefused, "English"),
        };
        var wizard = await AtStep(SetupStep.Location, engine);
        var text = English();
        wizard.IsAddressMethod = true;
        wizard.AddressText = "Boulder";

        await wizard.LookUpCommand.ExecuteAsync(null);

        Assert.Equal(text["Error.geocoding.refused"], wizard.LocationError);
        Assert.False(wizard.IsLookingUp);
    }

    [Fact]
    public async Task WithoutLookupTheAddressChoiceIsHidden()
    {
        var (wizard, _, _) = await Start(
            new FakeEngine { Settings = AppSettings.Empty, Geocoding = GeocodingInfo.Disabled }
        );

        Assert.False(wizard.CanLookUpAddresses);
        Assert.Null(wizard.LookupNotice);
        Assert.Null(wizard.LookupPolicyUri);
    }

    [Fact]
    public async Task AnotherServerCanBeTypedButNotLeftEmpty()
    {
        var wizard = await AtStep(SetupStep.Server);
        Assert.Equal("rotate.aprs2.net:14580", wizard.Server);
        wizard.SelectedServer = wizard.Servers.Single(s => s.Key == "euro");
        Assert.Equal("euro.aprs2.net:14580", wizard.Server);
        Assert.Equal("Europe and Africa", wizard.SelectedServer.ToString());

        wizard.UseOwnServer = true;
        Assert.False(wizard.UseRegionServer);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.Server, wizard.Step);
        Assert.NotNull(wizard.ServerError);

        wizard.OwnServer = " t2test.example:10152 ";
        Assert.Null(wizard.ServerError);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.Passcode, wizard.Step);
        Assert.Equal("t2test.example:10152", wizard.Server);
    }

    [Fact]
    public async Task FinishSavesOneActiveProfileReceiveOnlyWithUnitsForTheRegion()
    {
        var engine = new FakeEngine { Settings = AppSettings.Empty };
        var wizard = await AtStep(SetupStep.Location, engine);
        bool? closed = null;
        wizard.Closed += (_, saved) => closed = saved;
        wizard.LocatorText = "DN70";
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsLastStep);

        await wizard.FinishCommand.ExecuteAsync(null);

        var saved = Assert.Single(engine.Saved);
        var profile = Assert.Single(saved.Profiles);
        Assert.Equal(profile.Id, saved.ActiveProfileId);
        Assert.Equal(("AB1CD-9", "AB1CD-9", -1), (profile.Name, profile.Callsign, profile.Passcode));
        var source = Assert.Single(saved.SourceList); // the wizard sets up the APRS-IS source (F1.25 behavior 7)
        Assert.Equal((SourceKind.AprsIs, "rotate.aprs2.net:14580", null), (source.Kind, source.Server, source.Filter));
        Assert.Equal([source.Id], profile.SourceIds);
        Assert.Equal((40.5, -105.0), (profile.Latitude, profile.Longitude));
        Assert.Equal(UnitSettings.Imperial, saved.Units); // en-US
        Assert.True(closed);
    }

    [Theory]
    [InlineData("en-US", true)]
    [InlineData("en-GB", false)]
    [InlineData("en", true)]
    [InlineData("my-MM", true)]
    [InlineData("de", false)]
    public void UnitsFollowTheRegion(string culture, bool imperial) =>
        Assert.Equal(
            imperial ? UnitSettings.Imperial : UnitSettings.Metric,
            SetupWizardViewModel.UnitsFor(CultureInfo.GetCultureInfo(culture))
        );

    [Fact]
    public void TheInvariantCultureGetsMetric() =>
        Assert.Equal(UnitSettings.Metric, SetupWizardViewModel.UnitsFor(CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("40000")]
    [InlineData("-1")]
    [InlineData("12a")]
    [InlineData("")]
    public async Task APasscodeMustBeANumberUpTo32767(string passcode)
    {
        var engine = new FakeEngine { Settings = AppSettings.Empty };
        var wizard = await AtStep(SetupStep.Passcode, engine);
        wizard.HasPasscode = true;
        wizard.PasscodeText = passcode;

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.Equal(English()["Wizard.Passcode.Invalid"], wizard.PasscodeError);
        Assert.Empty(engine.Saved);

        wizard.PasscodeText = "12345";
        Assert.Null(wizard.PasscodeError);
        await wizard.FinishCommand.ExecuteAsync(null);
        Assert.Equal(12345, Assert.Single(Assert.Single(engine.Saved).Profiles).Passcode);
    }

    [Fact]
    public async Task TheCalculatorNeedsTheLicenceTickAndNeverFillsInThePasscode()
    {
        var wizard = await AtStep(SetupStep.Passcode);
        var copied = new List<string>();
        wizard.CopyToClipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        wizard.HasPasscode = true;

        wizard.OpenCalculatorCommand.Execute(null);
        Assert.True(wizard.IsCalculatorOpen);
        Assert.False(wizard.CalculateCommand.CanExecute(null));

        wizard.IsLicensed = true;
        Assert.True(wizard.CalculateCommand.CanExecute(null));
        await wizard.CalculateCommand.ExecuteAsync(null);
        Assert.Equal("5000", wizard.CalculatedPasscode); // the fake's: base callsign length × 1000
        Assert.True(wizard.CanCopy);
        await wizard.CopyCommand.ExecuteAsync(null);
        Assert.Equal(["5000"], copied);
        Assert.Equal(string.Empty, wizard.PasscodeText);

        wizard.CloseCalculatorCommand.Execute(null);
        wizard.OpenCalculatorCommand.Execute(null);
        Assert.False(wizard.IsLicensed); // asked again every time
        Assert.Null(wizard.CalculatedPasscode);
    }

    [Fact]
    public async Task WithoutAClipboardThereIsNoCopyButton()
    {
        var wizard = await AtStep(SetupStep.Passcode);
        wizard.OpenCalculatorCommand.Execute(null);
        wizard.IsLicensed = true;

        await wizard.CalculateCommand.ExecuteAsync(null);
        await wizard.CopyCommand.ExecuteAsync(null); // nothing to copy to: does nothing

        Assert.NotNull(wizard.CalculatedPasscode);
        Assert.False(wizard.CanCopy);
    }

    [Fact]
    public async Task ACalculatorRefusalIsShown()
    {
        var engine = new FakeEngine { Settings = AppSettings.Empty };
        var wizard = await AtStep(SetupStep.Passcode, engine);
        wizard.Callsign = string.Empty;
        wizard.OpenCalculatorCommand.Execute(null);
        wizard.IsLicensed = true;

        await wizard.CalculateCommand.ExecuteAsync(null);

        Assert.Null(wizard.CalculatedPasscode);
        Assert.StartsWith("The callsign is empty.", wizard.CalculatorError, StringComparison.Ordinal);
    }

    public static TheoryData<string, SetupStep, string> Rejections() =>
        new()
        {
            { "aprs.callsign-invalid-character", SetupStep.Callsign, nameof(SetupWizardViewModel.CallsignError) },
            { "aprs.server-port-invalid", SetupStep.Server, nameof(SetupWizardViewModel.ServerError) },
            { EngineErrorCodes.SettingsReadOnly, SetupStep.Passcode, nameof(SetupWizardViewModel.FinishError) },
            { EngineErrorCodes.ProfileNameDuplicate, SetupStep.Passcode, nameof(SetupWizardViewModel.FinishError) },
        };

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task ARejectedSaveGoesBackToTheStepItIsAbout(string code, SetupStep step, string property)
    {
        var engine = new FakeEngine
        {
            Settings = AppSettings.Empty,
            SaveFailure = EngineErrors.Create(code, "English reason"),
        };
        var wizard = await AtStep(SetupStep.Passcode, engine);
        bool? closed = null;
        wizard.Closed += (_, saved) => closed = saved;

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.Equal(step, wizard.Step);
        Assert.NotNull(typeof(SetupWizardViewModel).GetProperty(property)!.GetValue(wizard));
        Assert.Null(closed);
        Assert.False(wizard.IsBusy);
        Assert.Equal("AB1CD-9", wizard.Callsign); // everything entered is kept
    }

    [Fact]
    public async Task ClosingAsksFirstOnlyWhenSomethingWasEntered()
    {
        var (wizard, _, _) = await Start();
        var closes = new List<bool>();
        wizard.Closed += (_, saved) => closes.Add(saved);

        wizard.CloseCommand.Execute(null);
        Assert.Equal([false], closes);

        wizard.Callsign = "AB1CD";
        wizard.CloseCommand.Execute(null);
        Assert.True(wizard.IsConfirmingClose);
        wizard.KeepEditingCommand.Execute(null);
        Assert.False(wizard.IsConfirmingClose);
        wizard.CloseCommand.Execute(null);
        wizard.CloseCommand.Execute(null);
        Assert.Equal([false, false], closes);
    }

    [Fact]
    public async Task EscapeClosesTheCalculatorBeforeTheWizard()
    {
        var wizard = await AtStep(SetupStep.Passcode);
        wizard.OpenCalculatorCommand.Execute(null);

        wizard.CloseCommand.Execute(null);

        Assert.False(wizard.IsCalculatorOpen);
        Assert.False(wizard.IsConfirmingClose);
    }

    [Fact]
    public async Task BackKeepsWhatWasEntered()
    {
        var wizard = await AtStep(SetupStep.Server);

        wizard.BackCommand.Execute(null);
        wizard.BackCommand.Execute(null);
        wizard.BackCommand.Execute(null);

        Assert.Equal(SetupStep.Callsign, wizard.Step);
        Assert.Equal("AB1CD-9", wizard.Callsign);
    }

    [Fact]
    public async Task ALanguageChangeRewordsTheWizard()
    {
        var (wizard, _, text) = await Start();
        wizard.CoordinatesText = "40, -105";
        await wizard.UseCoordinatesCommand.ExecuteAsync(null);

        text.SetLanguage(Localizer.PseudoLanguage);

        Assert.StartsWith("[Šţéþ 1", wizard.StepText, StringComparison.Ordinal);
        Assert.StartsWith("[Šéţ ûþ", wizard.Title, StringComparison.Ordinal);
        Assert.StartsWith("[Ŵöŕļðŵîðé", wizard.Servers[0].Name, StringComparison.Ordinal);
        Assert.Equal("rotate", wizard.SelectedServer?.Key);
        Assert.StartsWith("[Ļöçåţîöñ", wizard.LocationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARegionWithoutATranslationShowsItsEnglishName()
    {
        var engine = new FakeEngine
        {
            Settings = AppSettings.Empty,
            ServerPresets = [new("mars", "Mars", "mars.aprs2.net:14580")],
        };

        var (wizard, _, _) = await Start(engine);

        Assert.Equal("Mars", Assert.Single(wizard.Servers).Name);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Yield();
        }
        Assert.True(condition());
    }
}
