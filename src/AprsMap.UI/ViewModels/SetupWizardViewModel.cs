// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Input;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>The wizard's steps, in order.</summary>
public enum SetupStep
{
    /// <summary>Callsign and SSID.</summary>
    Callsign,

    /// <summary>Where the station is (optional).</summary>
    Location,

    /// <summary>Which APRS-IS server.</summary>
    Server,

    /// <summary>Passcode, or receive only.</summary>
    Passcode,
}

/// <summary>Ways to give the station's location.</summary>
public enum LocationMethod
{
    /// <summary>A Maidenhead locator.</summary>
    Locator,

    /// <summary>An address or postal code, looked up with Nominatim.</summary>
    Address,

    /// <summary>Typed coordinates.</summary>
    Coordinates,

    /// <summary>This device's location (the browser's geolocation).</summary>
    Device,
}

/// <summary>A server preset as the wizard lists it, with its region name in the viewer's language.</summary>
public sealed record ServerChoice(string Key, string Name, string Server)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// The first-run wizard (docs/features/F0.5-first-run-wizard.md): callsign, location, server and passcode, saved as
/// the first station profile. Nothing is saved until <see cref="FinishCommand"/>.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    /// <summary>Regions that use US customary units (behavior 28); everyone else gets metric.</summary>
    private static readonly HashSet<string> ImperialRegions = new(StringComparer.Ordinal) { "US", "LR", "MM" };

    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly IDeviceLocation? _deviceLocation;
    private IReadOnlyList<ServerPreset> _presets = [];

    /// <summary>Creates the wizard; call <see cref="LoadAsync"/> before showing it.</summary>
    public SetupWizardViewModel(IEngineService engine, Localizer? text = null, IDeviceLocation? deviceLocation = null)
    {
        _engine = engine;
        _deviceLocation = deviceLocation;
        _text = text ?? Localizer.Instance;
        _text.LanguageChanged += (_, _) => Retranslate();
    }

    /// <summary>Raised when the wizard should close: after saving (true) or when the user gives up (false).</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>Puts text on the clipboard; set by the view where a clipboard exists (behavior 26.5).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    public partial Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>"Set up AprsMap", with the product name from branding.</summary>
    public string Title =>
        _text.Format("Wizard.Title", new Dictionary<string, object?> { ["product"] = Brand.DisplayName });

    /// <summary>The APRS SSID conventions, linked from the callsign step (behavior 7).</summary>
    public static Uri SsidHelpUri { get; } = new("http://www.aprs.org/aprs11/SSIDs.txt");

    /// <summary>OpenStreetMap's copyright page, for the lookup attribution (behavior 20).</summary>
    public static Uri OpenStreetMapCopyrightUri { get; } = new("https://www.openstreetmap.org/copyright");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepNumber), nameof(IsCallsignStep), nameof(IsLocationStep))]
    [NotifyPropertyChangedFor(nameof(IsServerStep), nameof(IsPasscodeStep), nameof(IsFirstStep), nameof(IsLastStep))]
    public partial SetupStep Step { get; set; }

    /// <summary>1 to 4, for "Step 2 of 4".</summary>
    public int StepNumber => (int)Step + 1;

    /// <summary>Whether the callsign step is showing.</summary>
    public bool IsCallsignStep => Step == SetupStep.Callsign;

    /// <summary>Whether the location step is showing.</summary>
    public bool IsLocationStep => Step == SetupStep.Location;

    /// <summary>Whether the server step is showing.</summary>
    public bool IsServerStep => Step == SetupStep.Server;

    /// <summary>Whether the passcode step is showing.</summary>
    public bool IsPasscodeStep => Step == SetupStep.Passcode;

    /// <summary>Whether Back has nowhere to go.</summary>
    public bool IsFirstStep => Step == SetupStep.Callsign;

    /// <summary>Whether Next is Finish.</summary>
    public bool IsLastStep => Step == SetupStep.Passcode;

    /// <summary>"Step 2 of 4", in the viewer's language.</summary>
    [ObservableProperty]
    public partial string StepText { get; set; } = string.Empty;

    /// <summary>Whether a save is under way.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(FinishCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>A failure saving that belongs to no single field.</summary>
    [ObservableProperty]
    public partial string? FinishError { get; set; }

    /// <summary>Whether the "discard what you've entered?" question is showing (behavior 31).</summary>
    [ObservableProperty]
    public partial bool IsConfirmingClose { get; set; }

    // ── Step 1: callsign ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The callsign, trimmed and upper-cased as the user types (behavior 5).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial string Callsign { get; set; } = string.Empty;

    /// <summary>Why the callsign can't be used, or null.</summary>
    [ObservableProperty]
    public partial string? CallsignError { get; set; }

    partial void OnCallsignChanged(string value)
    {
        var tidy = value.Trim().ToUpperInvariant();
        if (tidy != value)
        {
            Callsign = tidy;
            return;
        }
        CallsignError = null;
    }

    /// <summary>The callsign without its SSID, as the passcode calculator shows it.</summary>
    public string BaseCallsign => Callsign.Split('-')[0];

    // ── Step 2: location ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The way the user is giving the location.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocatorMethod), nameof(IsAddressMethod), nameof(IsCoordinatesMethod))]
    [NotifyPropertyChangedFor(nameof(IsDeviceMethod))]
    public partial LocationMethod Method { get; set; }

    /// <summary>Whether the Maidenhead field is showing; setting it picks that way.</summary>
    public bool IsLocatorMethod
    {
        get => Method == LocationMethod.Locator;
        set => Pick(value, LocationMethod.Locator);
    }

    /// <summary>Whether the address lookup is showing; setting it picks that way.</summary>
    public bool IsAddressMethod
    {
        get => Method == LocationMethod.Address;
        set => Pick(value, LocationMethod.Address);
    }

    /// <summary>Whether the coordinates field is showing; setting it picks that way.</summary>
    public bool IsCoordinatesMethod
    {
        get => Method == LocationMethod.Coordinates;
        set => Pick(value, LocationMethod.Coordinates);
    }

    /// <summary>Whether "this device's location" is showing; setting it picks that way.</summary>
    public bool IsDeviceMethod
    {
        get => Method == LocationMethod.Device;
        set => Pick(value, LocationMethod.Device);
    }

    /// <summary>Whether this platform can give the device's location (behavior 9.4); the choice is hidden otherwise.</summary>
    public bool CanUseDeviceLocation => _deviceLocation is not null;

    /// <summary>Whether the device is being asked for its location.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseDeviceLocationCommand))]
    public partial bool IsLocating { get; set; }

    // Whether the confirmed location came from this device, which the confirmation line points out.
    private bool _locationFromDevice;

    private void Pick(bool chosen, LocationMethod method)
    {
        if (chosen)
        {
            Method = method;
        }
    }

    /// <summary>The Maidenhead locator as typed.</summary>
    [ObservableProperty]
    public partial string LocatorText { get; set; } = string.Empty;

    /// <summary>Coordinates as typed.</summary>
    [ObservableProperty]
    public partial string CoordinatesText { get; set; } = string.Empty;

    /// <summary>The address or postal code as typed. Never stored (behavior 11).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand))]
    public partial string AddressText { get; set; } = string.Empty;

    /// <summary>Why the current way's input couldn't give a location, or null.</summary>
    [ObservableProperty]
    public partial string? LocationError { get; set; }

    /// <summary>The confirmed latitude, or null for no location yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocation))]
    public partial double? Latitude { get; set; }

    /// <summary>The confirmed longitude, or null.</summary>
    [ObservableProperty]
    public partial double? Longitude { get; set; }

    /// <summary>Whether a location is confirmed.</summary>
    public bool HasLocation => Latitude is not null;

    /// <summary>"40.0625, -105.1250 · DN70kb" for the confirmed location (behavior 9).</summary>
    [ObservableProperty]
    public partial string? LocationText { get; set; }

    private string? _locationLocator;

    // The typed locator or coordinates the confirmed location came from, so Next knows whether there is new input.
    private (LocationMethod Method, string Text)? _confirmedInput;

    /// <summary>Whether address lookup is available (behavior 13); the choice is hidden otherwise.</summary>
    [ObservableProperty]
    public partial bool CanLookUpAddresses { get; set; }

    /// <summary>"Your text is sent to …" before the first lookup (behavior 14).</summary>
    [ObservableProperty]
    public partial string? LookupNotice { get; set; }

    /// <summary>The lookup server's usage policy, linked from the notice; null when there is none to show.</summary>
    [ObservableProperty]
    public partial Uri? LookupPolicyUri { get; set; }

    /// <summary>Whether a lookup is under way.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand))]
    public partial bool IsLookingUp { get; set; }

    /// <summary>Matches from the last lookup.</summary>
    public ObservableCollection<GeocodeResult> AddressResults { get; } = [];

    /// <summary>The match the user picked; picking one confirms its location.</summary>
    [ObservableProperty]
    public partial GeocodeResult? SelectedAddress { get; set; }

    /// <summary>Whether the last lookup found nothing.</summary>
    [ObservableProperty]
    public partial bool NoAddressMatch { get; set; }

    private string? _geocodingHost;

    partial void OnMethodChanged(LocationMethod value) => LocationError = null;

    partial void OnSelectedAddressChanged(GeocodeResult? value)
    {
        if (value is not null)
        {
            _ = ConfirmAsync(value.Latitude, value.Longitude, null);
        }
    }

    // ── Step 3: server ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The regional servers, in the viewer's language.</summary>
    public ObservableCollection<ServerChoice> Servers { get; } = [];

    /// <summary>The chosen region, unless <see cref="UseOwnServer"/>.</summary>
    [ObservableProperty]
    public partial ServerChoice? SelectedServer { get; set; }

    /// <summary>Whether the user types a server instead of picking a region.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseRegionServer))]
    public partial bool UseOwnServer { get; set; }

    /// <summary>The opposite of <see cref="UseOwnServer"/>, for the region radio button.</summary>
    public bool UseRegionServer
    {
        get => !UseOwnServer;
        set => UseOwnServer = !value;
    }

    /// <summary>The server the user typed: <c>host</c> or <c>host:port</c>.</summary>
    [ObservableProperty]
    public partial string OwnServer { get; set; } = string.Empty;

    /// <summary>Why the server can't be used, or null.</summary>
    [ObservableProperty]
    public partial string? ServerError { get; set; }

    partial void OnOwnServerChanged(string value) => ServerError = null;

    partial void OnUseOwnServerChanged(bool value) => ServerError = null;

    /// <summary>The server the profile will use.</summary>
    public string Server =>
        UseOwnServer
            ? OwnServer.Trim()
            : SelectedServer?.Server ?? (_presets.Count > 0 ? _presets[0].Server : string.Empty);

    // ── Step 4: passcode ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Receive only (the default), or log in with a passcode (behavior 24).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPasscode))]
    public partial bool ReceiveOnly { get; set; } = true;

    /// <summary>The opposite of <see cref="ReceiveOnly"/>, for the "I have a passcode" radio button.</summary>
    public bool HasPasscode
    {
        get => !ReceiveOnly;
        set => ReceiveOnly = !value;
    }

    /// <summary>The passcode as typed; the app never fills it in (behavior 25).</summary>
    [ObservableProperty]
    public partial string PasscodeText { get; set; } = string.Empty;

    /// <summary>Why the passcode can't be used, or null.</summary>
    [ObservableProperty]
    public partial string? PasscodeError { get; set; }

    partial void OnPasscodeTextChanged(string value) => PasscodeError = null;

    partial void OnReceiveOnlyChanged(bool value) => PasscodeError = null;

    /// <summary>Whether the passcode calculator is open (behavior 26).</summary>
    [ObservableProperty]
    public partial bool IsCalculatorOpen { get; set; }

    /// <summary>"I'm a licensed amateur radio operator and this is my own callsign." Asked every time; never stored.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CalculateCommand))]
    public partial bool IsLicensed { get; set; }

    /// <summary>The calculated passcode, shown with Copy; never copied into <see cref="PasscodeText"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    public partial string? CalculatedPasscode { get; set; }

    /// <summary>Why the passcode couldn't be calculated, or null.</summary>
    [ObservableProperty]
    public partial string? CalculatorError { get; set; }

    /// <summary>Whether Copy is offered: a passcode is shown and there is a clipboard.</summary>
    public bool CanCopy => CalculatedPasscode is not null && CopyToClipboard is not null;

    partial void OnIsLicensedChanged(bool value)
    {
        CalculatedPasscode = null;
        CalculatorError = null;
    }

    // ── Loading ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads the server list and lookup availability, and starts at step 1.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _presets = await _engine.GetServerPresetsAsync(cancellationToken);
        var geocoding = await _engine.GetGeocodingInfoAsync(cancellationToken);
        CanLookUpAddresses = geocoding.Enabled;
        _geocodingHost = geocoding.Host;
        LookupPolicyUri = Uri.TryCreate(geocoding.UsagePolicyUrl, UriKind.Absolute, out var policy) ? policy : null;
        Retranslate();
        SelectedServer = Servers.FirstOrDefault();
        Step = SetupStep.Callsign;
    }

    /// <summary>Whether anything has been entered, so closing should ask first.</summary>
    public bool HasInput =>
        Callsign.Length > 0
        || HasLocation
        || LocatorText.Length > 0
        || CoordinatesText.Length > 0
        || AddressText.Length > 0
        || UseOwnServer
        || !ReceiveOnly;

    // ── Navigation ──────────────────────────────────────────────────────────────────────────────────────────────

    private bool CanGoNext() => !IsBusy && (Step != SetupStep.Callsign || Callsign.Length > 0);

    /// <summary>Checks the current step and moves on; on the last step, saves.</summary>
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case SetupStep.Callsign when CheckCallsign():
                Step = SetupStep.Location;
                break;
            case SetupStep.Location when await ConfirmPendingAsync():
                Step = SetupStep.Server;
                break;
            case SetupStep.Server when CheckServer():
                Step = SetupStep.Passcode;
                break;
            case SetupStep.Passcode:
                await FinishAsync();
                break;
        }
    }

    /// <summary>
    /// Confirms a locator or coordinates typed but not yet used, so Enter in the field moves on (behavior 31). Returns
    /// false, with the reason shown, if that input can't be read.
    /// </summary>
    private async Task<bool> ConfirmPendingAsync()
    {
        var text = Method switch
        {
            LocationMethod.Locator => LocatorText.Trim(),
            LocationMethod.Coordinates => CoordinatesText.Trim(),
            _ => string.Empty,
        };
        if (text.Length == 0 || _confirmedInput == (Method, text))
        {
            return true;
        }
        await (Method == LocationMethod.Locator ? UseLocatorAsync() : UseCoordinatesAsync());
        return LocationError is null;
    }

    /// <summary>Goes back a step, keeping everything entered.</summary>
    [RelayCommand]
    private void Back()
    {
        if (Step > SetupStep.Callsign)
        {
            Step--;
        }
    }

    /// <summary>Moves on from the location step without a location (behavior 8).</summary>
    [RelayCommand]
    private void SkipLocation()
    {
        Latitude = Longitude = null;
        LocationText = null;
        _locationLocator = null;
        _confirmedInput = null;
        _locationFromDevice = false;
        Step = SetupStep.Server;
    }

    /// <summary>Closes, asking first if anything was entered (behavior 31).</summary>
    [RelayCommand]
    private void Close()
    {
        if (IsCalculatorOpen)
        {
            CloseCalculator();
        }
        else if (HasInput && !IsConfirmingClose)
        {
            IsConfirmingClose = true;
        }
        else
        {
            IsConfirmingClose = false;
            Closed?.Invoke(this, false);
        }
    }

    /// <summary>Keeps the wizard open after all.</summary>
    [RelayCommand]
    private void KeepEditing() => IsConfirmingClose = false;

    // ── Validation ──────────────────────────────────────────────────────────────────────────────────────────────

    private bool CheckCallsign()
    {
        // The engine checks the format on save (behavior 29); the wizard only refuses the well-known placeholders.
        if (BaseCallsign is "N0CALL" or "NOCALL")
        {
            CallsignError = _text["Wizard.Callsign.Placeholder"];
            return false;
        }
        return true;
    }

    private bool CheckServer()
    {
        if (UseOwnServer && OwnServer.Trim().Length == 0)
        {
            ServerError = _text["Error.aprs.server-host-empty"];
            return false;
        }
        return true;
    }

    private bool TryPasscode(out int passcode)
    {
        passcode = -1;
        if (ReceiveOnly)
        {
            return true;
        }
        if (
            int.TryParse(PasscodeText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out passcode)
            && passcode <= 32767
        )
        {
            return true;
        }
        PasscodeError = _text["Wizard.Passcode.Invalid"];
        return false;
    }

    // ── Location ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the Maidenhead locator (behavior 9.1).</summary>
    [RelayCommand]
    private async Task UseLocatorAsync()
    {
        try
        {
            var info = await _engine.ParseLocatorAsync(LocatorText);
            if (await ConfirmAsync(info.Latitude, info.Longitude, info.Locator))
            {
                _confirmedInput = (LocationMethod.Locator, LocatorText.Trim());
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            LocationError = _text.Describe(ex);
        }
    }

    /// <summary>Reads the typed coordinates (behavior 9.3).</summary>
    [RelayCommand]
    private async Task UseCoordinatesAsync()
    {
        switch (CoordinateParser.TryParse(CoordinatesText, out var lat, out var lon))
        {
            case CoordinateParseError.None:
                if (await ConfirmAsync(lat, lon, null))
                {
                    _confirmedInput = (LocationMethod.Coordinates, CoordinatesText.Trim());
                }
                break;
            case CoordinateParseError.OutOfRange:
                LocationError = _text["Wizard.Location.Coordinates.OutOfRange"];
                break;
            default:
                LocationError = _text["Wizard.Location.Coordinates.Unreadable"];
                break;
        }
    }

    private bool CanLookUp() => !IsLookingUp && AddressText.Trim().Length > 0;

    /// <summary>Looks the address up: one request per press, never while typing (behavior 15).</summary>
    [RelayCommand(CanExecute = nameof(CanLookUp))]
    private async Task LookUpAsync()
    {
        IsLookingUp = true;
        LocationError = null;
        NoAddressMatch = false;
        AddressResults.Clear();
        try
        {
            var results = await _engine.GeocodeAsync(AddressText, _text.Culture.Name);
            foreach (var result in results)
            {
                AddressResults.Add(result);
            }
            NoAddressMatch = results.Count == 0;
            if (results.Count == 1)
            {
                SelectedAddress = results[0]; // a single match is preselected (behavior 19)
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            LocationError = _text.Describe(ex);
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    private bool CanUseDevice() => _deviceLocation is not null && !IsLocating;

    /// <summary>
    /// Asks the device where it is (behavior 9.4). The browser asks the user first. The result is the viewer's
    /// location, which on a gateway may not be the station's, so the confirmation line says so.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseDevice))]
    private async Task UseDeviceLocationAsync()
    {
        IsLocating = true;
        LocationError = null;
        try
        {
            var fix = await _deviceLocation!.GetAsync();
            if (await ConfirmAsync(fix.Latitude, fix.Longitude, null))
            {
                _locationFromDevice = true;
                ShowLocation();
            }
        }
        catch (DeviceLocationException ex)
        {
            // Written out, so the localization guard sees every key.
            LocationError = ex.Error switch
            {
                DeviceLocationError.Denied => _text["Wizard.Location.Device.Denied"],
                DeviceLocationError.Timeout => _text["Wizard.Location.Device.Timeout"],
                DeviceLocationError.Insecure => _text["Wizard.Location.Device.Insecure"],
                DeviceLocationError.Unsupported => _text["Wizard.Location.Device.Unsupported"],
                _ => _text["Wizard.Location.Device.Unavailable"],
            };
        }
        finally
        {
            IsLocating = false;
        }
    }

    /// <summary>Confirms a location from any way, with its locator for the confirmation line.</summary>
    private async Task<bool> ConfirmAsync(double latitude, double longitude, string? locator)
    {
        LocationError = null;
        try
        {
            locator ??= await _engine.GetLocatorAsync(latitude, longitude);
        }
        catch (ArgumentException ex)
        {
            LocationError = _text.Describe(ex);
            return false;
        }
        Latitude = latitude;
        Longitude = longitude;
        _locationLocator = locator;
        _confirmedInput = null;
        _locationFromDevice = false;
        ShowLocation();
        return true;
    }

    private void ShowLocation() =>
        LocationText =
            Latitude is { } lat && Longitude is { } lon
                ? _text.Format(
                    _locationFromDevice ? "Wizard.Location.ConfirmedDevice" : "Wizard.Location.Confirmed",
                    new Dictionary<string, object?>
                    {
                        ["latitude"] = lat.ToString("0.0000", _text.Culture),
                        ["longitude"] = lon.ToString("0.0000", _text.Culture),
                        ["locator"] = _locationLocator ?? string.Empty,
                    }
                )
                : null;

    // ── Passcode calculator ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens the calculator with the tick cleared (behavior 26.4).</summary>
    [RelayCommand]
    private void OpenCalculator()
    {
        IsLicensed = false;
        CalculatedPasscode = null;
        CalculatorError = null;
        IsCalculatorOpen = true;
    }

    /// <summary>Closes the calculator; the passcode field is left as it was.</summary>
    [RelayCommand]
    private void CloseCalculator()
    {
        IsCalculatorOpen = false;
        IsLicensed = false;
        CalculatedPasscode = null;
    }

    /// <summary>Asks the engine for the passcode; only once the user has ticked the licence box.</summary>
    [RelayCommand(CanExecute = nameof(IsLicensed))]
    private async Task CalculateAsync()
    {
        CalculatorError = null;
        try
        {
            CalculatedPasscode = (await _engine.ComputePasscodeAsync(BaseCallsign)).ToString(
                CultureInfo.InvariantCulture
            );
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
        {
            CalculatorError = _text.Describe(ex);
        }
    }

    /// <summary>Puts the calculated passcode on the clipboard.</summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        if (CalculatedPasscode is { } passcode && CopyToClipboard is { } copy)
        {
            await copy(passcode);
        }
    }

    // ── Finish ──────────────────────────────────────────────────────────────────────────────────────────────────

    private bool CanFinish() => !IsBusy;

    /// <summary>Saves the first profile and closes (behaviors 28–30).</summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private async Task FinishAsync()
    {
        FinishError = null;
        if (!CheckCallsign())
        {
            Step = SetupStep.Callsign;
            return;
        }
        if (!CheckServer())
        {
            Step = SetupStep.Server;
            return;
        }
        if (!TryPasscode(out var passcode))
        {
            return;
        }

        var profile = new StationProfile(
            Guid.NewGuid().ToString("N"),
            Callsign,
            Callsign,
            passcode,
            Server,
            null,
            Latitude,
            Longitude
        );
        IsBusy = true;
        try
        {
            await _engine.SaveSettingsAsync(new AppSettings([profile], profile.Id, UnitsFor(_text.Culture)));
            Closed?.Invoke(this, true);
        }
        catch (ArgumentException ex)
        {
            ShowRejection(ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            FinishError = _text.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Returns to the step a rejection is about and shows it there (behavior 29).</summary>
    private void ShowRejection(ArgumentException exception)
    {
        var code = (exception as IHasEngineError)?.EngineError.Code ?? string.Empty;
        var message = _text.Describe(exception);
        if (code.StartsWith("aprs.callsign-", StringComparison.Ordinal))
        {
            Step = SetupStep.Callsign;
            CallsignError = message;
        }
        else if (code.StartsWith("aprs.server-", StringComparison.Ordinal))
        {
            Step = SetupStep.Server;
            UseOwnServer = true;
            OwnServer = Server;
            ServerError = message;
        }
        else
        {
            FinishError = message;
        }
    }

    /// <summary>US customary units for the US, Liberia and Myanmar; metric everywhere else (behavior 28).</summary>
    internal static UnitSettings UnitsFor(CultureInfo culture)
    {
        try
        {
            var specific = culture.IsNeutralCulture ? CultureInfo.CreateSpecificCulture(culture.Name) : culture;
            return ImperialRegions.Contains(new RegionInfo(specific.Name).TwoLetterISORegionName)
                ? UnitSettings.Imperial
                : UnitSettings.Metric;
        }
        catch (ArgumentException)
        {
            return UnitSettings.Metric; // the invariant culture, or one with no region
        }
    }

    // ── Language ────────────────────────────────────────────────────────────────────────────────────────────────

    partial void OnStepChanged(SetupStep value)
    {
        StepText = _text.Format(
            "Wizard.StepOf",
            new Dictionary<string, object?> { ["step"] = StepNumber, ["steps"] = 4 }
        );
        IsConfirmingClose = false;
    }

    private void Retranslate()
    {
        OnStepChanged(Step);
        OnPropertyChanged(nameof(Title));
        LookupNotice = _geocodingHost is null
            ? null
            : _text.Format(
                "Wizard.Location.Address.Notice",
                new Dictionary<string, object?> { ["host"] = _geocodingHost }
            );
        var selected = SelectedServer?.Key;
        Servers.Clear();
        foreach (var preset in _presets)
        {
            var key = "Wizard.Server.Region." + preset.Key;
            Servers.Add(new ServerChoice(preset.Key, Localizer.Exists(key) ? _text[key] : preset.Name, preset.Server));
        }
        SelectedServer = Servers.FirstOrDefault(s => s.Key == selected) ?? Servers.FirstOrDefault();
        ShowLocation();
    }
}
