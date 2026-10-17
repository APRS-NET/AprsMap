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

    /// <summary>Which APRS-IS server, or a TNC instead.</summary>
    Server,

    /// <summary>Passcode, or receive only.</summary>
    Passcode,
}

/// <summary>What the wizard is for (docs/features/F0.8-profiles.md, behaviors 5–7).</summary>
public enum WizardMode
{
    /// <summary>First run: the first profile, with units from the region (F0.5).</summary>
    FirstRun,

    /// <summary>Another profile, added from Settings.</summary>
    Add,

    /// <summary>A profile changed from Settings, keeping its id.</summary>
    Edit,
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

    /// <summary>A point picked on the map, under a fixed crosshair (behavior 9.5).</summary>
    Map,
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
    private readonly AppSettings? _existing;
    private readonly StationProfile? _editing;
    private string? _initialState;

    /// <summary>
    /// Creates the wizard; call <see cref="LoadAsync"/> before showing it. Add and edit need the current
    /// <paramref name="existing"/> settings; edit also the profile to change.
    /// </summary>
    public SetupWizardViewModel(
        IEngineService engine,
        Localizer? text = null,
        IDeviceLocation? deviceLocation = null,
        WizardMode mode = WizardMode.FirstRun,
        AppSettings? existing = null,
        StationProfile? editing = null
    )
    {
        if (mode != WizardMode.FirstRun && existing is null)
        {
            throw new ArgumentNullException(
                nameof(existing),
                "Adding or editing a profile needs the current settings."
            );
        }
        if (mode == WizardMode.Edit && editing is null)
        {
            throw new ArgumentNullException(nameof(editing), "Editing needs the profile to change.");
        }
        _engine = engine;
        _deviceLocation = deviceLocation;
        Mode = mode;
        _existing = existing;
        _editing = editing;
        _text = text ?? Localizer.Instance;
        _text.LanguageChanged += (_, _) => Retranslate();
        SourceChoices = ChoicesOfSources();
        // Editing keeps the profile's source; a new profile starts with the active one's (F1.25 behavior 6).
        var current = existing?.AprsIsSourceOf(editing ?? existing.GetActiveProfile());
        SelectedSource =
            SourceChoices.FirstOrDefault(c => current is not null && c.Source?.Id == current.Id)
            ?? (editing is not null && current is null ? SourceChoices.FirstOrDefault(c => c.IsNone) : null)
            ?? SourceChoices.FirstOrDefault(c => c.IsNew);
    }

    /// <summary>
    /// The APRS-IS sources a profile may pick, then a new server and none; empty for first run or with no sources yet,
    /// when the server step sets up a new one.
    /// </summary>
    public IReadOnlyList<SourceChoice> SourceChoices { get; }

    /// <summary>Whether the profile picks from existing sources.</summary>
    public bool HasSourceChoices => SourceChoices.Count > 0;

    /// <summary>The source chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewSource))]
    public partial SourceChoice? SelectedSource { get; set; }

    /// <summary>Whether the server step sets up a new APRS-IS source (server presets and filter text).</summary>
    public bool IsNewSource => !HasSourceChoices || SelectedSource?.IsNew == true;

    private IReadOnlyList<SourceChoice> ChoicesOfSources()
    {
        if (!IsProfileMode || _existing is null)
        {
            return [];
        }
        var sources = _existing.SourceList.Where(s => s.Kind == SourceKind.AprsIs).ToList();
        return sources.Count == 0
            ? []
            :
            [
                .. sources.Select(s => new SourceChoice(
                    s,
                    // A source named after its server says it once; one named otherwise shows where it connects.
                    s.Name == s.Server
                    || s.Name == SourceSettings.NameFor(s.Server ?? string.Empty)
                        ? s.Name
                        : $"{s.Name} · {s.Server}",
                    false,
                    false
                )),
                new SourceChoice(null, _text["Wizard.Source.New"], true, false),
                new SourceChoice(null, _text["Wizard.Source.None"], false, true),
            ];
    }

    /// <summary>Raised when the wizard should close: after saving (true) or when the user gives up (false).</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>Puts text on the clipboard; set by the view where a clipboard exists (behavior 26.5).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy))]
    public partial Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>First run, add or edit.</summary>
    public WizardMode Mode { get; }

    /// <summary>Whether the profile name and filter are asked for (adding or editing from Settings).</summary>
    public bool IsProfileMode => Mode != WizardMode.FirstRun;

    /// <summary>Whether "Switch to it now" is offered (adding).</summary>
    public bool IsAddMode => Mode == WizardMode.Add;

