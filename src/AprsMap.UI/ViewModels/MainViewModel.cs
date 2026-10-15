// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.ViewModels;

/// <summary>The main screen: connection status and the live packet list. Talks only to <see cref="IEngineService"/>.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>How many packets the list keeps.</summary>
    public const int MaxPackets = 200;

    /// <summary>How long to wait before asking the engine again after a call or stream fails.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IEngineService engine;
    private readonly TimeProvider time;
    private readonly Localizer _text;
    private readonly Dictionary<string, StationState> _stations = new(StringComparer.Ordinal);
    private AppSettings? _settings;
    private bool _showingSettings;
    private EngineStatus? _status;
    private bool _engineUnreachable;
    private Exception? _error;
    private bool _setupDismissed;
    private bool _openingSetup;

    /// <summary>Creates the view model; its text follows <paramref name="text"/>'s language (the app's by default).</summary>
    public MainViewModel(IEngineService engine, TimeProvider time, Localizer? text = null)
    {
        this.engine = engine;
        this.time = time;
        _text = text ?? Localizer.Instance;
        _text.LanguageChanged += (_, _) => Retranslate();
        Retranslate();
    }

    /// <summary>The product name, from branding.</summary>
    public string Title { get; } = Brand.DisplayName;

    /// <summary>Newest first.</summary>
    public ObservableCollection<PacketView> Packets { get; } = [];

    [ObservableProperty]
    public partial string ConnectionText { get; set; } = string.Empty;

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

    private void ShowConnection() =>
        ConnectionText =
            _engineUnreachable ? _text["Status.EngineUnreachable"]
            : _status is null ? _text["Status.Starting"]
            : _status.Connection switch
            {
                ConnectionState.Connected => _text.Format(
                    "Status.Connected",
                    new Dictionary<string, object?> { ["server"] = _status.Server ?? string.Empty }
                ),
                ConnectionState.Connecting => _text["Status.Connecting"],
                ConnectionState.Faulted => _text["Status.Faulted"],
                ConnectionState.NotConfigured => _text["Status.NotConfigured"],
                _ => _text["Status.Disconnected"],
            };

    /// <summary>
    /// Starts watching packets and stations and polling status, until cancelled. A failure (the gateway restarted, say)
    /// shows in <see cref="ErrorText"/>; failed calls and failed or ended streams are retried. Awaits on the caller's
    /// context, so call it from the UI thread and the collections are only touched there.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var packets = KeepAsync(WatchPacketsAsync, cancellationToken);
        var stations = KeepAsync(WatchStationsAsync, cancellationToken);
        var status = KeepAsync(PollStatusAsync, cancellationToken);
        await Task.WhenAll(packets, stations, status);
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
    }

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

        var notSetUp = settings.Profiles.Count == 0;
        ShowGatewaySetupNote = notSetUp && settings.ReadOnly;
        CanOpenSetup = notSetUp && !settings.ReadOnly && _setupDismissed && Setup is null;
        if (notSetUp && !settings.ReadOnly && !_setupDismissed)
        {
            _ = OpenSetupAsync();
        }
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
            var wizard = new SetupWizardViewModel(engine, _text);
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
        if (Setup is { } wizard)
        {
            wizard.Closed -= OnSetupClosed;
        }
        Setup = null;
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
                first = false;
            }
            Apply(batch);
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
