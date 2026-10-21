// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Sources (docs/features/F1.25-sources.md, behaviors 1–3): the master list profiles pick from, with Add a
/// source, edit and delete: APRS-IS, and KISS TNCs over TCP and on a serial port (F3.1 behavior 1); GPS is listed as
/// coming. Deleting a source in use asks first and unticks it from its profiles.
/// </summary>
public sealed partial class SourcesSectionViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly Action<AppSettings> _saved;
    private AppSettings _settings = AppSettings.Empty;
    private string? _editingId;

    internal SourcesSectionViewModel(IEngineService engine, Localizer text, Action<AppSettings> saved)
    {
        _engine = engine;
        _text = text;
        _saved = saved;
        Kinds = [.. Enum.GetValues<SourceKind>().Select(k => new SourceKindChoice(k, true, text, this))];
    }

    /// <summary>The kind in the form: picked for a new source, fixed for one being edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsAprsIs),
        nameof(IsKiss),
        nameof(IsKissTcp),
        nameof(IsKissSerial),
        nameof(IsSerial),
        nameof(IsTnc),
        nameof(CanTransmitKind),
        nameof(IsGps),
        nameof(ShowsHost),
        nameof(ShowsTncSerialParts),
        nameof(PortPlaceholder),
        nameof(BaudPlaceholder)
    )]
    public partial SourceKind EditKind { get; set; } = SourceKind.AprsIs;

    /// <summary>Whether the form shows APRS-IS's server and filter.</summary>
    public bool IsAprsIs => EditKind == SourceKind.AprsIs;

    // ── APRS-IS: the server as a drop-down, and the passcode (docs/features/F0.5-first-run-wizard.md, 25–27) ──────

    /// <summary>The worldwide rotate, the regional rotates, then Custom… (behavior 25).</summary>
    public ObservableCollection<ServerOption> ServerOptions { get; } = [];

    /// <summary>The server chosen; picking a preset puts its address in <see cref="EditServer"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomServer))]
    public partial ServerOption? SelectedServerOption { get; set; }

    /// <summary>Whether Custom… is chosen, which shows the <c>host</c> or <c>host:port</c> box.</summary>
    public bool IsCustomServer => SelectedServerOption is { Server: null };

    partial void OnSelectedServerOptionChanged(ServerOption? value)
    {
        if (value?.Server is { } server)
        {
            EditServer = server;
        }
        else if (value is not null && ServerOptions.Any(o => o.Server == EditServer))
        {
            EditServer = string.Empty; // Custom… starts empty, not on the preset it left
        }
    }

    /// <summary>Lists the presets from the engine and picks the one <paramref name="server"/> is, or Custom….</summary>
    private async Task ShowServersAsync(string? server)
    {
        if (ServerOptions.Count == 0)
        {
            IReadOnlyList<ServerPreset> presets;
            try
            {
                presets = await _engine.GetServerPresetsAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                presets = [];
            }
            foreach (var preset in presets)
            {
                var key = "Wizard.Server.Region." + preset.Key;
                var name = Localizer.Exists(key) ? _text[key] : preset.Name;
                ServerOptions.Add(new ServerOption(preset.Server, $"{name} · {preset.Server}"));
            }
            ServerOptions.Add(new ServerOption(null, _text["Settings.Sources.CustomServer"]));
        }
        SelectedServerOption = string.IsNullOrWhiteSpace(server)
            ? ServerOptions[0]
            : ServerOptions.FirstOrDefault(o =>
                string.Equals(o.Server, server.Trim(), StringComparison.OrdinalIgnoreCase)
            ) ?? ServerOptions[^1];
        if (IsCustomServer)
        {
            EditServer = server ?? string.Empty;
        }
    }

    /// <summary>
    /// Whether the passcode is asked: an APRS-IS source and a licensed active profile (behavior 26). It's stored on the
    /// profile, so the form says whose it is.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsPasscode { get; private set; }

    /// <summary>"Passcode for NZ0P-10".</summary>
    [ObservableProperty]
    public partial string PasscodeForText { get; private set; } = string.Empty;

    /// <summary>I have a passcode, rather than receive only.</summary>
    [ObservableProperty]
    public partial bool EditHasPasscode { get; set; }

    /// <summary>The passcode typed: 0 to 32767. The app never fills it in.</summary>
    [ObservableProperty]
    public partial string EditPasscode { get; set; } = string.Empty;

    /// <summary>The calculator's "licensed, own callsign" tick, asked each time the form opens (behavior 27.1).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CalculateCommand))]
    public partial bool CalculatorLicensed { get; set; }

    /// <summary>The passcode the calculator worked out, shown to copy; it doesn't fill in the field (behavior 27.2).</summary>
    [ObservableProperty]
    public partial string? CalculatedPasscode { get; private set; }

    /// <summary>Works out the active profile's passcode with APRS.NET, once the tick says it's the user's own callsign.</summary>
    [RelayCommand(CanExecute = nameof(CalculatorLicensed))]
    private async Task CalculateAsync()
    {
        if (_settings.GetActiveProfile() is not { } profile)
        {
            return;
        }
        try
        {
            CalculatedPasscode = (await _engine.ComputePasscodeAsync(profile.Callsign)).ToString(
                CultureInfo.InvariantCulture
            );
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = _text.Describe(ex);
        }
    }

    /// <summary>
    /// What the automatic filter receives for the active profile, said beside the filter box (F1.2 behavior 7): the
    /// profile's range around its location, and the area the map shows. Null for a source that isn't APRS-IS.
    /// </summary>
    [ObservableProperty]
    public partial string? AutomaticFilterText { get; private set; }

    /// <summary>"In use now: r/40/-105/50", once APRS-IS has a filter; null before.</summary>
    [ObservableProperty]
    public partial string? FilterInUseText { get; private set; }

    private string? _filterInUse;

    /// <summary>The filter APRS-IS has now, from the engine's status.</summary>
    internal void ShowFilter(string? filter)
    {
        _filterInUse = filter;
        FilterInUseText = string.IsNullOrEmpty(filter)
            ? null
            : _text.Format("Settings.Sources.FilterInUse", new Dictionary<string, object?> { ["filter"] = filter });
    }

    private void ShowAutomaticFilter()
    {
        var profile = _settings.GetActiveProfile();
        AutomaticFilterText =
            !IsAprsIs || profile is null ? null
            : profile.RangeKm <= 0 ? _text["Settings.Sources.AutomaticFilter.MapOnly"]
            : _text.Format(
                profile is { Latitude: not null, Longitude: not null }
                    ? "Settings.Sources.AutomaticFilter"
                    : "Settings.Sources.AutomaticFilter.NoLocation",
                new Dictionary<string, object?> { ["range"] = profile.RangeKm, ["callsign"] = profile.Callsign }
            );
        ShowFilter(_filterInUse);
    }

    /// <summary>Shows the passcode part for the kind in the form and the active profile.</summary>
    private void ShowPasscode()
    {
        ShowAutomaticFilter();
        var profile = _settings.GetActiveProfile();
        ShowsPasscode = IsAprsIs && profile is { Licensed: true } && !ListenerId.Is(profile.Callsign);
        PasscodeForText = _text.Format(
            "Settings.Sources.PasscodeFor",
            new Dictionary<string, object?> { ["callsign"] = profile?.Callsign ?? string.Empty }
        );
        EditHasPasscode = profile is { Passcode: >= 0 };
        EditPasscode = profile is { Passcode: >= 0 } p
            ? p.Passcode.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        CalculatorLicensed = false;
        CalculatedPasscode = null;
    }

    /// <summary>Whether the form shows a KISS TNC's radio port, TX path and the profiles that use it (F3.1 behavior 1).</summary>
    public bool IsKiss => EditKind is SourceKind.KissTcp or SourceKind.KissSerial;

    /// <summary>Whether the form shows a TCP TNC's host and port (F3.1 behavior 1.1).</summary>
    public bool IsKissTcp => EditKind == SourceKind.KissTcp;

    /// <summary>Whether the form shows a serial KISS TNC's type (docs/features/F3.2-built-in-tncs.md, behavior 4).</summary>
    public bool IsKissSerial => EditKind == SourceKind.KissSerial;

    /// <summary>
    /// Whether the form shows a serial port and speed (F3.1 behavior 1.2, F3.2 behavior 1): a serial TNC, or a GPS on a
    /// serial port (docs/features/F3.6-gps.md, behavior 1.1).
    /// </summary>
    public bool IsSerial =>
        EditKind is SourceKind.KissSerial or SourceKind.AprsTextSerial || (IsGps && EditGpsLink == GpsLink.Serial);

    /// <summary>Whether the form shows a serial TNC's help and control lines, which a GPS doesn't need.</summary>
    public bool ShowsTncSerialParts => IsSerial && !IsGps;

    /// <summary>Whether the form is for a GPS (docs/features/F3.6-gps.md, behavior 1).</summary>
    public bool IsGps => EditKind == SourceKind.Gps;

    /// <summary>How the GPS in the form is reached.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsSerial),
        nameof(ShowsTncSerialParts),
        nameof(ShowsHost),
        nameof(PortPlaceholder),
        nameof(BaudPlaceholder),
        nameof(GpsSerial),
        nameof(GpsTcp),
        nameof(GpsGpsd)
    )]
    public partial GpsLink EditGpsLink { get; set; } = GpsLink.Serial;

    /// <summary>NMEA on a serial port (behavior 1.1).</summary>
    public bool GpsSerial
    {
        get => EditGpsLink == GpsLink.Serial;
        set => SetLink(value, GpsLink.Serial);
    }

    /// <summary>NMEA over TCP (behavior 1.2).</summary>
    public bool GpsTcp
    {
        get => EditGpsLink == GpsLink.Tcp;
        set => SetLink(value, GpsLink.Tcp);
    }

    /// <summary>gpsd (behavior 1.3).</summary>
    public bool GpsGpsd
    {
        get => EditGpsLink == GpsLink.Gpsd;
        set => SetLink(value, GpsLink.Gpsd);
    }

    private void SetLink(bool chosen, GpsLink link)
    {
        if (chosen)
        {
            EditGpsLink = link;
        }
    }

    partial void OnEditGpsLinkChanged(GpsLink value)
    {
        if (value == GpsLink.Serial && IsEditing && IsGps)
        {
            _ = RefreshSerialPortsAsync();
        }
    }

    /// <summary>Whether the form shows a host and port: a TCP TNC, or a GPS over TCP or gpsd.</summary>
    public bool ShowsHost => IsKissTcp || (IsGps && EditGpsLink != GpsLink.Serial);

    /// <summary>The port used when none is typed, shown in the box.</summary>
    public string PortPlaceholder =>
        (
            !IsGps ? SourceSettings.DefaultKissPort
            : EditGpsLink == GpsLink.Gpsd ? SourceSettings.DefaultGpsdPort
            : SourceSettings.DefaultNmeaPort
        ).ToString(CultureInfo.InvariantCulture);

    /// <summary>The speed used when none is typed, shown in the box.</summary>
    public string BaudPlaceholder =>
        (IsGps ? SourceSettings.DefaultGpsBaud : SourceSettings.DefaultBaud).ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether it's this station's own radio, so the form asks which profiles use it.</summary>
    public bool IsTnc => EditKind is not SourceKind.AprsIs;

    /// <summary>Whether the kind can send, so RX only may be unticked: not TNC text output (F3.2 behavior 3).</summary>
    public bool CanTransmitKind => new SourceSettings("", "", EditKind).CanTransmit;

    /// <summary>The TNC types: Plain KISS, those APRS.NET knows, and Custom (F3.2 behavior 4).</summary>
    public ObservableCollection<TncTypeChoice> TncTypes { get; } = [];

    /// <summary>The TNC type in the form.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomTnc))]
    public partial TncTypeChoice? SelectedTncType { get; set; }

    /// <summary>Whether the form shows the custom scripts.</summary>
    public bool IsCustomTnc => SelectedTncType?.Id == SourceSettings.CustomTncType;

    /// <summary>A custom TNC's script into KISS, one step a line.</summary>
    [ObservableProperty]
    public partial string EditEnterScript { get; set; } = string.Empty;

    /// <summary>A custom TNC's script out of KISS; empty for none.</summary>
    [ObservableProperty]
    public partial string EditLeaveScript { get; set; } = string.Empty;

    /// <summary>Fills <see cref="TncTypes"/> and picks <paramref name="id"/>'s, keeping an unknown id as it is.</summary>
    private async Task ShowTncTypesAsync(string? id)
    {
        var known = Array.Empty<TncTypeInfo>() as IReadOnlyList<TncTypeInfo>;
        try
        {
            known = await _engine.GetTncTypesAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            // Offer Plain KISS and Custom only.
        }
        TncTypes.Clear();
        TncTypes.Add(new TncTypeChoice(null, _text["Settings.Sources.TncType.Plain"]));
        foreach (var type in known)
        {
            TncTypes.Add(new TncTypeChoice(type.Id, type.Name));
        }
        if (id is { Length: > 0 } && id != SourceSettings.CustomTncType && known.All(t => t.Id != id))
        {
            TncTypes.Add(new TncTypeChoice(id, id));
        }
        TncTypes.Add(new TncTypeChoice(SourceSettings.CustomTncType, _text["Settings.Sources.TncType.Custom"]));
        SelectedTncType = TncTypes.FirstOrDefault(t => t.Id == (id is { Length: > 0 } ? id : null)) ?? TncTypes[0];
    }

    /// <summary>The serial port in the form: picked from <see cref="SerialPorts"/> or typed.</summary>
    [ObservableProperty]
    public partial string EditSerialPort { get; set; } = string.Empty;

    /// <summary>The serial ports present on the engine's machine (on a gateway, the gateway's).</summary>
    public ObservableCollection<string> SerialPorts { get; } = [];

    /// <summary>"No serial ports found", when the list came back empty; null otherwise.</summary>
    [ObservableProperty]
    public partial string? SerialPortsNote { get; private set; }

    /// <summary>The speed in the form, picked from <see cref="Bauds"/> or typed; empty for 9600.</summary>
    [ObservableProperty]
    public partial string EditBaud { get; set; } = string.Empty;

    /// <summary>The speeds offered.</summary>
    public static IReadOnlyList<string> Bauds { get; } =
    [.. SourceSettings.CommonBauds.Select(b => b.ToString(CultureInfo.InvariantCulture))];

    /// <summary>Whether DTR is raised when the port opens; on unless a TNC needs it low.</summary>
    [ObservableProperty]
    public partial bool EditDtr { get; set; } = true;

    /// <summary>Whether RTS is raised when the port opens; on unless a TNC needs it low.</summary>
    [ObservableProperty]
    public partial bool EditRts { get; set; } = true;

    /// <summary>Whether the kind can still be picked: only while adding.</summary>
    [ObservableProperty]
    public partial bool CanPickKind { get; private set; }

    /// <summary>The TNC's host in the form.</summary>
    [ObservableProperty]
    public partial string EditHost { get; set; } = string.Empty;

    /// <summary>The TNC's port in the form; empty for 8001.</summary>
    [ObservableProperty]
    public partial string EditPort { get; set; } = string.Empty;

    /// <summary>The TNC's radio port in the form, 0 to 15; empty for 0.</summary>
    [ObservableProperty]
    public partial string EditTncPort { get; set; } = string.Empty;

    /// <summary>
    /// The profiles, each ticked when it uses the KISS source being edited (F3.1 behavior 2); a new one is ticked for
    /// the active profile, so it opens at once.
    /// </summary>
    public ObservableCollection<ProfileChoice> EditProfiles { get; } = [];

    /// <summary>
    /// RX only (docs/features/F2.1-own-station.md, behavior 5): the station never sends on this source. Always ticked for a
    /// kind that can't send.
    /// </summary>
    [ObservableProperty]
    public partial bool EditRxOnly { get; set; }

    /// <summary>A TNC's TX path; empty for the default, <c>WIDE1-1,WIDE2-1</c> (behavior 5.2).</summary>
    [ObservableProperty]
    public partial string EditTxPath { get; set; } = string.Empty;

    /// <summary>The default TX path, shown as the field's placeholder.</summary>
    public static string DefaultTxPath => SourceSettings.DefaultTxPath;

    partial void OnEditKindChanged(SourceKind value)
    {
        foreach (var kind in Kinds)
        {
            kind.Refresh();
        }
        if (
            (
                value is SourceKind.KissSerial or SourceKind.AprsTextSerial
                || (value == SourceKind.Gps && EditGpsLink == GpsLink.Serial)
            ) && IsEditing
        )
        {
            _ = RefreshSerialPortsAsync();
        }
        if (value == SourceKind.KissSerial && IsEditing && TncTypes.Count == 0)
        {
            _ = ShowTncTypesAsync(null);
        }
        if (!CanTransmitKind)
        {
            EditRxOnly = true;
        }
        if (IsEditing && _editingId is null)
        {
            TickActiveProfile(); // one APRS-IS source per profile: ticked only for a profile without one (behavior 24)
        }
        if (value == SourceKind.AprsIs && IsEditing)
        {
            _ = ShowServersAsync(EditServer);
        }
        ShowPasscode();
    }

    /// <summary>
    /// Ticks the active profile for a new source: always for a TNC, and for APRS-IS when it has none yet (F0.5 behavior
    /// 24), so a source added from first run's last page connects at once.
    /// </summary>
    private void TickActiveProfile()
    {
        var active = _settings.GetActiveProfile();
        var tick = active is not null && (!IsAprsIs || _settings.AprsIsSourceOf(active) is null);
        FillProfiles(id => tick && id == active!.Id);
    }

    /// <summary>Lists the serial ports present again, for a TNC plugged in while the form is open.</summary>
    [RelayCommand]
    private async Task RefreshSerialPortsAsync()
    {
        try
        {
            var ports = await _engine.GetSerialPortsAsync();
            SerialPorts.Clear();
            foreach (var port in ports)
            {
                SerialPorts.Add(port);
            }
            SerialPortsNote = ports.Count == 0 ? _text["Settings.Sources.NoSerialPorts"] : null;
            if (EditSerialPort.Length == 0 && ports.Count == 1)
            {
                EditSerialPort = ports[0]; // the one TNC plugged in
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            SerialPortsNote = _text.Describe(ex);
        }
    }

    /// <summary>The sources, in order.</summary>
    public ObservableCollection<SourceRow> Rows { get; } = [];

    /// <summary>The kinds Add a source offers; only built ones can be picked (behavior 1).</summary>
    public IReadOnlyList<SourceKindChoice> Kinds { get; }

    /// <summary>Whether this viewer may only look (a gateway without operator sign-in).</summary>
    [ObservableProperty]
    public partial bool IsReadOnly { get; private set; }

    /// <summary>Whether the add or edit form is open.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    /// <summary>The form's title: Add a source, or Edit with the source's name.</summary>
    [ObservableProperty]
    public partial string? EditTitle { get; private set; }

    /// <summary>The source's name in the form.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    /// <summary>The APRS-IS server in the form, <c>host</c> or <c>host:port</c>.</summary>
    [ObservableProperty]
    public partial string EditServer { get; set; } = string.Empty;

    /// <summary>The filter text in the form, added to the automatic filter (F1.2).</summary>
    [ObservableProperty]
    public partial string EditFilter { get; set; } = string.Empty;

    /// <summary>"Used by: Home, Portable" for the source being edited (behavior 2).</summary>
    [ObservableProperty]
    public partial string? EditUsedBy { get; private set; }

    /// <summary>Why the engine refused the last change, in words; null when it didn't.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The source waiting for its delete to be confirmed, because profiles use it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete))]
    public partial SourceRow? PendingDelete { get; private set; }

    /// <summary>Whether a delete waits for confirmation.</summary>
    public bool IsConfirmingDelete => PendingDelete is not null;

    /// <summary>"Rotate is used by Home and Portable. Delete it and untick it there?"</summary>
    [ObservableProperty]
    public partial string? PendingDeleteText { get; private set; }

    /// <summary>The settings shown.</summary>
    internal AppSettings Current => _settings;

    /// <summary>Shows <paramref name="settings"/>.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        IsReadOnly = settings.ReadOnly;
        _showingIGate = true;
        try
        {
            IGateOn = settings.GetActiveProfile()?.EffectiveIGate ?? false;
        }
        finally
        {
            _showingIGate = false;
        }
        Rows.Clear();
        foreach (var source in settings.SourceList)
        {
            Rows.Add(new SourceRow(source, this));
        }
        Retranslate();
    }

    /// <summary>Rewords the rows after a language change.</summary>
    internal void Retranslate()
    {
        foreach (var kind in Kinds)
        {
            kind.Retranslate(_text);
        }
        IGateHeading = _settings.GetActiveProfile() is { } active
            ? _text.Format("IGate.Heading", new Dictionary<string, object?> { ["profile"] = active.Callsign })
            : null;
        ShowIGate(_igate);
        foreach (var row in Rows)
        {
            var users = UsersOf(row.Source);
            row.KindText = _text[$"Settings.Sources.Kind.{row.Source.Kind}"];
            row.UsedByText =
                users.Count == 0
                    ? _text["Settings.Sources.Unused"]
                    : _text.Format(
                        "Settings.Sources.UsedBy",
                        new Dictionary<string, object?> { ["profiles"] = string.Join(", ", users) }
                    );
            row.EditName = _text.Format(
                "Settings.Sources.EditNamed",
                new Dictionary<string, object?> { ["source"] = row.Name }
            );
            row.TransmitText =
                !row.Source.MaySend ? _text["Settings.Sources.RxOnly"]
                : row.Source.Kind == SourceKind.AprsIs ? _text["Settings.Sources.CanSend"]
                : _text.Format(
                    "Settings.Sources.CanSendPath",
                    new Dictionary<string, object?> { ["path"] = row.Source.EffectiveTxPath }
                );
            row.TestSendName = _text.Format(
                "Settings.Sources.TestSendNamed",
                new Dictionary<string, object?> { ["source"] = row.Name }
            );
            row.DeleteName = _text.Format(
                "Settings.Sources.DeleteNamed",
                new Dictionary<string, object?> { ["source"] = row.Name }
            );
        }
    }

    /// <summary>Opens the form for a new APRS-IS source (behavior 2).</summary>
    [RelayCommand]
    private void Add()
    {
        _editingId = null;
        EditTitle = _text["Settings.Sources.Add"];
        EditName =
            EditServer =
            EditFilter =
            EditHost =
            EditPort =
            EditTncPort =
            EditSerialPort =
            EditBaud =
                string.Empty;
        EditDtr = EditRts = true;
        SerialPortsNote = null;
        EditEnterScript = EditLeaveScript = string.Empty;
        TncTypes.Clear();
        SelectedTncType = null;
        EditKind = SourceKind.AprsIs;
        EditGpsLink = GpsLink.Serial;
        CanPickKind = true;
        EditRxOnly = false;
        EditTxPath = string.Empty;
        EditUsedBy = null;
        Error = null;
        IsEditing = true;
        TickActiveProfile();
        ShowPasscode();
        _ = ShowServersAsync(null);
    }

    /// <summary>Opens Add a source on <paramref name="kind"/>, from first run's last page (F0.5 behavior 23.2).</summary>
    internal void AddKind(SourceKind kind)
    {
        Add();
        EditKind = kind;
    }

    internal void Edit(SourceRow row)
    {
        _editingId = row.Source.Id;
        EditTitle = _text.Format(
            "Settings.Sources.EditNamed",
            new Dictionary<string, object?> { ["source"] = row.Name }
        );
        EditName = row.Source.Name;
        EditSerialPort = row.Source.SerialPort ?? string.Empty;
        EditBaud = row.Source.Baud?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        EditDtr = row.Source.DtrEnable ?? true;
        EditRts = row.Source.RtsEnable ?? true;
        SerialPortsNote = null;
        EditEnterScript = row.Source.EnterScript ?? string.Empty;
        EditLeaveScript = row.Source.LeaveScript ?? string.Empty;
        TncTypes.Clear();
        SelectedTncType = null;
        EditKind = row.Source.Kind;
        EditGpsLink = row.Source.GpsLink ?? GpsLink.Serial;
        CanPickKind = false;
        EditServer = row.Source.Server ?? string.Empty;
        EditFilter = row.Source.Filter ?? string.Empty;
        EditHost = row.Source.Host ?? string.Empty;
        EditPort = row.Source.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        EditTncPort = row.Source.TncPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        EditRxOnly = !row.Source.MaySend;
        EditTxPath = row.Source.TxPath ?? string.Empty;
        FillProfiles(id => _settings.Profiles.Any(p => p.Id == id && p.Uses(row.Source)));
        EditUsedBy = row.UsedByText;
        Error = null;
        IsEditing = true;
        ShowPasscode();
        if (IsAprsIs)
        {
            _ = ShowServersAsync(row.Source.Server);
        }
        if (IsSerial)
        {
            _ = RefreshSerialPortsAsync();
        }
        if (IsKissSerial)
        {
            _ = ShowTncTypesAsync(row.Source.TncType);
        }
    }

    /// <summary>Saves the form: a new source, or the edited one in its place. A name defaults to the server's host.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsTnc)
        {
            await SaveKissAsync();
            return;
        }
        var server = EditServer.Trim();
        var name = EditName.Trim().Length > 0 ? EditName.Trim() : SourceSettings.NameFor(server);
        var filter = EditFilter.Trim().Length > 0 ? EditFilter.Trim() : null;
        var source = _editingId is { } id
            ? _settings.SourceList.Single(s => s.Id == id) with
            {
                Name = name,
                Server = server,
                Filter = filter,
                RxOnly = EditRxOnly,
            }
            : SourceSettings.AprsIsFor(server, filter) with
            {
                Name = name,
                RxOnly = EditRxOnly,
            };
        var sources = _editingId is null
            ? [.. _settings.SourceList, source]
            : _settings.SourceList.Select(s => s.Id == source.Id ? source : s).ToList();
        int? passcode = null;
        if (ShowsPasscode)
        {
            if (!EditHasPasscode)
            {
                passcode = -1;
            }
            else if (
                int.TryParse(EditPasscode.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number <= 32767
            )
            {
                passcode = number;
            }
            else
            {
                Error = _text["Wizard.Passcode.Invalid"];
                return;
            }
        }
        // Ticked profiles use it as their one APRS-IS source (F1.25 behavior 3); the active one keeps the passcode.
        var ticked = EditProfiles.Where(p => p.IsChecked).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var aprsIsIds = sources
            .Where(s => s.Kind == SourceKind.AprsIs)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);
        var profiles = _settings
            .Profiles.Select(p =>
            {
                var changed = ticked.Contains(p.Id)
                    ? p with
                    {
                        SourceIds = [.. p.SourceIds.Where(id => !aprsIsIds.Contains(id)), source.Id],
                    }
                    : p with
                    {
                        SourceIds = [.. p.SourceIds.Where(id => id != source.Id)],
                    };
                return passcode is { } code && p.Id == _settings.ActiveProfileId
                    ? changed with
                    {
                        Passcode = code,
                    }
                    : changed;
            })
            .ToList();
        if (await SaveSettingsAsync(_settings with { Sources = sources, Profiles = profiles }))
        {
            IsEditing = false;
        }
    }

    /// <summary>
    /// Saves a KISS source, over TCP or on a serial port, and ticks or unticks it on each profile as the form says (F3.1
    /// behaviors 1, 2). A number that isn't one is passed on as out of range, so the engine's check words it.
    /// </summary>
    private async Task SaveKissAsync()
    {
        int? tncPort = EditTncPort.Trim().Length == 0 ? null : Number(EditTncPort);
        var txPath = EditTxPath.Trim().Length > 0 ? EditTxPath.Trim().ToUpperInvariant() : null;
        SourceSettings source;
        if (IsGps)
        {
            source = GpsSourceFromForm();
        }
        else if (IsSerial)
        {
            var serialPort = EditSerialPort.Trim();
            int? baud = EditBaud.Trim().Length == 0 ? null : Number(EditBaud);
            var name = EditName.Trim().Length > 0 ? EditName.Trim() : serialPort;
            var custom = IsKissSerial && IsCustomTnc;
            source = (
                _editingId is { } id ? _settings.SourceList.Single(s => s.Id == id)
                : IsKissSerial ? SourceSettings.KissSerialFor(serialPort)
                : SourceSettings.AprsTextSerialFor(serialPort)
            ) with
            {
                Name = name,
                SerialPort = serialPort,
                Baud = baud,
                TncPort = IsKissSerial ? tncPort : null,
                DtrEnable = EditDtr ? null : false,
                RtsEnable = EditRts ? null : false,
                RxOnly = !CanTransmitKind || EditRxOnly,
                TxPath = IsKissSerial ? txPath : null,
                TncType = IsKissSerial ? SelectedTncType?.Id : null,
                EnterScript = custom ? EditEnterScript.Trim() : null,
                LeaveScript = custom && EditLeaveScript.Trim().Length > 0 ? EditLeaveScript.Trim() : null,
            };
        }
        else
        {
            var host = EditHost.Trim();
            int? port = EditPort.Trim().Length == 0 ? null : Number(EditPort);
            var name = EditName.Trim().Length > 0 ? EditName.Trim() : host;
            source = _editingId is { } id
                ? _settings.SourceList.Single(s => s.Id == id) with
                {
                    Name = name,
                    Host = host,
                    Port = port,
                    TncPort = tncPort,
                    RxOnly = EditRxOnly,
                    TxPath = txPath,
                }
                : SourceSettings.KissTcpFor(host, port, tncPort) with
                {
                    Name = name,
                    RxOnly = EditRxOnly,
                    TxPath = txPath,
                };
        }
        var ticked = EditProfiles.Where(p => p.IsChecked).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var settings = _settings with
        {
            Sources = _editingId is null
                ? [.. _settings.SourceList, source]
                : [.. _settings.SourceList.Select(s => s.Id == source.Id ? source : s)],
            Profiles =
            [
                .. _settings.Profiles.Select(p =>
                    ticked.Contains(p.Id) == p.SourceIds.Contains(source.Id)
                        ? p
                        : p with
                        {
                            SourceIds = ticked.Contains(p.Id)
                                ? [.. p.SourceIds, source.Id]
                                : [.. p.SourceIds.Where(s => s != source.Id)],
                        }
                ),
            ],
        };
        if (await SaveSettingsAsync(settings))
        {
            IsEditing = false;
        }

        static int Number(string text) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    /// <summary>The GPS the form describes (docs/features/F3.6-gps.md, behavior 1), new or the one being edited.</summary>
    private SourceSettings GpsSourceFromForm()
    {
        var serial = EditGpsLink == GpsLink.Serial;
        var where = serial ? EditSerialPort.Trim() : EditHost.Trim();
        int? number = (serial ? EditBaud : EditPort).Trim() is { Length: > 0 } typed
            ? int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : -1
            : null;
        var start =
            _editingId is { } id ? _settings.SourceList.Single(s => s.Id == id)
            : serial ? SourceSettings.GpsSerialFor(where)
            : SourceSettings.GpsNetworkFor(EditGpsLink, where);
        return start with
        {
            Name = EditName.Trim().Length > 0 ? EditName.Trim() : where,
            GpsLink = EditGpsLink,
            SerialPort = serial ? where : null,
            Baud = serial ? number : null,
            Host = serial ? null : where,
            Port = serial ? null : number,
            RxOnly = true, // a GPS never sends (behavior 3)
        };
    }

    private void FillProfiles(Func<string, bool> ticked)
    {
        EditProfiles.Clear();
        foreach (var profile in _settings.Profiles)
        {
            EditProfiles.Add(new ProfileChoice(profile.Id, profile.Name) { IsChecked = ticked(profile.Id) });
        }
    }

    /// <summary>Closes the form without saving.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsEditing = false;
        Error = null;
    }

    /// <summary>
    /// Test send on <paramref name="row"/>'s source (docs/features/F2.1-own-station.md, behavior 9): says it's sending
    /// and listening, then who repeated it, that it wasn't heard back, or why it couldn't be sent.
    /// </summary>
    internal async Task TestSendAsync(SourceRow row)
    {
        row.TestResult = _text["Settings.Sources.TestSending"];
        row.OffersTransmitOn = false;
        try
        {
            var result = await _engine.TestSendAsync(row.Source.Id);
            row.TestResult = result.HeardThrough is { } through
                ? _text.Format(
                    result.ByIgate ? "Settings.Sources.TestGated" : "Settings.Sources.TestRepeated",
                    new Dictionary<string, object?> { ["station"] = through }
                )
                : _text["Settings.Sources.TestNotHeard"];
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            row.TestResult = _text.Describe(ex);
            row.OffersTransmitOn = ex is IHasEngineError { EngineError.Code: EngineErrorCodes.TransmitOff };
        }
    }

    /// <summary>
    /// Turn Transmit on, beside Test send's "Transmit is off" (F2.1 behavior 9): the same switch as the status bar's, so
    /// the engine's checks apply and a refusal says why.
    /// </summary>
    internal async Task TurnTransmitOnAsync(SourceRow row)
    {
        try
        {
            await _engine.SetToggleAsync(Toggle.Transmit, true);
            row.OffersTransmitOn = false;
            row.TestResult = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            row.TestResult = _text.Describe(ex);
            row.OffersTransmitOn = false;
        }
    }

    /// <summary>Deletes at once when no profile uses it; otherwise asks first (behavior 2).</summary>
    internal async Task DeleteAsync(SourceRow row)
    {
        var users = UsersOf(row.Source);
        if (users.Count == 0)
        {
            await RemoveAsync(row.Source);
            return;
        }
        PendingDeleteText = _text.Format(
            "Settings.Sources.ConfirmDelete",
            new Dictionary<string, object?> { ["source"] = row.Name, ["profiles"] = string.Join(", ", users) }
        );
        PendingDelete = row;
    }

    /// <summary>Deletes the source waiting for confirmation and unticks it from its profiles.</summary>
    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is { } row)
        {
            PendingDelete = null;
            await RemoveAsync(row.Source);
        }
    }

    /// <summary>Keeps the source.</summary>
    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    private Task<bool> RemoveAsync(SourceSettings source) =>
        SaveSettingsAsync(
            _settings with
            {
                Sources = [.. _settings.SourceList.Where(s => s.Id != source.Id)],
                Profiles =
                [
                    .. _settings.Profiles.Select(p =>
                        p.Uses(source) ? p with { SourceIds = [.. p.SourceIds.Where(id => id != source.Id)] } : p
                    ),
                ],
            }
        );

    private List<string> UsersOf(SourceSettings source) =>
        [.. _settings.Profiles.Where(p => p.Uses(source)).Select(p => p.Name)];

    // ── The receive-only IGate (docs/features/F3.4-rx-igate.md) ─────────────────────────────────────────────────

    private bool _showingIGate;
    private IGateStatus? _igate;

    /// <summary>"IGate for NZ0P-10", or null without a profile.</summary>
    [ObservableProperty]
    public partial string? IGateHeading { get; private set; }

    /// <summary>Whether the active profile gates what its TNCs hear to APRS-IS; saved as it's switched (behavior 1).</summary>
    [ObservableProperty]
    public partial bool IGateOn { get; set; }

    /// <summary>"12 packets gated, 3 not", while it's on (behavior 6); null otherwise.</summary>
    [ObservableProperty]
    public partial string? IGateCountText { get; private set; }

    /// <summary>Why it gates nothing now (behavior 3), in words; null when it gates.</summary>
    [ObservableProperty]
    public partial string? IGateHeldText { get; private set; }

    partial void OnIGateOnChanged(bool value)
    {
        if (!_showingIGate && _settings.GetActiveProfile() is { } active && active.EffectiveIGate != value)
        {
            _ = SaveSettingsAsync(
                _settings with
                {
                    Profiles =
                    [
                        .. _settings.Profiles.Select(p =>
                            p.Id == active.Id ? p with { IGate = value ? true : null } : p
                        ),
                    ],
                }
            );
        }
    }

    /// <summary>The IGate's counts and hold, from the engine's status.</summary>
    internal void ShowIGate(IGateStatus? igate)
    {
        _igate = igate;
        IGateCountText = igate is null
            ? null
            : _text.Format(
                "IGate.Counts",
                new Dictionary<string, object?> { ["gated"] = igate.Gated, ["refused"] = igate.Refused }
            );
        IGateHeldText = igate?.Held is { } held
            ? _text.Format("IGate.Held", new Dictionary<string, object?> { ["reason"] = _text.Format(held) })
            : null;
    }

    private async Task<bool> SaveSettingsAsync(AppSettings settings)
    {
        Error = null;
        try
        {
            await _engine.SaveSettingsAsync(settings);
            Show(settings);
            _saved(settings);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
            return false;
        }
    }
}

