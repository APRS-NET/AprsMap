// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Localization;
using AprsMap.UI.Map;
using AprsMap.UI.Packets;
using AprsMap.UI.Platform;
using AprsMap.UI.Settings;
using AprsMap.UI.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// The main screen: connection status, and the shell with the map and its panels. Talks only to
/// <see cref="IEngineService"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>How long to wait before asking the engine again after a call or stream fails.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IEngineService engine;
    private readonly TimeProvider time;
    private readonly Localizer _text;
    private readonly IDeviceLocation? _deviceLocation;
    private readonly AppearanceService _appearance;
    private readonly bool _showPseudoLanguage;
    private readonly Dictionary<string, StationState> _stations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TrackPoint>> _tracks = new(StringComparer.Ordinal);
    private AppSettings? _settings;
    private bool _showingSettings;
    private EngineStatus? _status;
    private bool _engineUnreachable;
    private Exception? _error;
    private bool _setupDismissed;
    private bool _openingSetup;

    /// <summary>Creates the view model; its text follows <paramref name="text"/>'s language (the app's by default).</summary>
    public MainViewModel(
        IEngineService engine,
        TimeProvider time,
        Localizer? text = null,
        IDeviceLocation? deviceLocation = null,
        AppearanceService? appearance = null,
        bool showPseudoLanguage = false,
        bool managedWindows = false
    )
    {
        this.engine = engine;
        this.time = time;
        _deviceLocation = deviceLocation;
        _appearance = appearance ?? new AppearanceService();
        _appearance.Changed += (_, _) => OnPropertyChanged(nameof(ShowAllLinks));
        _showPseudoLanguage = showPseudoLanguage;
        _text = text ?? Localizer.Instance;
        SourceNames = new SourceNames(() => _settings);
        PacketList = new PacketListViewModel(engine, time, _text, SourceNames);
        AprsIsSwitch = new SourceSwitch(SwitchAprsIsAsync);
        Details = new Stations.StationDetailsViewModel(
            engine,
            time,
            _text,
            () => _settings?.Units ?? UnitSettings.Metric,
            SourceNames,
            name => _settings is { ReadOnly: false } && _tracks.TryGetValue(name, out var track) && track.Count > 1
        );
        Details.CloseRequested += (_, _) => SelectedStation = null;
        Details.MessageRequested += (_, name) => OpenConversation(name);
        Details.DestinationRequested += (_, d) => _ = Destination.SetAsync(d.Latitude, d.Longitude, d.Name);
        Details.SelectRequested += (_, name) => SelectedStation = name;
        Details.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Details.IsFollowing))
            {
                _ = ReportMapViewAsync(); // following counts at once (F1.2 behavior 3)
            }
        };
        StationList = new Stations.StationListViewModel(
            engine,
            time,
            _text,
            () => _settings?.Units ?? UnitSettings.Metric
        );
        ViewFilter = new Stations.ViewFilterViewModel(engine, _text);
        ViewFilter.Changed += (_, _) => OnViewFilterChanged();
        StationList.Filter = () => ViewFilter.Current;
        Shell = new ShellViewModel(this, _text, managedWindows);
        Layouts = new SavedLayoutsViewModel(Shell, _text);
        Layouts.Saved += OnLayoutSaved;
        Shell.Changed += OnShellChangedForMenus;
        Shell.PanelClosed += OnPanelClosed;
        Dashboard = new Statistics.DashboardViewModel(
            engine,
            time,
            _text,
            () => _status,
            SourceNames,
            source =>
                source.Kind == SourceKind.AprsIs
                    ? AprsIsSwitch
                    : SourcePills.FirstOrDefault(p => p.Id == source.Id)?.Switch,
            name => SelectedStation = name,
            () =>
            {
                PacketList.IssuesOnly = true;
                ShowPanel(PanelKinds.Packets);
            },
            () => _settings?.Units ?? UnitSettings.Metric,
            filter =>
            {
                ViewFilter.Use(filter); // a table row: its View filter, on the map (F1.22 behavior 12)
                Shell.ShowMap();
            }
        );
        _text.LanguageChanged += (_, _) => Retranslate();
        _text.LanguageChanged += (_, _) => _destination?.Retranslate();
        Retranslate();
    }

    /// <summary>The product name, from branding.</summary>
    public string Title { get; } = Brand.DisplayName;

    /// <summary>The gateway's page title beside the product name (F1.26 behavior 10); null for none.</summary>
    [ObservableProperty]
    public partial string? StationTitle { get; private set; }

    /// <summary>Sets the browser tab's title; null where there's no tab.</summary>
    public Action<string>? SetPageTitle { get; init; }

    /// <summary>
    /// Deletes this viewer's own preferences (appearance, layout) and puts their defaults back, for Reset the app (F0.10).
    /// The app supplies it; without it only the in-memory appearance and layout are reset.
    /// </summary>
    public Func<Task>? ClearLocalData { get; init; }

    /// <summary>Whether the station's settings are a gateway's, shared with other viewers (the browser app).</summary>
    public bool SharedStation { get; init; }

    /// <summary>Starts the app afresh after Reset the app, where the platform can (the browser reloads the page).</summary>
    public Action? Restart { get; init; }

    /// <summary>
    /// Reset the app (docs/features/F0.10-start-over.md): deletes the station's settings when
    /// <paramref name="includeStation"/>, then this viewer's own preferences, and goes back to first-run setup.
    /// </summary>
    internal async Task ResetAppAsync(bool includeStation)
    {
        if (includeStation)
        {
            await engine.ResetSettingsAsync();
        }
        if (ClearLocalData is { } clear)
        {
            await clear();
        }
        else
        {
            _appearance.Apply(new AppearanceSettings());
            Shell.Reset();
        }
        Settings?.CloseCommand.Execute(null);
        if (Restart is { } restart)
        {
            restart();
            return;
        }
        _setupDismissed = false;
        _settings = null;
        PacketList.Clear();
        ShowError(null);
        Show(await engine.GetSettingsAsync());
    }

    /// <summary>The map's state (F1.4): tiles, Home and the remembered view; null shows the map's placeholder.</summary>
    public MapViewModel? Map { get; init; }

    /// <summary>The main window's screen, for Gather windows; set on the desktop only (F1.0 behavior 14).</summary>
    public Func<ScreenArea?>? MainScreen { get; init; }

    /// <summary>Whether the app menu offers Gather floating windows: the desktop, where panels float as windows.</summary>
    public bool CanGatherWindows => MainScreen is not null;

    /// <summary>The app menu's entries for closed panels: each kind with its title (F1.0 behavior 4).</summary>
    public IReadOnlyList<(string Kind, string Title)> ClosedPanels =>
        [.. Shell.ClosedPanels.Select(kind => (kind, _text[PanelKinds.TitleKey(kind)]))];

    /// <summary>Shows a closed panel again (app menu).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    internal void ShowPanel(string kind) => Shell.Show(kind);

    /// <summary>Puts the default layout back (app menu, F1.0 behavior 10).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    internal void ResetLayout() => Shell.Reset();

    /// <summary>Moves every floating window onto the main window's screen (app menu, F1.0 behavior 14).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    internal void GatherWindows()
    {
        if (MainScreen?.Invoke() is { } screen)
        {
            Shell.Gather(screen);
        }
    }

    /// <summary>The map and the panels around it (docs/features/F1.0-shell-and-docking.md).</summary>
    public ShellViewModel Shell { get; }

    /// <summary>Saved layouts, and the question when the monitors change (docs/features/F1.23-saved-layouts.md).</summary>
    public SavedLayoutsViewModel Layouts { get; }

    /// <summary>Save layout as… in the app menu: Settings › Layout, where the name is typed (F1.23 behavior 1).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    internal async Task OpenLayoutSettingsAsync()
    {
        await OpenSettingsAsync();
        Settings?.ShowLayout();
    }

    /// <summary>Said once when the saved layout couldn't be read and the default is shown (F1.0 behavior 10); or null.</summary>
    [ObservableProperty]
    public partial string? LayoutNotice { get; set; }

    /// <summary>Restores the saved layout, fitted to <paramref name="screens"/>, and says so if it couldn't be read.</summary>
    public LayoutRestore RestoreLayout(string? saved, IReadOnlyList<ScreenArea> screens)
    {
        var result = Shell.Restore(saved, screens);
        LayoutNotice = result == LayoutRestore.Unreadable ? _text["Shell.LayoutUnreadable"] : null;
        return result;
    }

    /// <summary>Hides <see cref="LayoutNotice"/>.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    internal void DismissLayoutNotice() => LayoutNotice = null;

    /// <summary>The Packets panel (F1.8).</summary>
    public PacketListViewModel PacketList { get; }

    /// <summary>The dashboard tab (F1.22).</summary>
    public Statistics.DashboardViewModel Dashboard { get; }

    /// <summary>The Messages panel (docs/features/F2.4-messaging.md).</summary>
    public Messages.MessagesViewModel Messages =>
        _messagesPanel ??= new Messages.MessagesViewModel(engine, _text, time, () => _status);

    private Messages.MessagesViewModel? _messagesPanel;

    /// <summary>Opens the Messages panel on the conversation with <paramref name="correspondent"/> (behavior 3).</summary>
    internal void OpenConversation(string correspondent)
    {
        Messages.Open(correspondent);
        ShowPanel(PanelKinds.Messages);
    }

    /// <summary>"2 new messages" in the status bar while any are unread (behavior 4); null otherwise.</summary>
    [ObservableProperty]
    public partial string? UnreadText { get; private set; }

    /// <summary>My destination, in the map's chip and as a line on the map (docs/features/F1.18-destination.md).</summary>
    public Map.DestinationViewModel Destination =>
        _destination ??= new Map.DestinationViewModel(engine, _text, () => _settings?.Units ?? UnitSettings.Metric);

    private Map.DestinationViewModel? _destination;

    /// <summary>The Source console panel (docs/features/F1.28-source-console.md).</summary>
    public Traffic.SourceConsoleViewModel Console => _consolePanel ??= CreateConsole();

    private Traffic.SourceConsoleViewModel? _consolePanel;

    private Traffic.SourceConsoleViewModel CreateConsole()
    {
        var console = new Traffic.SourceConsoleViewModel(engine, _text, time, () => _status);
        if (_status?.Sources is { } sources)
        {
            console.ShowSources(sources);
        }
        return console;
    }

    /// <summary>
    /// Opens the Source console on a source, from its pill (F1.28 behavior 1), with the command line open for TNC
    /// commands (docs/features/F3.2-built-in-tncs.md, behavior 8).
    /// </summary>
    internal async Task OpenConsoleAsync(string sourceId, bool commandLine)
    {
        ShowPanel(PanelKinds.Console);
        await Console.OpenAsync(sourceId, commandLine);
    }

    /// <summary>"Capturing unexpected packets: 37 today" while capture is on (F1.27 behavior 6); null otherwise.</summary>
    [ObservableProperty]
    public partial string? CaptureText { get; private set; }

    /// <summary>The Captured packets panel (docs/features/F1.27-packet-capture.md, behavior 7).</summary>
    public Capture.CapturedPacketsViewModel CapturedPackets =>
        _capturedPanel ??= new Capture.CapturedPacketsViewModel(engine, _text, time, OpenStationPackets);

    private Capture.CapturedPacketsViewModel? _capturedPanel;

    /// <summary>Opens the Captured packets panel, from the status line (F1.27 behavior 6).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task OpenCapturedPacketsAsync()
    {
        ShowPanel(PanelKinds.Captured);
        await CapturedPackets.LoadAsync();
    }

    /// <summary>Open in Packets: that station's packets in the Packets panel (F1.27 behavior 7).</summary>
    private void OpenStationPackets(string station)
    {
        PacketList.OnlyStation(station);
        ShowPanel(PanelKinds.Packets);
    }

    /// <summary>Opens the Source console on the APRS-IS source, from its pill (F1.28 behavior 1).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task OpenAprsIsConsoleAsync() =>
        _status?.Sources?.FirstOrDefault(s => s.Kind == SourceKind.AprsIs) is { } aprsIs
            ? OpenConsoleAsync(aprsIs.Id, commandLine: false)
            : Task.CompletedTask;

    /// <summary>The Bulletins panel (docs/features/F2.5-bulletins.md).</summary>
    public Bulletins.BulletinsViewModel Bulletins =>
        _bulletinsPanel ??= new Bulletins.BulletinsViewModel(engine, _text, time, () => _status);

    private Bulletins.BulletinsViewModel? _bulletinsPanel;

    /// <summary>Opens the Messages panel, from the unread marker.</summary>
    [RelayCommand]
    private void ShowMessages() => ShowPanel(PanelKinds.Messages);

    /// <summary>The packet list's rows, newest first.</summary>
    public PacketRows Packets => PacketList.Rows;

    [ObservableProperty]
    public partial string ConnectionText { get; set; } = string.Empty;

    /// <summary>Whether the login is verified and how often it reconnected, for the connection's tooltip; or null.</summary>
    [ObservableProperty]
    public partial string? ConnectionDetails { get; set; }

    /// <summary>Whether the connection needs the user (a refused login), so it shows in the warning colour.</summary>
    [ObservableProperty]
    public partial bool IsConnectionWarning { get; set; }

    /// <summary>Whether Reconnect now is offered: there is a connection to restart, and this engine accepts it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    public partial bool CanReconnect { get; set; }

    /// <summary>Closes the APRS-IS connection and connects again at once (F1.1 behavior 7).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanReconnect))]
    internal async Task ReconnectAsync()
    {
        try
        {
            await engine.ReconnectAsync();
            ShowError(null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }

    [ObservableProperty]
    public partial long PacketCount { get; set; }

    [ObservableProperty]
    public partial int StationCount { get; set; }

    /// <summary>"12 packets", in the viewer's language.</summary>
    [ObservableProperty]
    public partial string PacketCountText { get; set; } = string.Empty;

    /// <summary>"3 stations", in the viewer's language.</summary>
    [ObservableProperty]
    public partial string StationCountText { get; set; } = string.Empty;

    /// <summary>The station profiles, to switch between; empty before first-run setup.</summary>
    public ObservableCollection<StationProfile> Profiles { get; } = [];

    /// <summary>The active profile. Choosing another one switches the engine to it (and reconnects).</summary>
    [ObservableProperty]
    public partial StationProfile? SelectedProfile { get; set; }

    /// <summary>Whether there is more than one profile and this engine accepts the switch.</summary>
    [ObservableProperty]
    public partial bool CanSwitchProfiles { get; set; }

    /// <summary>The first-run wizard while it is open, or null (docs/features/F0.5-first-run-wizard.md).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupOpen))]
    public partial SetupWizardViewModel? Setup { get; set; }

    /// <summary>Whether the wizard covers the main view.</summary>
    public bool IsSetupOpen => Setup is not null;

    /// <summary>The Settings page while it is open, or null (docs/features/F0.6-appearance.md).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand), nameof(OpenClearDataCommand))]
    public partial SettingsViewModel? Settings { get; set; }

    private bool CanOpenSettings() => Settings is null && Setup is null;

    /// <summary>Opens Settings (the gear button, Ctrl+, or Cmd+,): not while the setup wizard is open.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanOpenSettings))]
    internal async Task OpenSettingsAsync()
    {
        // A viewer who can never act here sees only their own choices (F1.26 behavior 9).
        var profiles = IsViewerOnly
            ? null
            : new ProfilesSectionViewModel(engine, time, _text, OpenProfileEditorAsync, Show);
        var data = new DataSectionViewModel(
            _text,
            SharedStation,
            canResetStation: _settings is { ReadOnly: false },
            ResetAppAsync
        )
        {
            Clear = _settings is { ReadOnly: false } ? new ClearDataViewModel(engine, _text) : null,
            Capture = _settings is { ReadOnly: false }
                ? new Capture.CaptureSettingsViewModel(engine, _text, Show)
                : null,
        };
        if (_settings is { } shown)
        {
            data.Capture?.Show(shown);
        }
        var mapItems = Map is null
            ? null
            : SettingsRegistry.Map(
                _appearance,
                _text,
                Map.Providers,
                _settings is { ReadOnly: false }
                    ? (() => _settings?.EffectiveAging ?? StationAging.Default, SaveAging)
                    : null
            );
        var sources = IsViewerOnly ? null : new SourcesSectionViewModel(engine, _text, Show);
        var beacons = IsViewerOnly ? null : new BeaconsSectionViewModel(engine, _text, Show);
        var unitItems = _settings is { ReadOnly: false }
            ? SettingsRegistry.Units(_text, () => _settings?.Units ?? UnitSettings.Metric, SaveUnits)
            : null;
        var settings = new SettingsViewModel(
            _appearance,
            time,
            _text,
            _showPseudoLanguage,
            profiles,
            data,
            mapItems,
            sources,
            unitItems,
            beacons,
            Layouts
        );
        settings.Closed += OnSettingsClosed;
        Settings = settings;
        settings.Show(_status);
        if (_settings is { } current)
        {
            sources?.Show(current);
            beacons?.Show(current);
            if (profiles is not null)
            {
                await profiles.ShowAsync(current);
            }
        }
    }

    /// <summary>Saves new station aging times (Settings › Map, F1.3 behavior 9); a refusal shows in the error line.</summary>
    /// <summary>Saves the station's units (F1.10 behavior 6) and shows them at once.</summary>
    private async void SaveUnits(UnitSettings units)
    {
        if (_settings is not { } settings)
        {
            return;
        }
        var changed = settings with { Units = units };
        try
        {
            await engine.SaveSettingsAsync(changed);
            Show(changed);
            ShowError(null);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }

    private async void SaveAging(StationAging aging)
    {
        if (_settings is not { } settings)
        {
            return;
        }
        var changed = settings with { Aging = aging };
        try
        {
            await engine.SaveSettingsAsync(changed);
            _settings = changed;
            ShowError(null);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }

    /// <summary>Opens the setup wizard over Settings to add or edit a profile (F0.8 behaviors 5–6).</summary>
    internal async Task OpenProfileEditorAsync(WizardMode mode, StationProfile? profile, AppSettings settings)
    {
        if (Setup is not null)
        {
            return;
        }
        var wizard = new SetupWizardViewModel(engine, _text, _deviceLocation, mode, settings, profile) { Map = Map };
        try
        {
            await wizard.LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError(ex);
            return;
        }
        wizard.Closed += OnSetupClosed;
        Setup = wizard;
    }

    private void OnSettingsClosed(object? sender, EventArgs e)
    {
        if (Settings is { } settings)
        {
            settings.Closed -= OnSettingsClosed;
            settings.Dispose();
        }
        Settings = null;
    }

    partial void OnSetupChanged(SetupWizardViewModel? value)
    {
        OpenSettingsCommand.NotifyCanExecuteChanged();
        OpenClearDataCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The main menu's Clear data: Settings › Data, where Clear is (docs/features/F1.19-housekeeping.md).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanOpenSettings))]
    internal Task OpenClearDataAsync()
    {
        OpenClearData(); // a dialog of its own now (F1.29 behavior 9)
        return Task.CompletedTask;
    }

    /// <summary>Whether this viewer may clear the station's data, for the main menu.</summary>
    public bool CanClearData => _settings is { ReadOnly: false };

    /// <summary>
    /// The map's Clear stations question, with how many there are now (docs/features/F1.19-housekeeping.md, behavior
    /// 1); set as the question opens.
    /// </summary>
    [ObservableProperty]
    public partial string? ClearStationsQuestion { get; private set; }

    /// <summary>Words the question for the stations held now.</summary>
    public void AskClearStations() =>
        ClearStationsQuestion = _text.Format(
            "Map.ClearStations.Question",
            new Dictionary<string, object?> { ["count"] = _status?.Stations ?? StationCount }
        );

    /// <summary>
    /// Clears every station, object and item, with their tracks, for every viewer (F1.19), from the map's button. They
    /// come back as they're heard again.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task ClearStationsAsync()
    {
        try
        {
            await engine.ClearAsync(ClearScope.Stations);
            ShowError(null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }

    private async Task ReloadSettingsAsync()
    {
        try
        {
            var settings = await engine.GetSettingsAsync();
            Show(settings);
            if (Settings?.Profiles is { } profiles)
            {
                await profiles.ShowAsync(settings);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError(ex);
        }
    }

    /// <summary>Whether the status bar offers to open the wizard again: no profile, and the user closed it (behavior 3).</summary>
    [ObservableProperty]
    public partial bool CanOpenSetup { get; set; }

    /// <summary>Whether to say that the gateway's operator sets the first callsign (behavior 2).</summary>
    [ObservableProperty]
    public partial bool ShowGatewaySetupNote { get; set; }

    /// <summary>The latest error talking to the engine (for example, the gateway is unreachable), or null.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>Shows <paramref name="error"/> in the viewer's language, or clears it.</summary>
    /// <summary>
    /// The gateway now runs a newer app than this page (F1.14 behavior 8): the main view offers Reload instead of
    /// failing calls.
    /// </summary>
    [ObservableProperty]
    public partial bool IsOutdated { get; private set; }

    /// <summary>The engine can't be reached just now (the gateway restarting, say): the panels are shown faded.</summary>
    [ObservableProperty]
    public partial bool IsEngineAway { get; private set; }

    /// <summary>Shows the engine as unreachable until the next status answers, for tests of the faded panels.</summary>
    internal void MarkEngineAway()
    {
        _engineUnreachable = true;
        ShowConnection();
    }

    /// <summary>Connected to APRS-IS: the pill is shown in the OK colours.</summary>
    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    /// <summary>Connected without a verified login: the pill carries the receive-only symbol.</summary>
    [ObservableProperty]
    public partial bool IsReceiveOnly { get; private set; }

    /// <summary>Reloads the page for the new version (the browser app).</summary>
    [RelayCommand]
    private void Reload() => Restart?.Invoke();

    internal void ShowError(Exception? error)
    {
        if (error is IHasEngineError { EngineError.Code: EngineErrorCodes.GatewayOutdated })
        {
            IsOutdated = true;
        }
        _error = error;
        ErrorText = error is null ? null : _text.Describe(error);
    }

    partial void OnPacketCountChanged(long value) =>
        PacketCountText = _text.Format("Main.PacketCount", new Dictionary<string, object?> { ["count"] = value });

    partial void OnStationCountChanged(int value) =>
        StationCountText = _text.Format("Main.StationCount", new Dictionary<string, object?> { ["count"] = value });

    /// <summary>Rebuilds every text from its source, for the current language.</summary>
    private void Retranslate()
    {
        OnPacketCountChanged(PacketCount);
        OnStationCountChanged(StationCount);
        ShowConnection();
        if (_error is not null)
        {
            ShowError(_error);
        }
    }

    private void ShowConnection()
    {
        ConnectionText =
            _engineUnreachable ? _text[SharedStation ? "Status.GatewayReconnecting" : "Status.EngineUnreachable"]
            : _status is null ? _text["Status.Starting"]
            : ViewModels.ConnectionText.Summary(_text, _status, time.GetUtcNow());
        ConnectionDetails =
            _engineUnreachable || _status is null ? null : ViewModels.ConnectionText.Details(_text, _status);
        IsConnectionWarning = !_engineUnreachable && _status?.Connection == ConnectionState.Refused;
        IsConnected = !_engineUnreachable && _status?.Connection == ConnectionState.Connected;
        IsReceiveOnly = IsConnected && _status is { Verified: false };
        IsEngineAway = _engineUnreachable;
        ShowTransmit();
        CanReconnect =
            !_engineUnreachable
            && _status is { Connection: not (ConnectionState.NotConfigured or ConnectionState.Disconnected) }
            && _settings is { ReadOnly: false };
    }

    /// <summary>
    /// Starts watching packets and stations and polling status, until cancelled. A failure (the gateway restarted, say)
    /// shows in <see cref="ErrorText"/>; failed calls and failed or ended streams are retried. Awaits on the caller's
    /// context, so call it from the UI thread and the collections are only touched there.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Map is not null)
        {
            await Map.LoadAsync(cancellationToken);
            Map.UseProvider(_appearance.Current.MapProvider);
            _appearance.Changed += (_, settings) => Map.UseProvider(settings.MapProvider);
        }
        await ViewFilter.LoadAsync(Preferences);
        Destination.Preferences = Preferences;
        await Destination.LoadAsync();
        await StationList.LoadAsync();
        await LoadAuthAsync(); // signed in as the gateway's operator? (F2.9)
        await LoadCloseNoteAsync();
        var packets = KeepAsync(PacketList.WatchAsync, cancellationToken);
        var stations = KeepAsync(WatchStationsAsync, cancellationToken);
        var status = KeepAsync(PollStatusAsync, cancellationToken);
        var countdown = KeepAsync(CountDownAsync, cancellationToken);
        var messages = KeepAsync(Messages.WatchAsync, cancellationToken);
        Bulletins.Preferences = Preferences;
        await Bulletins.LoadAsync();
        var bulletins = KeepAsync(Bulletins.WatchAsync, cancellationToken);
        await Task.WhenAll(packets, stations, status, countdown, messages, bulletins);
    }

    /// <summary>Applies one batch from the station stream.</summary>
    internal void Apply(StationBatch batch)
    {
        foreach (var station in batch.Changed)
        {
            _stations[station.Name] = station;
        }
        foreach (var name in batch.Removed)
        {
            _stations.Remove(name);
            _tracks.Remove(name);
        }
        foreach (var update in batch.Tracks ?? [])
        {
            if (!_tracks.TryGetValue(update.Name, out var points))
            {
                _tracks[update.Name] = points = [];
            }
            UI.Map.TrackUpdates.Apply(points, update, p => p);
            if (points.Count == 0)
            {
                _tracks.Remove(update.Name);
            }
        }
        StationCount = _stations.Count;
        ViewFilter.ShowCounts(_stations.Count, _status?.Stations ?? _stations.Count);
        if (
            _routeQuery is { Path: null, TrackPoint: null } following
            && batch.Changed.Any(s => s.Name == following.Name)
        )
        {
            _ = ShowRouteAsync(following); // the station's last packet changed
        }
        StationsChanged?.Invoke(this, batch);
        _destination?.StationsChanged(batch);
        Details.Changed(batch);
        StationList.Changed();
    }

    /// <summary>Every station on the map, by name, as the station stream has them.</summary>
    public IReadOnlyCollection<StationState> Stations => _stations.Values;

    /// <summary>Every station's track (F1.5), oldest point first, as the station stream has them.</summary>
    public IEnumerable<KeyValuePair<string, IReadOnlyList<TrackPoint>>> Tracks =>
        _tracks.Select(t => KeyValuePair.Create(t.Key, (IReadOnlyList<TrackPoint>)t.Value));

    /// <summary>
    /// Raised after each batch from the station stream (about every 250 ms at most), with the batch; after a fresh
    /// snapshot (a resubscription) with <see cref="Snapshot"/> set.
    /// </summary>
    public event EventHandler<StationBatch>? StationsChanged;

    /// <summary>Sent with <see cref="StationsChanged"/> when the stations were replaced as a whole.</summary>
    public static StationBatch Snapshot { get; } = new([], []);

    /// <summary>The station selected on the map or in a list, for the Details panel (F1.6); null for none.</summary>
    [ObservableProperty]
    public partial string? SelectedStation { get; set; }

    /// <summary>The Details panel (F1.6): the selected station, in full.</summary>
    public Stations.StationDetailsViewModel Details { get; }

    partial void OnSelectedStationChanged(string? value)
    {
        _ = Details.ShowAsync(value);
        _ = ShowRouteAsync(value is null ? null : new RouteQuery(value));
        if (value is not null)
        {
            LastSelectedStation = value;
        }
    }

    /// <summary>
    /// The route drawn for the selection (docs/features/F1.21-rf-paths.md, behavior 2): the selected station's last
    /// packet, a packet picked in the list, or a track point's; null for none.
    /// </summary>
    [ObservableProperty]
    public partial PacketRoute? Route { get; private set; }

    /// <summary>
    /// The route the map draws: a packet picked in the list or a track point's (F1.21 behavior 2). A selected station
    /// shows its RF links instead (behavior 5), so its last packet's route stays in the details.
    /// </summary>
    [ObservableProperty]
    public partial PacketRoute? PickedRoute { get; private set; }

    /// <summary>
    /// The selected station's last position's routes out, when it's this station (docs/features/F3.3-rf-paths.md,
    /// behavior 3); empty otherwise.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<PacketRoute> RoutesOut { get; private set; } = [];

    private RouteQuery? _routeQuery;
    private int _routeLoads;

    /// <summary>
    /// Whether every RF link is drawn, not only the selected station's (F1.21 behavior 5): the map's button toggles the
    /// same viewer setting as Settings › Map.
    /// </summary>
    public bool ShowAllLinks
    {
        get => _appearance.Current.ShowAllLinks;
        set
        {
            if (value != ShowAllLinks)
            {
                _appearance.Apply(_appearance.Current with { ShowAllLinks = value });
            }
        }
    }

    /// <summary>The RF network's links over the last hour (F1.21 behavior 4); empty when the engine can't be reached.</summary>
    internal async Task<IReadOnlyList<NetworkLink>> LoadNetworkLinksAsync()
    {
        try
        {
            return await engine.GetNetworkLinksAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return [];
        }
    }

    /// <summary>A packet picked in the packet list: its station is selected and that packet's route shown.</summary>
    public void SelectPacket(PacketView packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Source.Length == 0)
        {
            return;
        }
        SelectedStation = packet.Source;
        _ = ShowRouteAsync(
            new RouteQuery(
                packet.Source,
                packet.Path ?? string.Empty,
                packet.Latitude,
                packet.Longitude,
                HeardHere: packet.Markers.HasFlag(PacketMarkers.HeardHere)
            )
        );
    }

    /// <summary>A point of the selected station's track picked on the map: that packet's route (F1.5 behavior 6).</summary>
    public void SelectTrackPoint(string name, DateTimeOffset time)
    {
        SelectedStation = name;
        _ = ShowRouteAsync(new RouteQuery(name, TrackPoint: time));
    }

    private async Task ShowRouteAsync(RouteQuery? query)
    {
        _routeQuery = query;
        var load = ++_routeLoads;
        PacketRoute? route = null;
        if (query is not null)
        {
            try
            {
                route = await engine.GetRouteAsync(query);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                route = null; // the engine went away; the next selection asks again
            }
        }
        IReadOnlyList<PacketRoute> routesOut = [];
        if (query is { Path: null, TrackPoint: null })
        {
            try
            {
                // Only this station's routes come back, named for it: another station selected gets none.
                routesOut = [.. (await engine.GetRoutesOutAsync()).Where(r => r.Name == query.Name)];
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                routesOut = [];
            }
        }
        if (load == _routeLoads)
        {
            Route = route;
            RoutesOut = routesOut;
            Details.ShowRoutesOut(routesOut);
            PickedRoute = query is { Path: null, TrackPoint: null } ? null : route;
            Details.ShowRoute(route);
        }
    }

    /// <summary>Sources by name, for the lists and details (F1.25).</summary>
    public SourceNames SourceNames { get; }

    /// <summary>
    /// A pill per source besides APRS-IS (F1.25 behavior 11): its state in words and colour, a switch to turn it off
    /// and on, and Reconnect now (F3.1 behaviors 5, 6); empty with only APRS-IS.
    /// </summary>
    public ObservableCollection<SourcePill> SourcePills { get; } = [];

    private void ShowSourcePills(EngineStatus status)
    {
        _consolePanel?.ShowSources(status.Sources ?? []);
        var sources = (status.Sources ?? []).Where(s => s.Kind != SourceKind.AprsIs).ToList();
        for (var i = SourcePills.Count - 1; i >= 0; i--)
        {
            if (!sources.Any(s => s.Id == SourcePills[i].Id))
            {
                SourcePills.RemoveAt(i);
            }
        }
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var pill = SourcePills.FirstOrDefault(p => p.Id == source.Id);
            if (pill is null)
            {
                pill = new SourcePill(
                    source.Id,
                    _text,
                    on => SwitchSourceAsync(source.Id, on),
                    () => ReconnectSourceAsync(source.Id),
                    () => SendPositionOnAsync(source.Id),
                    commandLine => OpenConsoleAsync(source.Id, commandLine)
                );
                SourcePills.Insert(Math.Min(i, SourcePills.Count), pill);
                pill.Switch.IsShown = !IsViewerOnly;
            }
            pill.Show(source, CanTransmit, TransmitOn && CanTransmit);
        }
    }

    /// <summary>A source's switch was flipped (F3.1 behavior 6); the pill shows what the engine settles on.</summary>
    private async Task SwitchSourceAsync(string id, bool on)
    {
        try
        {
            await engine.SetSourceEnabledAsync(id, on);
            if (_status is { Sources: { } sources })
            {
                // Shown at once, not at the next status poll.
                _status = _status with
                {
                    Sources =
                    [
                        .. sources.Select(s =>
                            s.Id == id ? s with { Enabled = on, State = ConnectionState.Disconnected, Error = null } : s
                        ),
                    ],
                };
            }
            ShowError(null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowError(ex);
        }
        if (_status is not null)
        {
            ShowSourcePills(_status); // springs back if the engine refused
        }
    }

    /// <summary>Reconnect now on a source's pill (F3.1 behavior 5).</summary>
    private async Task ReconnectSourceAsync(string id)
    {
        try
        {
            await engine.ReconnectSourceAsync(id);
            ShowError(null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }

    /// <summary>How long after the map stops moving its view is reported for the filter (F1.2 behavior 4).</summary>
    public static readonly TimeSpan MapReportDelay = TimeSpan.FromSeconds(2);

    private (double Latitude, double Longitude, double RadiusKm)? _mapView;
    private int _mapMoves;

    /// <summary>The APRS-IS filter's range parts, for the map's filter circles (F1.2 behavior 6).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<FilterArea>? FilterAreas { get; private set; }

    /// <summary>The map moved: its view is reported <see cref="MapReportDelay"/> after it stops (F1.2 behavior 4).</summary>
    internal void MapMoved(double latitude, double longitude, double radiusKm)
    {
        _mapView = (latitude, longitude, radiusKm);
        _ = ReportAfterAsync(++_mapMoves);
    }

    private async Task ReportAfterAsync(int move)
    {
        await Task.Delay(MapReportDelay, time);
        if (move == _mapMoves)
        {
            await ReportMapViewAsync(); // still: no move since
        }
    }

    /// <summary>Tells the engine what the map shows and follows; it decides whether the filter changes.</summary>
    private async Task ReportMapViewAsync()
    {
        var following = Details.IsFollowing ? Details.Name : null;
        if (_mapView is null && following is null)
        {
            return;
        }
        var view = new MapViewReport(_mapView?.Latitude, _mapView?.Longitude, _mapView?.RadiusKm ?? 0, following);
        try
        {
            await engine.SetMapViewAsync(view);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // The gateway is away; the next move reports again.
        }
    }

    /// <summary>The Internet master switch (F1.15).</summary>
    [ObservableProperty]
    public partial bool InternetOn { get; private set; } = true;

    /// <summary>APRS-IS (F1.15).</summary>
    [ObservableProperty]
    public partial bool AprsIsOn { get; private set; } = true;

    /// <summary>Map tile downloads (F1.15).</summary>
    [ObservableProperty]
    public partial bool TilesOn { get; private set; } = true;

    /// <summary>Whether this viewer may change the toggles (F1.15 behavior 6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleDependents))]
    public partial bool CanToggle { get; private set; }

    /// <summary>Whether APRS-IS and Map tiles can be changed: Internet is on (behavior 2).</summary>
    public bool CanToggleDependents => CanToggle && InternetOn;

    /// <summary>"Internet off", "APRS-IS off": a marker in the status bar per toggle that is off (behavior 4).</summary>
    public ObservableCollection<string> ToggleMarkers { get; } = [];

    /// <summary>Said to screen readers when a toggle changes (behavior 5).</summary>
    [ObservableProperty]
    public partial string? ToggleAnnouncement { get; private set; }

    /// <summary>
    /// The APRS-IS on/off switch on the connection pill and the dashboard's Connection tile: the same toggle as the
    /// connection menu's (F1.15), shared so every switch moves together.
    /// </summary>
    public SourceSwitch AprsIsSwitch { get; }

    /// <summary>A switch was flipped to <paramref name="on"/>; whatever the engine says, the switch shows after.</summary>
    private async Task SwitchAprsIsAsync(bool on)
    {
        if (on != AprsIsOn)
        {
            await ToggleAsync(Toggle.AprsIs);
        }
        AprsIsSwitch.Show(AprsIsOn, CanToggleDependents); // springs back if the engine refused
    }

    partial void OnAprsIsOnChanged(bool value) => AprsIsSwitch.Show(value, CanToggleDependents);

    partial void OnInternetOnChanged(bool value) => AprsIsSwitch.Show(AprsIsOn, CanToggleDependents);

    partial void OnCanToggleChanged(bool value)
    {
        AprsIsSwitch.Show(AprsIsOn, CanToggleDependents);
        ShowTransmit();
        ShowViewerOnly();
    }

    /// <summary>Turns <paramref name="toggle"/> the other way (F1.15).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task ToggleAsync(Toggle toggle)
    {
        var on = toggle switch
        {
            Toggle.Internet => !InternetOn,
            Toggle.AprsIs => !AprsIsOn,
            Toggle.Transmit => !TransmitOn,
            _ => !TilesOn,
        };
        try
        {
            await engine.SetToggleAsync(toggle, on);
            var toggles = (_status?.Toggles ?? StationToggles.AllOn) switch
            {
                var t when toggle == Toggle.Internet => t with { Internet = on },
                var t when toggle == Toggle.AprsIs => t with { AprsIs = on },
                var t when toggle == Toggle.Transmit => t with { Transmit = on },
                var t => t with { Tiles = on },
            };
            ShowToggles(toggles);
            if (_status is not null)
            {
                _status = _status with { Toggles = toggles };
                ShowConnection(); // the pill says "APRS-IS off" now, not at the next status poll
            }
            ToggleAnnouncement = _text.Format(
                "Toggles.Announce",
                new Dictionary<string, object?> { ["name"] = _text[$"Toggles.{toggle}"], ["on"] = on ? "yes" : "no" }
            );
            ShowError(null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowError(ex);
        }
    }

    private void ShowToggles(StationToggles toggles)
    {
        InternetOn = toggles.Internet;
        AprsIsOn = toggles.AprsIs;
        TilesOn = toggles.Tiles;
        OnPropertyChanged(nameof(CanToggleDependents));
        var markers = new List<string>();
        if (!toggles.Internet)
        {
            markers.Add(_text["Toggles.Marker.Internet"]);
        }
        // APRS-IS off shows in its own pill ("APRS-IS off", with its switch), not as another marker.
        if (!toggles.Tiles)
        {
            markers.Add(_text["Toggles.Marker.Tiles"]);
        }
        if (!markers.SequenceEqual(ToggleMarkers))
        {
            ToggleMarkers.Clear();
            foreach (var marker in markers)
            {
                ToggleMarkers.Add(marker);
            }
        }
    }

    /// <summary>The View filter (F1.9), for the map and the station list.</summary>
    public Stations.ViewFilterViewModel ViewFilter { get; }

    /// <summary>The Stations panel (F1.7).</summary>
    public Stations.StationListViewModel StationList { get; }

    /// <summary>Where this viewer's own preferences live (the station list's sort); null keeps them for this run.</summary>
    public IPreferenceStore? Preferences
    {
        get => StationList.Preferences;
        init => StationList.Preferences = value;
    }

    /// <summary>The station selected most recently, for Go to (F1.7 behavior 9) after the selection is cleared.</summary>
    public string? LastSelectedStation { get; private set; }

    /// <summary>Raised to centre the map on a station by name (F1.7 behaviors 7 and 9).</summary>
    public event EventHandler<string>? CentreOnStationRequested;

    /// <summary>Raised by <c>/</c> or Ctrl+F: the station search takes focus (F1.7 behavior 3).</summary>
    public event EventHandler? FocusStationSearchRequested;

    /// <summary>Centres the map on <paramref name="name"/> (Enter or double-click in the list).</summary>
    internal void CentreOn(string name) => CentreOnStationRequested?.Invoke(this, name);

    /// <summary>Puts the keyboard in the station search.</summary>
    internal void FocusStationSearch() => FocusStationSearchRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows freshly loaded settings without treating them as the user's choice.</summary>
    internal void Show(AppSettings settings)
    {
        var couldClear = CanClearData;
        _settings = settings;
        PacketList.ShowSources(settings);
        Settings?.Beacons?.Show(settings); // a source's TX enabled changes what beacons can go out on
        Settings?.Data?.Capture?.Show(settings);
        if (_destination is { IsSet: true } destination)
        {
            _ = destination.RefreshAsync(); // my location or the units may have changed
        }
        CanToggle = !settings.ReadOnly;
        if (CanClearData != couldClear)
        {
            OnPropertyChanged(nameof(CanClearData));
        }
        _showingSettings = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in settings.Profiles)
            {
                Profiles.Add(profile);
            }
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId);
            CanSwitchProfiles = !settings.ReadOnly && Profiles.Count > 1;
        }
        finally
        {
            _showingSettings = false;
        }

        var active = settings.GetActiveProfile();
        CanSetMyLocation = active is not null && !settings.ReadOnly;
        // Sources and profiles each show the other's names (F1.25 behavior 2): both follow a change from either.
        if (Settings is { } open && !ReferenceEquals(open.Sources?.Current, settings))
        {
            open.Sources?.Show(settings);
            if (open.Profiles is { } profilesSection && !ReferenceEquals(profilesSection.Current, settings))
            {
                _ = profilesSection.ShowAsync(settings);
            }
        }
        if (Map is not null)
        {
            // Home is the active profile's location (behavior 3), drawn as a house with its callsign (F1.13).
            Map.Home = active is { Latitude: { } lat, Longitude: { } lon } ? (lat, lon) : null;
            Map.HomeName = active?.Callsign;
            Map.DistanceUnit = settings.Units.Distance;
            Map.Units = settings.Units;
        }
        var notSetUp = settings.Profiles.Count == 0;
        ShowGatewaySetupNote = notSetUp && settings.ReadOnly;
        CanOpenSetup = notSetUp && !settings.ReadOnly && _setupDismissed && Setup is null;
        if (notSetUp && !settings.ReadOnly && !_setupDismissed)
        {
            _ = OpenSetupAsync();
        }
        ShowConnection(); // whether Reconnect now is offered depends on the settings being read-only
    }

    /// <summary>Opens the first-run wizard (behavior 1), or again from the status bar (behavior 3).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    internal async Task OpenSetupAsync()
    {
        if (Setup is not null || _openingSetup)
        {
            return;
        }
        _openingSetup = true;
        try
        {
            var wizard = new SetupWizardViewModel(engine, _text, _deviceLocation) { Map = Map };
            await wizard.LoadAsync();
            wizard.Closed += OnSetupClosed;
            Setup = wizard;
            CanOpenSetup = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError(ex);
        }
        finally
        {
            _openingSetup = false;
        }
    }

    private void OnSetupClosed(object? sender, bool saved)
    {
        var mode = Setup?.Mode ?? WizardMode.FirstRun;
        if (Setup is { } wizard)
        {
            wizard.Closed -= OnSetupClosed;
        }
        Setup = null;
        if (mode != WizardMode.FirstRun)
        {
            // Added or edited from Settings: show the result there and in the status bar.
            if (saved)
            {
                _ = ReloadSettingsAsync();
            }
            return;
        }
        _setupDismissed = !saved;
        CanOpenSetup = !saved && _settings is { Profiles.Count: 0, ReadOnly: false };
        if (saved)
        {
            _settings = null; // the next status poll loads the new profile
        }
    }

    partial void OnSelectedProfileChanged(StationProfile? value)
    {
        if (!_showingSettings && value is not null && _settings is { } settings && value.Id != settings.ActiveProfileId)
        {
            _ = SwitchProfileAsync(settings, value);
        }
    }

    /// <summary>Asks the engine to use <paramref name="profile"/>; on failure, shows why and the profile still in use.</summary>
    internal async Task SwitchProfileAsync(AppSettings settings, StationProfile profile)
    {
        var switched = settings with { ActiveProfileId = profile.Id };
        try
        {
            await engine.SaveSettingsAsync(switched);
            _settings = switched;
            ShowError(null);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
            Show(settings);
        }
    }

    internal void Show(EngineStatus status)
    {
        if (status.Title != StationTitle)
        {
            StationTitle = status.Title;
            SetPageTitle?.Invoke(status.Title is { } title ? $"{title} · {Brand.DisplayName}" : Brand.DisplayName);
        }
        FilterAreas = status.FilterAreas;
        Details.Refresh(); // "heard 4 minutes ago" moves on with the status poll
        StationList.Rewrite();
        PacketCount = status.PacketsReceived;
        if (_status is { } before && status.PacketHistory != before.PacketHistory)
        {
            PacketList.Clear(); // someone cleared the packet history (F1.19 behavior 3)
        }
        _status = status;
        CaptureText = status.CapturedToday is { } captured
            ? _text.Format("Capture.Status", new Dictionary<string, object?> { ["count"] = captured })
            : null;
        ShowToggles(status.Toggles ?? StationToggles.AllOn);
        _engineUnreachable = false;
        ShowConnection();
        ShowSourcePills(status); // after the transmit state, which their Send position now follows
        UnreadText =
            status.UnreadMessages > 0
                ? _text.Format(
                    "Messages.UnreadMarker",
                    new Dictionary<string, object?> { ["count"] = status.UnreadMessages }
                )
                : null;
    }

    /// <summary>Runs <paramref name="run"/> until cancelled, retrying after failures and after the stream ends.</summary>
    private async Task KeepAsync(Func<CancellationToken, Task> run, CancellationToken cancellationToken)
    {
        // Leaves only through cancellation: of the run, or of the wait before the next one.
        while (true)
        {
            try
            {
                await run(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Any failure talking to the engine is shown and retried, never left unobserved. In the browser a lost
                // connection is the status bar's "Reconnecting to the gateway…", not the transport's own message
                // (F1.14 behavior 7); the engine's own errors are still shown.
                if (SharedStation && ex is not IHasEngineError)
                {
                    _engineUnreachable = true;
                    ShowConnection();
                }
                else
                {
                    ShowError(ex);
                }
            }

            try
            {
                await Task.Delay(RetryDelay, time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private CancellationTokenSource? _stationsRestart;

    /// <summary>The View filter changed (F1.9): the station stream starts again with it, and the list asks again.</summary>
    private void OnViewFilterChanged()
    {
        _stationsRestart?.Cancel();
        _ = StationList.RefreshAsync();
        ViewFilter.ShowCounts(_stations.Count, _status?.Stations ?? _stations.Count);
    }

    private async Task WatchStationsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var restart = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stationsRestart = restart;
            try
            {
                await WatchStationsOnceAsync(restart.Token);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The View filter changed: a new stream with a new snapshot.
            }
            finally
            {
                _stationsRestart = null;
            }
        }
    }

    private async Task WatchStationsOnceAsync(CancellationToken cancellationToken)
    {
        // The first batches are a full snapshot, so a resubscription starts from scratch once it arrives.
        var first = true;
        await foreach (var batch in engine.WatchStationsAsync(ViewFilter.Current, cancellationToken))
        {
            if (first)
            {
                _stations.Clear();
                _tracks.Clear();
                StationsChanged?.Invoke(this, Snapshot);
                first = false;
            }
            Apply(batch);
        }
    }

    /// <summary>Refreshes the reconnect countdown every second; status polls are less frequent.</summary>
    private async Task CountDownAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (ViewModels.ConnectionText.IsCountingDown(_status) && !_engineUnreachable)
            {
                ShowConnection();
            }
        }
    }

    private async Task PollStatusAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        do
        {
            EngineStatus status;
            try
            {
                status = await engine.GetStatusAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The last APRS-IS state shown is no longer known.
                _engineUnreachable = true;
                ShowConnection();
                if (ex is IHasEngineError { EngineError.Code: EngineErrorCodes.GatewayOutdated })
                {
                    IsOutdated = true;
                }
                throw;
            }
            Show(status);
            ShowError(null);
            if (_settings is null || status.ProfileId != _settings.ActiveProfileId)
            {
                // First poll, or the profile changed elsewhere (another window, another browser).
                Show(await engine.GetSettingsAsync(cancellationToken));
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
