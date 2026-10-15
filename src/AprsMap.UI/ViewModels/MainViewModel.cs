// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Localization;
using AprsMap.UI.Map;
using AprsMap.UI.Platform;
using AprsMap.UI.Settings;
using AprsMap.UI.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// The main screen: connection status, and the shell with the map and its panels. Talks only to
/// <see cref="IEngineService"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>How many packets the list keeps.</summary>
    public const int MaxPackets = 200;

    /// <summary>How long to wait before asking the engine again after a call or stream fails.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IEngineService engine;
    private readonly TimeProvider time;
    private readonly Localizer _text;
    private readonly IDeviceLocation? _deviceLocation;
    private readonly AppearanceService _appearance;
    private readonly bool _showPseudoLanguage;
    private readonly Dictionary<string, StationState> _stations = new(StringComparer.Ordinal);
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
        _showPseudoLanguage = showPseudoLanguage;
        _text = text ?? Localizer.Instance;
        Shell = new ShellViewModel(this, _text, managedWindows);
        _text.LanguageChanged += (_, _) => Retranslate();
        Retranslate();
    }

    /// <summary>The product name, from branding.</summary>
    public string Title { get; } = Brand.DisplayName;

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
        Packets.Clear();
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

    /// <summary>Newest first.</summary>
    public ObservableCollection<PacketView> Packets { get; } = [];

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
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
    public partial SettingsViewModel? Settings { get; set; }

    private bool CanOpenSettings() => Settings is null && Setup is null;

    /// <summary>Opens Settings (the gear button, Ctrl+, or Cmd+,): not while the setup wizard is open.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanOpenSettings))]
    internal async Task OpenSettingsAsync()
    {
        var profiles = new ProfilesSectionViewModel(engine, time, _text, OpenProfileEditorAsync, Show);
        var data = new DataSectionViewModel(
            _text,
            SharedStation,
            canResetStation: _settings is { ReadOnly: false },
            ResetAppAsync
        );
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
        var settings = new SettingsViewModel(_appearance, time, _text, _showPseudoLanguage, profiles, data, mapItems);
        settings.Closed += OnSettingsClosed;
        Settings = settings;
        GeocodingInfo? geocoding = null;
        try
        {
            geocoding = await engine.GetGeocodingInfoAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // About shows less without it; nothing else depends on it.
        }
        settings.Show(_status, geocoding);
        if (_settings is { } current)
        {
            await profiles.ShowAsync(current);
        }
    }

    /// <summary>Saves new station aging times (Settings › Map, F1.3 behavior 9); a refusal shows in the error line.</summary>
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
        var wizard = new SetupWizardViewModel(engine, _text, _deviceLocation, mode, settings, profile);
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

    partial void OnSetupChanged(SetupWizardViewModel? value) => OpenSettingsCommand.NotifyCanExecuteChanged();

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
    internal void ShowError(Exception? error)
    {
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
            _engineUnreachable ? _text["Status.EngineUnreachable"]
            : _status is null ? _text["Status.Starting"]
            : ViewModels.ConnectionText.Summary(_text, _status, time.GetUtcNow());
        ConnectionDetails =
            _engineUnreachable || _status is null ? null : ViewModels.ConnectionText.Details(_text, _status);
        IsConnectionWarning = !_engineUnreachable && _status?.Connection == ConnectionState.Refused;
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
        var packets = KeepAsync(WatchPacketsAsync, cancellationToken);
        var stations = KeepAsync(WatchStationsAsync, cancellationToken);
        var status = KeepAsync(PollStatusAsync, cancellationToken);
        var countdown = KeepAsync(CountDownAsync, cancellationToken);
        await Task.WhenAll(packets, stations, status, countdown);
    }

    /// <summary>Adds a packet to the top of the list.</summary>
    internal void Add(PacketView packet)
    {
        Packets.Insert(0, packet);
        while (Packets.Count > MaxPackets)
        {
            Packets.RemoveAt(Packets.Count - 1);
        }
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
        }
        StationCount = _stations.Count;
        StationsChanged?.Invoke(this, batch);
    }

    /// <summary>Every station on the map, by name, as the station stream has them.</summary>
    public IReadOnlyCollection<StationState> Stations => _stations.Values;

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

    /// <summary>Shows freshly loaded settings without treating them as the user's choice.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
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

        if (Map is not null)
        {
            // Home is the active profile's location (behavior 3).
            Map.Home = settings.GetActiveProfile() is { Latitude: { } lat, Longitude: { } lon } ? (lat, lon) : null;
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
            var wizard = new SetupWizardViewModel(engine, _text, _deviceLocation);
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
        PacketCount = status.PacketsReceived;
        _status = status;
        _engineUnreachable = false;
        ShowConnection();
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
                // Any failure talking to the engine is shown and retried, never left unobserved.
                ShowError(ex);
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

    private async Task WatchPacketsAsync(CancellationToken cancellationToken)
    {
        // The engine replays its recent packets on every subscription, so a resubscription replaces the list. Keep
        // the old one on screen until the replay starts.
        var first = true;
        await foreach (var packet in engine.WatchPacketsAsync(cancellationToken))
        {
            if (first)
            {
                Packets.Clear();
                first = false;
            }
            Add(packet);
        }
    }

    private async Task WatchStationsAsync(CancellationToken cancellationToken)
    {
        // The first batches are a full snapshot, so a resubscription starts from scratch once it arrives.
        var first = true;
        await foreach (var batch in engine.WatchStationsAsync(cancellationToken))
        {
            if (first)
            {
                _stations.Clear();
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
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The last APRS-IS state shown is no longer known.
                _engineUnreachable = true;
                ShowConnection();
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