    /// <summary>"Set up AprsMap", "Add a profile" or "Edit Home".</summary>
    public string Title =>
        Mode switch
        {
            WizardMode.Add => _text["Wizard.Title.Add"],
            WizardMode.Edit => _text.Format(
                "Wizard.Title.Edit",
                new Dictionary<string, object?> { ["profile"] = _editing!.Name }
            ),
            _ => _text.Format("Wizard.Title", new Dictionary<string, object?> { ["product"] = Brand.DisplayName }),
        };

    /// <summary>The profile's name; follows the callsign until the user types one (F0.8 behavior 5.1).</summary>
    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    /// <summary>Why the name can't be used, or null.</summary>
    [ObservableProperty]
    public partial string? NameError { get; set; }

    private bool _nameChosen;
    private bool _settingName;

    partial void OnProfileNameChanged(string value)
    {
        NameError = null;
        if (!_settingName)
        {
            _nameChosen = value.Trim().Length > 0;
        }
    }

    /// <summary>The server-side filter, or empty for the stand-in around the location (F0.8 behavior 5.2).</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>The ranges offered around the station, in km (F1.2 behavior 1); 0 is none.</summary>
    public static IReadOnlyList<int> RangeSteps { get; } = [0, 25, 50, 100, 150, 250, 500];

    /// <summary>The range choices, named in the viewer's language.</summary>
    public IReadOnlyList<RangeChoice> Ranges =>
        [
            .. RangeSteps.Select(km => new RangeChoice(
                km,
                km == 0
                    ? _text["Wizard.Range.None"]
                    : _text.Format("Wizard.Range.Km", new Dictionary<string, object?> { ["km"] = km })
            )),
        ];

    /// <summary>How far around the station APRS-IS sends packets (F1.2 behavior 1).</summary>
    [ObservableProperty]
    public partial int RangeKm { get; set; } = StationProfile.DefaultRangeKm;