/// <summary>One source in Settings › Sources.</summary>
public sealed partial class SourceRow(SourceSettings source, SourcesSectionViewModel section) : ObservableObject
{
    /// <summary>The source.</summary>
    public SourceSettings Source { get; } = source;

    /// <summary>Its name.</summary>
    public string Name => Source.Name;

    /// <summary>Where it connects, for APRS-IS.</summary>
    public string? Server => Source.Server ?? Source.KissEndpoint ?? Source.SerialEndpoint;

    /// <summary>Its filter text, when it has one.</summary>
    public string? Filter => Source.Filter;

    /// <summary>"APRS-IS".</summary>
    [ObservableProperty]
    public partial string KindText { get; set; } = string.Empty;

    /// <summary>"Used by: Home, Portable", or "Not used by any profile".</summary>
    [ObservableProperty]
    public partial string UsedByText { get; set; } = string.Empty;

    /// <summary>Edit's accessible name.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    /// <summary>Delete's accessible name.</summary>
    [ObservableProperty]
    public partial string DeleteName { get; set; } = string.Empty;

    [RelayCommand]
    private void Edit() => section.Edit(this);

    [RelayCommand]
    private Task DeleteAsync() => section.DeleteAsync(this);

    /// <summary>"Can send · WIDE1-1,WIDE2-1", or "RX only" (docs/features/F2.1-own-station.md, behavior 5).</summary>
    [ObservableProperty]
    public partial string TransmitText { get; set; } = string.Empty;