    /// <summary>The selected range choice, for the list.</summary>
    public RangeChoice? SelectedRange
    {
        get => Ranges.FirstOrDefault(r => r.Km == RangeKm);
        set
        {
            if (value is not null && value.Km != RangeKm)
            {
                RangeKm = value.Km;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The APRS-IS filter syntax, linked from the filter's help.</summary>
    public static Uri FilterHelpUri { get; } = new("https://www.aprs-is.net/javAPRSFilter.aspx");

    /// <summary>Whether a new profile becomes the active one (F0.8 behavior 5.3).</summary>
    [ObservableProperty]
    public partial bool SwitchToNew { get; set; }

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

    /// <summary>Whether Next is Finish: on the passcode step, or on the server step with a TNC (no APRS-IS login).</summary>
    public bool IsLastStep => Step == SetupStep.Passcode || (Step == SetupStep.Server && SkipsPasscode);

    /// <summary>No passcode step: a TNC-only profile, or a listener's (F2.1 behavior 4), which never logs in verified.</summary>
    public bool SkipsPasscode => UseTnc || ListenOnly;

    /// <summary>How many steps there are: 4, or 3 with a TNC, which needs no passcode.</summary>
    public int StepCount => SkipsPasscode ? 3 : 4;

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
        if (IsLicensed && _licensedBase is not null && BaseCallsign != _licensedBase)
        {
            IsLicensed = false; // the confirmation was for the other call (F2.1 behavior 4.1)
        }
        if (!_nameChosen)
        {
            _settingName = true;
            ProfileName = value;
            _settingName = false;
        }
    }

    /// <summary>The callsign without its SSID, as the passcode calculator shows it.</summary>
    public string BaseCallsign => Callsign.Split('-')[0];

    // ── Transmitting (docs/features/F2.1-own-station.md, behavior 4) ──────────────────────────────────────────

    private string? _listenerId;
    private string _callsignBeforeListening = string.Empty;
    private string? _licensedBase;

    /// <summary>
    /// "I'm not a licensed amateur (listen only)": the profile gets a generated listener ID instead of a callsign, logs
    /// in without a passcode and never transmits.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkipsPasscode), nameof(IsLastStep), nameof(StepCount), nameof(ListeningAsText))]
    public partial bool ListenOnly { get; set; }

    /// <summary>"You'll listen as SWL482913."</summary>
    public string? ListeningAsText =>
        ListenOnly ? _text.Format("Wizard.ListenOnly.As", new Dictionary<string, object?> { ["id"] = Callsign }) : null;

    partial void OnListenOnlyChanged(bool value)
    {
        if (value)
        {
            if (!ListenerId.Is(Callsign))
            {
                _callsignBeforeListening = Callsign;
            }
            Callsign = _listenerId ?? Callsign;
            IsLicensed = false;
            ReceiveOnly = true;
        }
        else if (ListenerId.Is(Callsign))
        {
            Callsign = _callsignBeforeListening;
        }
        CallsignError = null;
        OnPropertyChanged(nameof(ListeningAsText));
        OnStepChanged(Step); // "Step 1 of 3"
    }

    /// <summary>Disable TX: a receive-only profile, even with a licensed operator (behavior 4.2).</summary>
    [ObservableProperty]
    public partial bool DisableTransmit { get; set; }

    /// <summary>The symbols to choose from (docs/features/F2.1-own-station.md, behavior 1).</summary>
    public static IReadOnlyList<SymbolChoice> Symbols => SymbolChoice.All;

    /// <summary>The station's symbol: a house until the user picks another (F2.1 behavior 1).</summary>
    [ObservableProperty]
    public partial SymbolChoice? SelectedSymbol { get; set; } = SymbolChoice.For(StationProfile.DefaultSymbol);

    /// <summary>The comment sent with the station's positions (F2.1 behavior 2); the engine checks what it may hold.</summary>
    [ObservableProperty]
    public partial string Comment { get; set; } = string.Empty;

    /// <summary>Why the comment or symbol can't be sent, or null.</summary>
    [ObservableProperty]
    public partial string? CommentError { get; set; }

    partial void OnCommentChanged(string value) => CommentError = null;

    /// <summary>The most characters <see cref="Comment"/> may have.</summary>
    public static int MaxCommentLength => StationProfile.MaxCommentLength;

    // ── Step 2: location ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The way the user is giving the location.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocatorMethod), nameof(IsAddressMethod), nameof(IsCoordinatesMethod))]
    [NotifyPropertyChangedFor(nameof(IsDeviceMethod), nameof(IsMapMethod))]
    public partial LocationMethod Method { get; set; }

    /// <summary>Whether the map picker is showing; setting it picks that way (behavior 9.5).</summary>
    public bool IsMapMethod
    {
        get => Method == LocationMethod.Map;
        set => Pick(value, LocationMethod.Map);
    }

    /// <summary>The map the picker shows, with its tiles; null hides the choice (no map on this platform yet).</summary>
    public Map.MapViewModel? Map { get; init; }

    /// <summary>Whether the map picker is offered.</summary>
    public bool CanPickOnMap => Map is not null;

    /// <summary>Confirms the point under the map's crosshair (behavior 9.5).</summary>
    internal Task<bool> UseMapPointAsync(double latitude, double longitude) =>
        ConfirmAsync(Math.Round(latitude, 5), Math.Round(longitude, 5), null);

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

    /// <summary>
    /// Use a TNC instead (F1.25 behavior 7, F3.1 behavior 3): the profile receives on RF only, through a KISS over TCP
    /// source, with no APRS-IS source and no passcode step.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseAprsIs), nameof(IsLastStep), nameof(StepCount))]
    public partial bool UseTnc { get; set; }

    /// <summary>The opposite of <see cref="UseTnc"/>, for the APRS-IS fields.</summary>
    public bool UseAprsIs => !UseTnc;

    /// <summary>Where the TNC is: a host name or address.</summary>
    [ObservableProperty]
    public partial string TncHost { get; set; } = string.Empty;

    /// <summary>The TNC's KISS port, 8001 to start with (Direwolf's and soundmodem's).</summary>
    [ObservableProperty]
    public partial string TncPort { get; set; } = SourceSettings.DefaultKissPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>Why the TNC's details can't be used, or null.</summary>
    [ObservableProperty]
    public partial string? TncError { get; set; }

    partial void OnTncHostChanged(string value) => TncError = null;

    partial void OnTncPortChanged(string value) => TncError = null;

    partial void OnUseTncChanged(bool value)
    {
        TncError = null;
        OnStepChanged(Step); // "Step 3 of 3"
        NextCommand.NotifyCanExecuteChanged();
    }

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

    /// <summary>
    /// "I'm a licensed amateur and may transmit with this callsign": stored on the profile as
    /// <see cref="StationProfile.Licensed"/> (F2.1 behavior 4.1), and what unlocks the passcode calculator. Cleared when
    /// the base call changes.
    /// </summary>
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
        _licensedBase = value ? BaseCallsign : null;
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
        _listenerId = await _engine.NewListenerIdAsync(cancellationToken);
        if (_editing is { } profile)
        {
            await FillFromAsync(profile);
        }
        Step = SetupStep.Callsign;
        _initialState = State();
    }

    /// <summary>Fills the steps in from a profile being edited (F0.8 behavior 6).</summary>
    private async Task FillFromAsync(StationProfile profile)
    {
        if (ListenerId.Is(profile.Callsign))
        {
            _listenerId = profile.Callsign; // keeps its ID
            ListenOnly = true;
        }
        Callsign = profile.Callsign;
        IsLicensed = profile.Licensed && !ListenOnly;
        DisableTransmit = profile.DisableTransmit;
        SelectedSymbol = SymbolChoice.For(profile.EffectiveSymbol);
        Comment = profile.Comment ?? string.Empty;
        _settingName = true;
        ProfileName = profile.Name;
        _settingName = false;
        _nameChosen = true;
        // Where it connects is its APRS-IS source's (F1.25 behavior 5).
        var source = _existing?.AprsIsSourceOf(profile);
        var server = source?.Server ?? string.Empty;
        Filter = source?.Filter ?? string.Empty;
        RangeKm = profile.RangeKm;
        OnPropertyChanged(nameof(SelectedRange));
        var preset = _presets.FirstOrDefault(p => string.Equals(p.Server, server, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
        {
            SelectedServer = Servers.FirstOrDefault(s => s.Key == preset.Key);
        }
        else
        {
            UseOwnServer = true;
            OwnServer = server;
        }
        ReceiveOnly = profile.Passcode < 0;
        PasscodeText = profile.Passcode < 0 ? string.Empty : profile.Passcode.ToString(CultureInfo.InvariantCulture);
        if (profile is { Latitude: { } lat, Longitude: { } lon })
        {
            await ConfirmAsync(lat, lon, null);
        }
    }

    /// <summary>Everything the user can change, to tell whether an edit changed anything.</summary>
    private string State() =>
        string.Join(
            '\u001F',
            Callsign,
            ListenOnly ? "listen" : "",
            IsLicensed ? "licensed" : "",
            DisableTransmit ? "no-tx" : "",
            SelectedSymbol?.Symbol ?? "",
            Comment,
            ProfileName,
            Filter,
            SelectedSource?.Name ?? "",
            RangeKm.ToString(CultureInfo.InvariantCulture),
            Latitude?.ToString(CultureInfo.InvariantCulture) ?? "",
            Longitude?.ToString(CultureInfo.InvariantCulture) ?? "",
            Server,
            ReceiveOnly ? "" : PasscodeText,
            UseTnc ? $"{TncHost}:{TncPort}" : "",
            LocatorText,
            CoordinatesText,
            AddressText
        );

    /// <summary>Whether anything has been entered (or, when editing, changed), so closing should ask first.</summary>
    public bool HasInput =>
        Mode == WizardMode.Edit
            ? State() != _initialState
            : Callsign.Length > 0
                || HasLocation
                || LocatorText.Length > 0
                || CoordinatesText.Length > 0
                || AddressText.Length > 0
                || UseOwnServer
                || UseTnc
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
            case SetupStep.Server when CheckServer() && SkipsPasscode:
                await FinishAsync();
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
        if (UseTnc)
        {
            if (TncHost.Trim().Length == 0)
            {
                TncError = _text["Wizard.Tnc.HostEmpty"];
                return false;
            }
            return true;
        }
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
        if ((UseTnc || IsNewSource) && !CheckServer())
        {
            Step = SetupStep.Server;
            return;
        }
        var passcode = -1;
        if (ListenOnly)
        {
            passcode = -1; // a listener logs in without one (F2.1 behavior 4)
        }
        else if (UseTnc)
        {
            passcode = _editing?.Passcode ?? -1; // no APRS-IS login to use it; kept for when one's added
        }
        else if (!TryPasscode(out passcode))
        {
            return;
        }

        var source =
            UseTnc ? TncFor(TncHost.Trim(), TncPort.Trim())
            : IsNewSource ? SourceFor(Server, IsProfileMode && Filter.Trim().Length > 0 ? Filter.Trim() : null)
            : SelectedSource?.Source; // an existing source, or none
        var others = _editing is null
            ? []
            : _editing.SourceIds.Where(id =>
                _existing?.SourceList.FirstOrDefault(s => s.Id == id) is not { Kind: SourceKind.AprsIs }
            );
        var profile = new StationProfile(
            _editing?.Id ?? Guid.NewGuid().ToString("N"),
            IsProfileMode && ProfileName.Trim().Length > 0 ? ProfileName.Trim() : Callsign,
            Callsign,
            passcode,
            [.. others.Where(id => id != source?.Id), .. source is null ? Array.Empty<string>() : [source.Id]],
            Latitude,
            Longitude,
            IsProfileMode ? RangeKm : StationProfile.DefaultRangeKm,
            Symbol: SelectedSymbol?.Symbol,
            Comment: Comment.Trim().Length > 0 ? Comment.Trim() : null,
            PositionConfirmed: _editing?.PositionConfirmed ?? false, // the engine clears it when what's sent changed
            Licensed: IsLicensed && !ListenOnly,
            DisableTransmit: DisableTransmit
        );
        IsBusy = true;
        try
        {
            await _engine.SaveSettingsAsync(Settings(profile, source));
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

    /// <summary>
    /// The APRS-IS source for <paramref name="server"/> and <paramref name="filter"/>: an existing one with both the
    /// same, or a new one named after the server (F1.25 behavior 7).
    /// </summary>
    private SourceSettings SourceFor(string server, string? filter) =>
        _existing?.SourceList.FirstOrDefault(s =>
            s.Kind == SourceKind.AprsIs
            && string.Equals(s.Server, server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Filter, filter, StringComparison.Ordinal)
        )
        ?? SourceSettings.AprsIsFor(server, filter);

    /// <summary>
    /// The KISS over TCP source for <paramref name="host"/> and <paramref name="port"/>: an existing one that connects
    /// there, or a new one named after the host (F3.1 behavior 1.1). A port that isn't a number is passed on as out of
    /// range, so the engine's check words it.
    /// </summary>
    private SourceSettings TncFor(string host, string port)
    {
        int? number =
            port.Length == 0 ? null
            : int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : -1;
        if (number == SourceSettings.DefaultKissPort)
        {
            number = null;
        }
        return _existing?.SourceList.FirstOrDefault(s =>
                s.Kind == SourceKind.KissTcp
                && string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase)
                && (s.Port ?? SourceSettings.DefaultKissPort) == (number ?? SourceSettings.DefaultKissPort)
            )
            ?? SourceSettings.KissTcpFor(host, number);
    }

    /// <summary>
    /// The settings to save: the first profile, or the current ones with this profile added or replaced, and its
    /// source added when it is new.
    /// </summary>
    private AppSettings Settings(StationProfile profile, SourceSettings? source)
    {
        var existing = _existing ?? AppSettings.Empty;
        IReadOnlyList<SourceSettings> sources =
            source is null || existing.SourceList.Any(s => s.Id == source.Id)
                ? existing.SourceList
                : [.. existing.SourceList, source];
        return Mode switch
        {
            WizardMode.Add => existing with
            {
                Profiles = [.. existing.Profiles, profile],
                ActiveProfileId = SwitchToNew ? profile.Id : existing.ActiveProfileId,
                Sources = sources,
            },
            WizardMode.Edit => existing with
            {
                Profiles = [.. existing.Profiles.Select(p => p.Id == profile.Id ? profile : p)],
                Sources = sources,
            },
            _ => new AppSettings([profile], profile.Id, UnitsFor(_text.Culture), Sources: sources),
        };
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
        else if (code == EngineErrorCodes.ProfileNameDuplicate && IsProfileMode)
        {
            Step = SetupStep.Callsign;
            NameError = message;
        }
        else if (code is EngineErrorCodes.ProfileCommentInvalid or EngineErrorCodes.ProfileSymbolInvalid)
        {
            Step = SetupStep.Callsign;
            CommentError = message;
        }
        else if (code == EngineErrorCodes.KissSourceInvalid && UseTnc)
        {
            Step = SetupStep.Server;
            TncError = message;
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
            new Dictionary<string, object?> { ["step"] = StepNumber, ["steps"] = StepCount }
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

/// <summary>One range the profile editor offers.</summary>
/// <param name="Km">The range in km; 0 for none.</param>
/// <param name="Name">Its name, such as "150 km".</param>
public sealed record RangeChoice(int Km, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>An APRS-IS source the profile editor offers, or a new server, or none.</summary>
/// <param name="Source">The source, or null for a new server or none.</param>
/// <param name="Name">How it is listed.</param>
/// <param name="IsNew">Set up a new source from the server step.</param>
/// <param name="IsNone">No APRS-IS source: the profile receives nothing from APRS-IS.</param>
public sealed record SourceChoice(SourceSettings? Source, string Name, bool IsNew, bool IsNone)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}