    /// <summary>Whether Test send is offered: the source isn't RX only (behavior 9).</summary>
    public bool CanTestSend => Source.MaySend;

    /// <summary>What the last Test send did: sending, heard back through whom, or why it couldn't.</summary>
    [ObservableProperty]
    public partial string? TestResult { get; set; }

    /// <summary>Test send's accessible name: "Test send on Direwolf".</summary>
    [ObservableProperty]
    public partial string TestSendName { get; set; } = string.Empty;

    /// <summary>Sends one status report on this source and listens for it coming back (behavior 9).</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task TestSendAsync() => section.TestSendAsync(this);

    /// <summary>Whether Turn Transmit on shows: the last Test send was refused because Transmit is off.</summary>
    [ObservableProperty]
    public partial bool OffersTransmitOn { get; set; }

    /// <summary>Turns the Transmit switch on (behavior 9).</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task TurnTransmitOnAsync() => section.TurnTransmitOnAsync(this);
}

/// <summary>A server the APRS-IS drop-down offers: a preset's address, or null for Custom….</summary>
/// <param name="Server">The address as <c>host:port</c>, or null for Custom….</param>
/// <param name="Name">How it's listed: the region's name and the address.</param>
public sealed record ServerOption(string? Server, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A kind Add a source offers, with "coming" for those not built yet.</summary>
public sealed partial class SourceKindChoice(
    SourceKind kind,
    bool isAvailable,
    Localizer text,
    SourcesSectionViewModel section
) : ObservableObject
{
    /// <summary>The kind.</summary>
    public SourceKind Kind { get; } = kind;

    /// <summary>Whether it's the kind in the form; picking it switches the form's fields.</summary>
    public bool IsSelected
    {
        get => section.EditKind == Kind;
        set
        {
            if (value && IsAvailable)
            {
                section.EditKind = Kind;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));

    /// <summary>Whether it can be added now.</summary>
    public bool IsAvailable { get; } = isAvailable;

    /// <summary>"APRS-IS", or "KISS over TCP · coming".</summary>
    [ObservableProperty]
    public partial string Name { get; private set; } = Named(kind, isAvailable, text);

    internal void Retranslate(Localizer text) => Name = Named(Kind, IsAvailable, text);

    private static string Named(SourceKind kind, bool available, Localizer text) =>
        available
            ? text[$"Settings.Sources.Kind.{kind}"]
            : text.Format(
                "Settings.Sources.Coming",
                new Dictionary<string, object?> { ["kind"] = text[$"Settings.Sources.Kind.{kind}"] }
            );
}

/// <summary>A TNC type in the serial KISS form (docs/features/F3.2-built-in-tncs.md, behavior 4).</summary>
/// <param name="Id">The id saved as <see cref="SourceSettings.TncType"/>: null for Plain KISS.</param>
/// <param name="Name">"Plain KISS", "Kenwood TH-D72", "Custom".</param>
public sealed record TncTypeChoice(string? Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A profile in the KISS form's "Use with" list (F3.1 behavior 2).</summary>
public sealed partial class ProfileChoice(string id, string name) : ObservableObject
{
    /// <summary>The profile's id.</summary>
    public string Id { get; } = id;

    /// <summary>The profile's name.</summary>
    public string Name { get; } = name;

    /// <summary>Whether the profile uses the source.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}
