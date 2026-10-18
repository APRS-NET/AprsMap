// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsNet.AprsIsTcpClient;
using AprsNet.Kiss;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AprsMap.Core;

/// <summary>
/// The in-process engine. <see cref="RunAsync"/> keeps an APRS-IS connection open, reads every packet with APRS.NET,
/// remembers recent packets and fans them out to every <see cref="WatchPacketsAsync"/> caller, and keeps the latest
/// position of every station for <see cref="WatchStationsAsync"/>. It logs in with the active station profile from
/// <see cref="ISettingsStore"/>, waits while there is none, and reconnects when the profile changes.
/// </summary>
public sealed partial class Engine : IEngineService, IDisposable, IMessengerHost, IBeaconHost
{
    private readonly EngineOptions _options;
    private readonly IPacketSourceFactory _sources;
    private readonly ISettingsStore _settingsStore;
    private readonly IGeocoder _geocoder;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly TimeProvider _time;
    private readonly ILogger<Engine> _logger;
    private readonly Lock _gate = new();
    private readonly LinkedList<PacketView> _recent = new();

    // The packets listed in the last duplicate window, by identity, so a copy heard on another source adds to their
    // Seen on (F1.25 behavior 9). Oldest first, for expiry.
    private readonly Dictionary<PacketIdentity, LinkedListNode<PacketView>> _listed = [];

    // Seen on for a first copy another source is still taking in: applied when that copy is listed.
    private readonly Dictionary<PacketIdentity, (DateTimeOffset At, List<string> Sources)> _seenEarly = [];
    private readonly Queue<(DateTimeOffset At, PacketIdentity Identity, LinkedListNode<PacketView> Node)> _listedOrder =
        new();

    // The non-APRS-IS sources open, with their connection state (F1.25 behavior 11), by source id.
    private readonly Dictionary<string, (SourceSettings Source, Link Link)> _sourceLinks = new(StringComparer.Ordinal);
    private TaskCompletionSource _settingsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(Channel<PacketView> Channel, PacketFilter? Filter)> _watchers = [];
    private readonly StationStore _stations;
    private readonly DuplicateFilter _duplicates;
    private readonly DashboardCounters _dashboard;

    // Each open source's current attempt, so Reconnect now can end it (F3.1 behavior 5). Only touched under the lock.
    private readonly Dictionary<string, CancellationTokenSource> _sourceAttempts = new(StringComparer.Ordinal);

    // The map views reported for the filter (F1.2), by viewer, most recently moved last.
    private readonly List<(string Viewer, MapViewReport View)> _views = [];
    private string? _filter;
    private IReadOnlyList<FilterArea> _filterAreas = [];
    private DateTimeOffset _filterSentAt = DateTimeOffset.MinValue;
    private bool _filterScheduled;
    private Link _link = new(ConnectionState.Disconnected);
    private int _reconnects;
    private IPacketSource? _source;
    private long _packets;
    private DateTimeOffset? _lastSent;

    // The open connections of the other sources that can send (KISS TNCs), by source id (F2.1 behavior 5).
    private readonly Dictionary<string, IPacketSender> _senders = new(StringComparer.Ordinal);
    private readonly List<EchoWatch> _echoes = [];
    private readonly Messenger _messenger;
    private readonly Beaconer _beaconer;
    private int _packetHistory;
    private AppSettings? _settings;

    // Cancelled (and replaced) when the active profile's login changes or a reconnect is asked for, to end the current
    // session or wait.
    private CancellationTokenSource _restart = new();

    /// <summary>Creates the engine.</summary>
    public Engine(
        IOptions<EngineOptions> options,
        IPacketSourceFactory sources,
        ISettingsStore settingsStore,
        TimeProvider time,
        ILogger<Engine> logger,
        IGeocoder? geocoder = null,
        IMessageStore? messages = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _stations = new StationStore(_options.MaxStations);
        _sources = sources;
        _settingsStore = settingsStore;
        _geocoder = geocoder ?? NoGeocoder.Instance;
        _time = time;
        _duplicates = new DuplicateFilter(timeProvider: time);
        _dashboard = new DashboardCounters(time.GetUtcNow());
        _logger = logger;
        _messenger = new Messenger(this, messages ?? new InMemoryMessageStore(), time);
        _beaconer = new Beaconer(this, time);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _messenger.Dispose();
        _beaconer.Dispose();
        _settingsLock.Dispose();
        lock (_gate)
        {
            _restart.Dispose();
        }
    }

    /// <summary>The APRS.NET packages' version, without build metadata.</summary>
    internal static readonly string AprsNetVersion = (
        typeof(AprsPacket).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AprsPacket).Assembly.GetName().Version?.ToString()
        ?? "?"
    ).Split('+')[0];

    /// <inheritdoc />
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // Outside the gate: the messenger and the beaconer ask the engine things under their own locks.
        var unread = _messenger.Unread;
        var (nextBeacon, beaconHeld) = _beaconer.Status;
        lock (_gate)
        {
            var profile = _settings?.GetActiveProfile();
            return Task.FromResult(
                new EngineStatus(
                    _link.State,
                    _link.Server,
                    _packets,
                    Brand.Version,
                    profile?.Id,
                    profile?.Callsign,
                    AprsNetVersion,
                    _link.Verified,
                    _link.RetryAt,
                    _reconnects,
                    _link.Error,
                    Filter: _filter,
                    FilterAreas: _filterAreas,
                    Sources: SourceStatuses(),
                    PacketHistory: _packetHistory,
                    Stations: _stations.Count,
                    Toggles: (_settings ?? AppSettings.Empty).EffectiveToggles,
                    Transmit: new TransmitStatus(
                        (_settings ?? AppSettings.Empty).EffectiveToggles.Transmit,
                        TransmitBlock(_settings ?? AppSettings.Empty),
                        _lastSent,
                        _options.ReceiveOnly,
                        nextBeacon,
                        beaconHeld
                    ),
                    UnreadMessages: unread
                )
            );
        }
    }

    /// <inheritdoc />
    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        SaveAsync(settings, trusted: false, cancellationToken);

    /// <summary>
    /// Validates and stores <paramref name="settings"/>. Unless <paramref name="trusted"/> (the engine's own change),
    /// saving never turns Transmit on or marks a position confirmed (docs/features/F2.1-own-station.md, behaviors 4,
    /// 12): those keep what they were, and a change to what a profile sends clears its confirmation. Switching the
    /// active profile turns Transmit off (behavior 7).
    /// </summary>
    private async Task SaveAsync(AppSettings settings, bool trusted, CancellationToken cancellationToken)
    {
        SettingsValidator.Validate(settings, _time);
        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var old = await LoadSettingsLockedAsync(cancellationToken).ConfigureAwait(false);
            var saved = trusted ? settings with { ReadOnly = false } : KeepTransmitState(settings, old);
            var before = Station.Of(old);
            await _settingsStore.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _settings = saved;
            }
            MeasureFrom(saved);
            await UseMessengerAsync(saved, cancellationToken).ConfigureAwait(false);
            SignalSettingsChanged();

            var after = Station.Of(saved);
            if (old.EffectiveToggles.ConnectsAprsIs != saved.EffectiveToggles.ConnectsAprsIs)
            {
                LogToggled(saved.EffectiveToggles.ConnectsAprsIs ? "on" : "off");
                SignalRestart(); // APRS-IS turned on or off (F1.15 behavior 1)
            }
            else if (after is null ? before is not null : after.NeedsNewLogin(before))
            {
                LogProfileChanged(after?.Name ?? "(none)");
                SignalRestart();
            }
            else if (after is not null && after.NeedsNewFilter(before))
            {
                await SendFilterAsync(after, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private static AppSettings KeepTransmitState(AppSettings settings, AppSettings old)
    {
        var oldProfiles = old.Profiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var profiles = settings
            .Profiles.Select(p =>
                p with
                {
                    PositionConfirmed =
                        oldProfiles.TryGetValue(p.Id, out var was)
                        && was.PositionConfirmed
                        && !p.ChangesWhatIsSent(was),
                }
            )
            .ToList();
        var transmit =
            settings.EffectiveToggles.Transmit
            && old.EffectiveToggles.Transmit
            && settings.ActiveProfileId == old.ActiveProfileId;
        return settings with
        {
            ReadOnly = false,
            Profiles = profiles,
            Toggles = settings.EffectiveToggles with { Transmit = transmit },
        };
    }

    /// <inheritdoc />
    public async Task ResetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _settingsStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _settings = AppSettings.Empty; // not the configured defaults: the point is to see first-run setup
                _recent.Clear();
                _listed.Clear();
                _listedOrder.Clear();
                _seenEarly.Clear();
                _packets = 0;
            }
            _stations.Clear();
            MeasureFrom(AppSettings.Empty);
            await UseMessengerAsync(AppSettings.Empty, cancellationToken).ConfigureAwait(false);
            SignalSettingsChanged();
            LogReset();
            SignalRestart();
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    /// <inheritdoc />
    public Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        LogReconnectRequested();
        SignalRestart();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<GeocodingInfo> GetGeocodingInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_geocoder.Info);

    /// <inheritdoc />
    public Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(
        string query,
        string? language,
        CancellationToken cancellationToken = default
    ) =>
        InternetOn
            ? _geocoder.GeocodeAsync(query, language, cancellationToken)
            : Task.FromException<IReadOnlyList<GeocodeResult>>(
                EngineErrors.ToException(
                    new EngineError(
                        EngineErrorCodes.GeocodingOffline,
                        "The Internet switch is off, so nothing is looked up."
                    )
                )
            );

    /// <inheritdoc />
    public Task<int> ComputePasscodeAsync(string callsign, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        try
        {
            return Task.FromResult(AprsIsPasscode.Compute(callsign.Trim()));
        }
        catch (AprsValidationException ex)
        {
            return Task.FromException<int>(new EngineArgumentException(AprsErrors.From(ex), ex));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServerPreset>> GetServerPresetsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ServerPreset>>([
            .. AprsIsServerPreset.All.Select(p => new ServerPreset(p.Key, p.Name, p.Endpoint.ToString())),
        ]);

    /// <inheritdoc />
    public Task<LocatorInfo> ParseLocatorAsync(string locator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return MaidenheadLocator.TryParse(locator, out var parsed, out var error)
            ? Task.FromResult(new LocatorInfo(parsed.Value, parsed.Center.Latitude, parsed.Center.Longitude))
            : Task.FromException<LocatorInfo>(new EngineArgumentException(AprsErrors.From(error, locator)));
    }

    /// <inheritdoc />
    public Task<string> GetLocatorAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default
    )
    {
        if (latitude is not (>= -90 and <= 90) || longitude is not (>= -180 and <= 180))
        {
            return Task.FromException<string>(
                EngineErrors.Create(EngineErrorCodes.PositionOffGlobe, $"{latitude}, {longitude} is off the globe.")
            );
        }
        return Task.FromResult(MaidenheadLocator.FromPosition(latitude, longitude, 6).Value);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PacketView> WatchPacketsAsync(
        PacketFilter? filter = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        filter = filter is { IsEmpty: false } ? filter : null;
        // Bounded so one slow client can't hold unbounded memory; it loses its oldest packets instead.
        var channel = Channel.CreateBounded<PacketView>(
            new BoundedChannelOptions(_options.RecentPacketCapacity) { FullMode = BoundedChannelFullMode.DropOldest }
        );
        lock (_gate)
        {
            foreach (var packet in _recent)
            {
                if (filter?.Matches(packet) ?? true)
                {
                    channel.Writer.TryWrite(packet);
                }
            }
            _watchers.Add((channel, filter));
        }

        try
        {
            await foreach (var packet in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return packet;
            }
        }
        finally
        {
            lock (_gate)
            {
                _watchers.Remove((channel, filter));
            }
        }
    }

    /// <summary>How many stations the first batches of <see cref="WatchStationsAsync"/> carry each.</summary>
    internal const int SnapshotBatchSize = 1000;

    /// <summary>About how many track points each of the snapshot's track batches carries.</summary>
    internal const int SnapshotTrackPoints = 20_000;

    /// <inheritdoc />
    public async IAsyncEnumerable<StationBatch> WatchStationsAsync(
        StationFilter? filter = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var watcher = _stations.Subscribe(filter, out var snapshot, out var tracks);
        try
        {
            foreach (var chunk in snapshot.Chunk(SnapshotBatchSize))
            {
                yield return new StationBatch(chunk, []);
            }
            // Then the tracks, a batch per SnapshotTrackPoints points or so, after the stations they belong to.
            var points = 0;
            var some = new List<TrackUpdate>();
            foreach (var track in tracks)
            {
                some.Add(track);
                points += track.Added.Count;
                if (points >= SnapshotTrackPoints)
                {
                    yield return new StationBatch([], [], [.. some]);
                    some.Clear();
                    points = 0;
                }
            }
            if (some.Count > 0)
            {
                yield return new StationBatch([], [], [.. some]);
            }

            using var timer = new PeriodicTimer(_options.StationBatchInterval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_stations.Drain(watcher) is { } batch)
                {
                    yield return batch;
                }
            }
        }
        finally
        {
            _stations.Unsubscribe(watcher);
        }
    }

    /// <summary>
    /// Connects with the active profile and reads until <paramref name="cancellationToken"/> is cancelled. Once logged
    /// in, the source reconnects by itself; this loop retries only a first login that couldn't reach the server, with
    /// the same backoff. A refused login isn't retried until the profile changes or <see cref="ReconnectAsync"/> asks.
    /// With no profile it waits for one; a profile change or a reconnect request ends the session and connects at once.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var sweeping = SweepAsync(cancellationToken);
        var others = RunOtherSourcesAsync(cancellationToken);
        try
        {
            await RunConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await others.ConfigureAwait(false);
            await sweeping.ConfigureAwait(false);
        }
    }

    /// <summary>Wakes whatever waits for the settings to change.</summary>
    private void SignalSettingsChanged()
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            changed = _settingsChanged;
            _settingsChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    /// <summary>
    /// Keeps the active profile's other sources open (F1.25 behavior 4): TNCs and the like, beside the APRS-IS session.
    /// A source both the old and the new profile use stays open across a switch; one whose details changed reopens.
    /// </summary>
    private async Task RunOtherSourcesAsync(CancellationToken cancellationToken)
    {
        var running = new Dictionary<string, (SourceSettings Source, CancellationTokenSource Stop, Task Run)>(
            StringComparer.Ordinal
        );
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task changed;
                lock (_gate)
                {
                    changed = _settingsChanged.Task;
                }
                var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
                var profile = settings.GetActiveProfile();
                var wanted = profile is null
                    ? []
                    : settings
                        .SourceList.Where(s =>
                            s.Kind != SourceKind.AprsIs && profile.Uses(s) && settings.EffectiveToggles.IsSourceOn(s.Id) // switched off: stopped (F3.1 behavior 6)
                        )
                        .ToList();
                foreach (var (id, run) in running.ToList())
                {
                    if (!wanted.Contains(run.Source))
                    {
                        await StopAsync(id, run.Stop, run.Run).ConfigureAwait(false);
                        running.Remove(id);
                    }
                }
                foreach (var source in wanted.Where(s => !running.ContainsKey(s.Id)))
                {
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    running[source.Id] = (source, stop, RunSourceAsync(profile!, source, stop.Token));
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            foreach (var (id, run) in running)
            {
                await StopAsync(id, run.Stop, run.Run).ConfigureAwait(false);
            }
        }
    }

    private async Task StopAsync(string sourceId, CancellationTokenSource stop, Task run)
    {
        await stop.CancelAsync().ConfigureAwait(false);
        await run.ConfigureAwait(false);
        stop.Dispose();
        lock (_gate)
        {
            _sourceLinks.Remove(sourceId);
            _sourceAttempts.Remove(sourceId);
        }
    }

    /// <summary>
    /// One non-APRS-IS source, reconnecting with backoff until stopped; a kind not built yet stops at once. Each attempt
    /// has its own token, so Reconnect now (F3.1 behavior 5) can end the connection or the wait and start again at once.
    /// </summary>
    private async Task RunSourceAsync(
        StationProfile profile,
        SourceSettings source,
        CancellationToken cancellationToken
    )
    {
        var delay = _options.InitialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate)
            {
                _sourceAttempts[source.Id] = attempt;
            }
            var kicked = false;
            IPacketSource connection;
            try
            {
                connection = _sources.Create(profile, source, string.Empty);
            }
            catch (NotSupportedException)
            {
                SetSourceLink(
                    source,
                    new Link(
                        ConnectionState.NotConfigured,
                        Error: new EngineError(
                            EngineErrorCodes.SourceKindNotBuilt,
                            "This kind of source isn't built yet."
                        )
                    )
                );
                return;
            }
            await using (connection.ConfigureAwait(false))
            {
                AttachTraffic(source.Id, connection, always: true); // a TNC's traffic is kept from the start (F1.28)
                try
                {
                    SetSourceLink(source, new Link(ConnectionState.Connecting));
                    var opened = await connection
                        .StartAsync(
                            e =>
                                SetSourceLink(
                                    source,
                                    new Link(
                                        e.State,
                                        e.Login?.Server,
                                        RetryAt: e.RetryIn is { } wait ? _time.GetUtcNow() + wait : null,
                                        Error: e.Error,
                                        Warning: e.Warning
                                    )
                                ),
                            attempt.Token
                        )
                        .ConfigureAwait(false);
                    SetSourceLink(
                        source,
                        new Link(ConnectionState.Connected, opened.Server, opened.Verified, Warning: WarningOf(source))
                    );
                    if (connection is IPacketSender sender)
                    {
                        lock (_gate)
                        {
                            _senders[source.Id] = sender;
                        }
                    }
                    delay = _options.InitialRetryDelay;
                    await foreach (var packet in connection.ReadPacketsAsync(attempt.Token).ConfigureAwait(false))
                    {
                        Accept(packet, source.Id);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested)
                {
                    kicked = true; // Reconnect now
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SetSourceLink(
                        source,
                        new Link(
                            ConnectionState.Reconnecting,
                            RetryAt: _time.GetUtcNow() + delay,
                            Error: ex is IHasEngineError coded
                                ? coded.EngineError
                                : new EngineError(EngineErrorCodes.AprsIsLinkFailed, ex.Message)
                        )
                    );
                    LogSourceFailed(ex, source.Name, delay);
                }
                finally
                {
                    lock (_gate)
                    {
                        _senders.Remove(source.Id); // closed: nothing sends on it until it's open again
                    }
                    DetachTraffic(source.Id, connection);
                }
            }
            if (kicked)
            {
                delay = _options.InitialRetryDelay;
                continue;
            }
            try
            {
                await Task.Delay(delay, _time, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                delay = _options.InitialRetryDelay; // Reconnect now, during the wait
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
        }
    }

    /// <inheritdoc />
    public async Task SetSourceEnabledAsync(
        string sourceId,
        bool enabled,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (
            !settings.SourceList.Any(s => s.Id == sourceId)
            || settings.EffectiveToggles.IsSourceOn(sourceId) == enabled
        )
        {
            return;
        }
        // Saved with the toggles; the runner of other sources stops or opens it as the settings change.
        await SaveSettingsAsync(
                settings with
                {
                    Toggles = settings.EffectiveToggles.WithSource(sourceId, enabled),
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task ReconnectSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        CancellationTokenSource? attempt;
        lock (_gate)
        {
            _sourceAttempts.TryGetValue(sourceId, out attempt);
        }
        try
        {
            attempt?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt ended as it was asked to: the next one is on its way.
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetSerialPortsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. KissSerialConnection.GetPortNames().Order(StringComparer.Ordinal)]);

    /// <inheritdoc />
    public Task<IReadOnlyList<TncTypeInfo>> GetTncTypesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TncTypeInfo>>([.. TncSetup.Known.Select(t => new TncTypeInfo(t.Id, t.Name))]);

    private void SetSourceLink(SourceSettings source, Link link)
    {
        lock (_gate)
        {
            _sourceLinks[source.Id] = (source, link);
        }
    }

    /// <summary>A warning the source reported while connected, kept when the engine records the first connection.</summary>
    private EngineError? WarningOf(SourceSettings source)
    {
        lock (_gate)
        {
            return _sourceLinks.TryGetValue(source.Id, out var v) && v.Link.State == ConnectionState.Connected
                ? v.Link.Warning
                : null;
        }
    }

    /// <summary>Each source in use with its state: APRS-IS from the session, the others from their runners.</summary>
    private List<SourceStatus> SourceStatuses()
    {
        var list = new List<SourceStatus>();
        if (Station.Of(_settings) is { } station)
        {
            list.Add(
                new SourceStatus(
                    station.Source.Id,
                    station.Source.Name,
                    SourceKind.AprsIs,
                    _link.State,
                    _link.Server,
                    _link.Error,
                    TransmitEnabled: station.Source.Transmit,
                    TransmitBlocked: station.Source.Transmit ? SourceBlock(_settings!, station.Source) : null
                )
            );
        }
        list.AddRange(
            _sourceLinks.Values.Select(v => new SourceStatus(
                v.Source.Id,
                v.Source.Name,
                v.Source.Kind,
                v.Link.State,
                v.Link.Server,
                v.Link.Error,
                RetryAt: v.Link.RetryAt,
                Warning: v.Link.Warning,
                Manual: IsManual(v.Source.Id),
                TransmitEnabled: v.Source.Transmit,
                TransmitBlocked: v.Source.Transmit && _settings is not null ? SourceBlock(_settings, v.Source) : null
            ))
        );
        // A source switched off stays listed, so its pill can switch it on again (F3.1 behavior 6).
        if (_settings?.GetActiveProfile() is { } profile)
        {
            var toggles = _settings.EffectiveToggles;
            list.AddRange(
                _settings
                    .SourceList.Where(s =>
                        s.Kind != SourceKind.AprsIs
                        && profile.Uses(s)
                        && !toggles.IsSourceOn(s.Id)
                        && !_sourceLinks.ContainsKey(s.Id)
                    )
                    .Select(s => new SourceStatus(
                        s.Id,
                        s.Name,
                        s.Kind,
                        ConnectionState.Disconnected,
                        Enabled: false,
                        TransmitEnabled: s.Transmit,
                        TransmitBlocked: s.Transmit ? SourceBlock(_settings, s) : null
                    ))
            );
        }
        return list;
    }

    /// <inheritdoc />
    public Task<StationDetail?> GetStationAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Task.FromResult(_stations.Get(name));
    }

    /// <inheritdoc />
    public async Task SetToggleAsync(Toggle toggle, bool enabled, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var toggles = settings.EffectiveToggles;
        toggles = toggle switch
        {
            Toggle.Internet => toggles with { Internet = enabled },
            Toggle.AprsIs => toggles with { AprsIs = enabled },
            Toggle.Tiles => toggles with { Tiles = enabled },
            Toggle.Transmit => toggles with { Transmit = enabled },
            _ => throw new ArgumentOutOfRangeException(nameof(toggle), toggle, "Not a toggle."),
        };
        if (toggle == Toggle.Transmit && enabled)
        {
            // Turning it on is the one change that's checked first (F2.1 behavior 5); off always works (behavior 6).
            EngineError? blocked;
            lock (_gate)
            {
                blocked = TransmitBlock(settings);
            }
            if (blocked is not null)
            {
                throw EngineErrors.ToException(blocked);
            }
            LogTransmit("on");
        }
        else if (toggle == Toggle.Transmit)
        {
            LogTransmit("off");
        }
        await SaveAsync(settings with { Toggles = toggles }, trusted: toggle == Toggle.Transmit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Why the station couldn't transmit now, leaving the switch aside (docs/features/F2.1-own-station.md, behavior
    /// 6.1): the engine, the profile, then its sources; null when at least one of them can send. Call under
    /// <see cref="_gate"/>.
    /// </summary>
    private EngineError? TransmitBlock(AppSettings settings)
    {
        if (ProfileBlock(settings) is { } profileBlock)
        {
            return profileBlock;
        }
        var sources = TransmitSources(settings);
        if (sources.Count == 0)
        {
            return new EngineError(
                EngineErrorCodes.TransmitNoSource,
                "None of this profile's sources is TX enabled. Tick TX enabled on one in Settings."
            );
        }
        EngineError? first = null;
        foreach (var source in sources)
        {
            if (SourceBlock(settings, source) is not { } block)
            {
                return null;
            }
            first ??= block;
        }
        return first;
    }

    /// <summary>Why the active profile may not transmit (behavior 4), or null. Call under <see cref="_gate"/>.</summary>
    private EngineError? ProfileBlock(AppSettings settings)
    {
        var profile = settings.GetActiveProfile();
        return _options.ReceiveOnly
                ? new EngineError(
                    EngineErrorCodes.TransmitReceiveOnly,
                    "This station was started receive only, so it doesn't transmit."
                )
            : profile is null || IsPlaceholder(profile.Callsign)
                ? new EngineError(
                    EngineErrorCodes.TransmitPlaceholderCallsign,
                    "N0CALL and NOCALL are placeholders: set your own callsign to transmit."
                )
            : !profile.Licensed || AprsIsLogin.IsListenerId(profile.Callsign)
                ? new EngineError(
                    EngineErrorCodes.TransmitNotLicensed,
                    "This profile isn't confirmed as a licensed amateur's, so it only listens."
                )
            : profile.DisableTransmit
                ? new EngineError(EngineErrorCodes.TransmitProfileDisabled, "This profile has Disable TX ticked.")
            : null;
    }

    /// <summary>The active profile's sources that are TX enabled, in the list's order.</summary>
    private static List<SourceSettings> TransmitSources(AppSettings settings) =>
        settings.GetActiveProfile() is { } profile
            ? [.. settings.SourceList.Where(s => s.Transmit && profile.Uses(s))]
            : [];

    /// <summary>
    /// Why the station couldn't send on <paramref name="source"/> now (behavior 5), or null when it can. Call under
    /// <see cref="_gate"/>.
    /// </summary>
    private EngineError? SourceBlock(AppSettings settings, SourceSettings source)
    {
        var notConnected = new EngineError(
            EngineErrorCodes.TransmitNotConnected,
            source.Kind == SourceKind.AprsIs ? "APRS-IS isn't connected." : $"{source.Name} isn't connected."
        );
        if (!source.Transmit)
        {
            return new EngineError(
                EngineErrorCodes.TransmitSourceDisabled,
                $"{source.Name} isn't TX enabled. Tick TX enabled on it in Settings."
            );
        }
        if (source.Kind == SourceKind.AprsIs)
        {
            return !settings.EffectiveToggles.ConnectsAprsIs || _link.State != ConnectionState.Connected ? notConnected
                : !_link.Verified
                    ? new EngineError(
                        EngineErrorCodes.TransmitNotVerified,
                        "The APRS-IS server didn't verify the login: check the passcode for this callsign."
                    )
                : _source is not IPacketSender ? notConnected
                : null;
        }
        return
            _senders.ContainsKey(source.Id)
            && _sourceLinks.TryGetValue(source.Id, out var link)
            && link.Link.State == ConnectionState.Connected
            ? null
            : notConnected;
    }

    /// <summary>The connection that sends on <paramref name="source"/>. Call under <see cref="_gate"/>.</summary>
    private IPacketSender? SenderFor(SourceSettings source) =>
        source.Kind == SourceKind.AprsIs ? _source as IPacketSender : _senders.GetValueOrDefault(source.Id);

    /// <summary>Whether <paramref name="callsign"/>'s base call is a placeholder, N0CALL or NOCALL (F2.1 behavior 5.2).</summary>
    internal static bool IsPlaceholder(string callsign)
    {
        var dash = callsign.IndexOf('-', StringComparison.Ordinal);
        var call = dash < 0 ? callsign : callsign[..dash];
        return call.Equals("N0CALL", StringComparison.OrdinalIgnoreCase)
            || call.Equals("NOCALL", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The packet for <paramref name="payload"/> from the profile, on <paramref name="source"/>: no path to APRS-IS,
    /// the source's TX path on a TNC (behavior 10.1).
    /// </summary>
    private static AprsPacket Build(StationProfile profile, SourceSettings source, AprsPayload payload) =>
        PacketAddress
            .Parse(
                profile.Callsign,
                Brand.Tocall,
                source.Kind == SourceKind.AprsIs
                    ? []
                    : source.EffectiveTxPath.Split(',', StringSplitOptions.TrimEntries)
            )
            .Create(payload);

    /// <summary>
    /// Checks what every send checks (behaviors 6–8) and returns the sources to send on: <paramref name="sourceId"/>
    /// alone, or every source that can send now. Throws the reason when there's none.
    /// </summary>
    private async Task<(
        AppSettings Settings,
        StationProfile Profile,
        List<(SourceSettings Source, IPacketSender Sender)> Targets
    )> TargetsAsync(string? sourceId, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var targets = new List<(SourceSettings, IPacketSender)>();
        EngineError? blocked;
        lock (_gate)
        {
            blocked = !settings.EffectiveToggles.Transmit
                ? new EngineError(EngineErrorCodes.TransmitOff, "Transmit is off.")
                : ProfileBlock(settings);
            if (blocked is null)
            {
                var profile = settings.GetActiveProfile()!;
                var candidates = sourceId is null
                    ? TransmitSources(settings)
                    : [.. settings.SourceList.Where(s => s.Id == sourceId && profile.Uses(s))];
                if (candidates.Count == 0)
                {
                    blocked = sourceId is null
                        ? new EngineError(
                            EngineErrorCodes.TransmitNoSource,
                            "None of this profile's sources is TX enabled. Tick TX enabled on one in Settings."
                        )
                        : new EngineError(
                            EngineErrorCodes.TransmitSourceDisabled,
                            "That source isn't one this profile uses."
                        );
                }
                foreach (var source in candidates)
                {
                    if (SourceBlock(settings, source) is { } block)
                    {
                        blocked ??= block;
                    }
                    else if (SenderFor(source) is { } sender)
                    {
                        targets.Add((source, sender));
                    }
                }
                if (targets.Count > 0)
                {
                    blocked = null; // the ones that can, send; the others are skipped (behavior 7)
                }
            }
        }
        if (blocked is not null)
        {
            throw EngineErrors.ToException(blocked);
        }
        return (settings, settings.GetActiveProfile()!, targets);
    }

    /// <summary>Sends <paramref name="packet"/> on one source and lists it as ours (behavior 13); null when it failed.</summary>
    private async Task<(PacketView? View, Exception? Error)> SendOnAsync(
        SourceSettings source,
        IPacketSender sender,
        AprsPacket packet,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await sender.SendAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            LogSendFailed(ex, source.Name);
            return (null, ex);
        }
        LogSent(packet, source.Name);
        lock (_gate)
        {
            _lastSent = _time.GetUtcNow();
        }
        return (Accept(packet, source.Id, sent: true), null);
    }

    private static EngineOperationException SendFailed(Exception error) =>
        new(new EngineError(EngineErrorCodes.TransmitFailed, "The packet couldn't be sent: " + error.Message), error);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PacketView>> SendPositionAsync(
        string? sourceId = null,
        CancellationToken cancellationToken = default
    )
    {
        var (_, profile, targets) = await TargetsAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (profile is not { Latitude: not null, Longitude: not null })
        {
            throw EngineErrors.ToException(
                new EngineError(EngineErrorCodes.TransmitNoPosition, "Set the station's location first.")
            );
        }
        var report = Beaconer.PositionReport(profile); // what a beacon sends, PHG and format included (F2.2 behavior 2)
        var sent = new List<PacketView>();
        Exception? failure = null;
        foreach (var (source, sender) in targets)
        {
            var (view, error) = await SendOnAsync(source, sender, Build(profile, source, report), cancellationToken)
                .ConfigureAwait(false);
            if (view is not null)
            {
                sent.Add(view);
            }
            failure ??= error;
        }
        if (sent.Count == 0)
        {
            throw SendFailed(failure!);
        }
        _beaconer.SentByHand(sent.Select(v => v.SourceId!).Where(id => id is not null)); // F2.2 behavior 11

        // The interlock (behavior 12): automatic sends may start once the user has seen this go out.
        if (!profile.PositionConfirmed)
        {
            var now = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (now.GetActiveProfile() is { } current && !current.ChangesWhatIsSent(profile))
            {
                await SaveAsync(
                        now with
                        {
                            Profiles =
                            [
                                .. now.Profiles.Select(p =>
                                    p.Id == current.Id ? p with { PositionConfirmed = true } : p
                                ),
                            ],
                        },
                        trusted: true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        return sent;
    }

    /// <summary>How long Test send listens for its packet coming back (behavior 9.3).</summary>
    internal static readonly TimeSpan TestListenTime = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<TestSendResult> TestSendAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var (_, profile, targets) = await TargetsAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var (source, sender) = targets[0];
        var packet = Build(profile, source, new StatusReport { Text = $"{Brand.DisplayName} test" });

        // Listen before sending, so a fast digipeater isn't missed.
        var heard = new TaskCompletionSource<(string Through, bool ByIgate)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var watch = new EchoWatch(profile.Callsign, packet.Information.ToArray(), heard);
        lock (_gate)
        {
            _echoes.Add(watch);
        }
        try
        {
            var (view, error) = await SendOnAsync(source, sender, packet, cancellationToken).ConfigureAwait(false);
            if (view is null)
            {
                throw SendFailed(error!);
            }
            try
            {
                var (through, byIgate) = await heard
                    .Task.WaitAsync(TestListenTime, _time, cancellationToken)
                    .ConfigureAwait(false);
                return new TestSendResult(view, through, byIgate);
            }
            catch (TimeoutException)
            {
                return new TestSendResult(view);
            }
        }
        finally
        {
            lock (_gate)
            {
                _echoes.Remove(watch);
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Conversation>> GetConversationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_messenger.Conversations());

    /// <inheritdoc />
    public IAsyncEnumerable<ChatMessage> WatchMessagesAsync(CancellationToken cancellationToken = default) =>
        _messenger.WatchAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> SendMessageAsync(
        string correspondent,
        string text,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(correspondent);
        ArgumentNullException.ThrowIfNull(text);
        await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return _messenger.Send(correspondent, text);
    }

    /// <inheritdoc />
    public Task CancelMessageAsync(string id, CancellationToken cancellationToken = default)
    {
        _messenger.Cancel(id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResendMessageAsync(string id, CancellationToken cancellationToken = default)
    {
        _messenger.Resend(id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkReadAsync(string correspondent, CancellationToken cancellationToken = default)
    {
        _messenger.MarkRead(correspondent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetConversationAsync(
        string correspondent,
        SendVia sendVia,
        MessageRetries retries,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(correspondent);
        ArgumentNullException.ThrowIfNull(sendVia);
        _messenger.SetConversation(correspondent, sendVia, retries);
        return Task.CompletedTask;
    }

    /// <summary>Saves the conversations now, for tests.</summary>
    internal Task FlushMessagesAsync() => _messenger.FlushAsync();

    EngineError? IMessengerHost.ProfileBlock()
    {
        lock (_gate)
        {
            return ProfileBlock(_settings ?? AppSettings.Empty);
        }
    }

    bool IMessengerHost.CanSendOn(string sourceId)
    {
        lock (_gate)
        {
            var settings = _settings ?? AppSettings.Empty;
            return settings.EffectiveToggles.Transmit
                && ProfileBlock(settings) is null
                && settings.GetActiveProfile() is { } profile
                && settings.SourceList.FirstOrDefault(s => s.Id == sourceId) is { } source
                && profile.Uses(source)
                && SourceBlock(settings, source) is null;
        }
    }

    IReadOnlyList<(string Id, string Name, bool IsRf)> IMessengerHost.TransmitSources()
    {
        lock (_gate)
        {
            return
            [
                .. TransmitSources(_settings ?? AppSettings.Empty)
                    .Select(s => (s.Id, s.Name, s.Kind != SourceKind.AprsIs)),
            ];
        }
    }

    Task<bool> IMessengerHost.SendAsync(string sourceId, MessagePayload payload, CancellationToken cancellationToken) =>
        SendPayloadAsync(sourceId, payload, cancellationToken);

    (
        StationProfile? Profile,
        EngineError? Held,
        IReadOnlyList<(string Id, bool IsRf)> Sources
    ) IBeaconHost.BeaconState()
    {
        lock (_gate)
        {
            var settings = _settings ?? AppSettings.Empty;
            var profile = settings.GetActiveProfile();
            var held = !settings.EffectiveToggles.Transmit
                ? new EngineError(EngineErrorCodes.TransmitOff, "Transmit is off.")
                : ProfileBlock(settings)
                    ?? (
                        profile is not { Latitude: not null, Longitude: not null }
                            ? new EngineError(EngineErrorCodes.TransmitNoPosition, "Set the station's location first.")
                        : !profile.PositionConfirmed
                            ? new EngineError(
                                EngineErrorCodes.TransmitPositionUnconfirmed,
                                "Send your position once by hand to start beaconing."
                            )
                        : null
                    );
            return (profile, held, [.. TransmitSources(settings).Select(s => (s.Id, s.Kind != SourceKind.AprsIs))]);
        }
    }

    bool IBeaconHost.CanSendOn(string sourceId) => ((IMessengerHost)this).CanSendOn(sourceId);

    Task<bool> IBeaconHost.SendAsync(string sourceId, AprsPayload payload, CancellationToken cancellationToken) =>
        SendPayloadAsync(sourceId, payload, cancellationToken);

    /// <summary>Sends <paramref name="payload"/> from the active profile on <paramref name="sourceId"/>; false when it failed.</summary>
    private async Task<bool> SendPayloadAsync(string sourceId, AprsPayload payload, CancellationToken cancellationToken)
    {
        StationProfile? profile;
        SourceSettings? source;
        IPacketSender? sender;
        lock (_gate)
        {
            profile = _settings?.GetActiveProfile();
            source = _settings?.SourceList.FirstOrDefault(s => s.Id == sourceId);
            sender = source is null ? null : SenderFor(source);
        }
        if (profile is null || source is null || sender is null)
        {
            return false;
        }
        var (view, _) = await SendOnAsync(source, sender, Build(profile, source, payload), cancellationToken)
            .ConfigureAwait(false);
        return view is not null;
    }

    /// <inheritdoc />
    public Task<string> NewListenerIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AprsIsLogin.NewListenerId());

    /// <summary>A Test send listening for its packet coming back (behavior 9.3).</summary>
    private sealed record EchoWatch(
        string Callsign,
        byte[] Information,
        TaskCompletionSource<(string Through, bool ByIgate)> Heard
    );

    /// <summary>
    /// Tells a Test send that <paramref name="packet"/>, just received, is its packet come back: repeated by a
    /// digipeater, or gated to APRS-IS by an IGate.
    /// </summary>
    private void CheckEchoes(AprsPacket packet)
    {
        List<EchoWatch> watches;
        lock (_gate)
        {
            if (_echoes.Count == 0)
            {
                return;
            }
            watches = [.. _echoes];
        }
        foreach (var watch in watches)
        {
            if (
                !string.Equals(packet.Source?.Value, watch.Callsign, StringComparison.OrdinalIgnoreCase)
                || !packet.Information.Span.SequenceEqual(watch.Information)
            )
            {
                continue;
            }
            var route = PathAnalysis.FromPacket(packet);
            if (route.IsGatedFromRadio && route.EntryStation is { } igate)
            {
                watch.Heard.TrySetResult((igate.Value, true));
            }
            else if (route.Digipeaters.Count > 0)
            {
                watch.Heard.TrySetResult((route.Digipeaters[^1].Value, false));
            }
        }
    }

    /// <summary>Whether map tiles may be downloaded now (F1.15): the Internet and Map tiles toggles are on.</summary>
    public bool DownloadsTiles
    {
        get
        {
            lock (_gate)
            {
                return (_settings ?? AppSettings.Empty).EffectiveToggles.DownloadsTiles;
            }
        }
    }

    /// <summary>Whether the Internet switch is on (F1.15), for address lookup.</summary>
    private bool InternetOn
    {
        get
        {
            lock (_gate)
            {
                return (_settings ?? AppSettings.Empty).EffectiveToggles.Internet;
            }
        }
    }

    /// <inheritdoc />
    public Task<ViewFacets> GetViewFacetsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_stations.Facets());

    /// <inheritdoc />
    public Task<ClearCounts> GetClearCountsAsync(CancellationToken cancellationToken = default)
    {
        var (stations, stale, tracks, telemetry) = _stations.Counts();
        int packets;
        lock (_gate)
        {
            packets = _recent.Count;
        }
        return Task.FromResult(new ClearCounts(stations, stale, tracks, packets, telemetry));
    }

    /// <inheritdoc />
    public Task<int> ClearAsync(ClearScope scope, CancellationToken cancellationToken = default)
    {
        var cleared = scope switch
        {
            ClearScope.Stations => _stations.Clear(),
            ClearScope.StaleStations => _stations.ClearStale(),
            ClearScope.Tracks => _stations.ClearTracks(),
            ClearScope.Packets => ClearPackets(),
            ClearScope.Telemetry => _stations.ClearTelemetry(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Not something Clear knows."),
        };
        LogCleared(scope, cleared);
        return Task.FromResult(cleared);

        int ClearPackets()
        {
            lock (_gate)
            {
                var count = _recent.Count;
                _dashboard.Clear(_time.GetUtcNow());
                _recent.Clear();
                _listed.Clear();
                _listedOrder.Clear();
                _seenEarly.Clear();
                _packetHistory++;
                return count;
            }
        }
    }

    /// <inheritdoc />
    public Task<Dashboard> GetDashboardAsync(DashboardRange range, CancellationToken cancellationToken = default)
    {
        var dashboard = _dashboard.Read(range, _time.GetUtcNow());
        // The counters cover the range; the rest is read from the stations held (F1.22 behaviors 4.10, 4.11, 10).
        return Task.FromResult(
            dashboard with
            {
                FarthestDirect = _stations.FarthestDirect(dashboard.Start, 5),
                Weather = _stations.WeatherSince(dashboard.Start),
                Tables = _stations.Statistics(),
            }
        );
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<NetworkLink>> GetNetworkLinksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_stations.Links());

    /// <inheritdoc />
    public Task<TelemetryView?> GetTelemetryAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Task.FromResult(_stations.Telemetry(name));
    }

    /// <inheritdoc />
    public Task<PacketRoute?> GetRouteAsync(RouteQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(_stations.Route(query));
    }

    /// <inheritdoc />
    public Task ClearTrackAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        _stations.ClearTrack(name);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<StationPage> QueryStationsAsync(StationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(_stations.Query(query));
    }

    /// <summary>One pass of the sweep: stale, removed and aged-out track points (F1.3 behavior 10, F1.5 behavior 1).</summary>
    internal void SweepNow(DateTimeOffset now)
    {
        AppSettings? settings;
        lock (_gate)
        {
            settings = _settings;
        }
        _stations.Sweep(now, (settings ?? AppSettings.Empty).EffectiveAging);
    }

    /// <summary>Marks stations stale and removes expired ones, once a <see cref="EngineOptions.SweepInterval"/> (F1.3 behavior 10).</summary>
    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                SweepNow(_time.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        var delay = _options.InitialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var restart = RestartToken;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, restart);
            TimeSpan? retryIn = null; // null: wait for a restart
            try
            {
                var loaded = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
                var profile = Station.Of(loaded);
                if (!loaded.EffectiveToggles.ConnectsAprsIs)
                {
                    // Turned off (F1.15): stay disconnected until a toggle or settings change restarts this.
                    SetLink(new Link(ConnectionState.Disconnected));
                }
                else if (profile is null)
                {
                    // Nothing to log in with: wait until settings supply a profile with an APRS-IS source.
                    SetLink(new Link(ConnectionState.NotConfigured));
                    LogNotConfigured();
                }
                else if (await RunOnceAsync(profile, attempt.Token).ConfigureAwait(false))
                {
                    LogLoginRefused(_link.Error?.Arg(EngineErrorArgs.Reason) ?? string.Empty);
                }
                else
                {
                    // The session ended by itself (a source that doesn't reconnect): start again, backoff reset.
                    delay = _options.InitialRetryDelay;
                    retryIn = delay;
                    SetRetry(
                        profile,
                        new EngineError(EngineErrorCodes.AprsIsServerClosed, "The server closed the connection."),
                        delay
                    );
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (restart.IsCancellationRequested)
            {
                delay = _options.InitialRetryDelay;
                continue; // the profile changed or a reconnect was asked for: connect now
            }
            catch (LoginRejectedException ex)
            {
                // Retrying a refused login on a timer only adds load to the servers (F1.1 behavior 3).
                EngineTelemetry.ConnectionFailures.Add(1);
                SetLink(new Link(ConnectionState.Refused, Error: AprsIsErrors.From(ex, wasConnected: false)));
                LogLoginRefused(ex.ServerMessage);
            }
            catch (KissTncLoginException ex)
            {
                // A KISS TNC answered, not a server: retrying only knocks on the TNC again (G32).
                EngineTelemetry.ConnectionFailures.Add(1);
                SetLink(new Link(ConnectionState.Refused, Error: AprsIsErrors.From(ex, wasConnected: false)));
                LogKissTnc();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EngineTelemetry.ConnectionFailures.Add(1);
                retryIn = delay;
                SetRetry(Station.Of(_settings), AprsIsErrors.From(ex, wasConnected: false), delay);
                LogConnectionFailed(ex, delay);
            }

            try
            {
                await Task.Delay(retryIn ?? Timeout.InfiniteTimeSpan, _time, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                delay = _options.InitialRetryDelay;
                continue; // a restart while waiting
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
        }
        SetLink(new Link(ConnectionState.Disconnected));
    }

    /// <summary>Handles one received line. Public for hosts that feed lines from elsewhere (and for tests).</summary>
    public void Accept(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        AprsPacket packet;
        try
        {
            packet = AprsPacket.Parse(line);
        }
        catch (ArgumentException ex)
        {
            // Text that wasn't read as Latin-1, from a source other than the APRS.NET client. Drop the line, not the
            // connection.
            EngineTelemetry.LinesRejected.Add(1);
            _dashboard.RecordRejected(_time.GetUtcNow());
            LogLineRejected(ex);
            return;
        }
        Accept(packet);
    }

    /// <summary>
    /// Takes a packet from <paramref name="sourceId"/> (null from <see cref="Accept(string)"/>). A copy of one heard
    /// first on another source isn't listed or counted again: the first stands, and its Seen on gains this source
    /// (F1.25 behavior 9). A copy on the same source (another IGate on APRS-IS) is listed, marked (F1.8 behavior 2).
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <param name="sourceId">Where it came from, or the source it was sent on.</param>
    /// <param name="sent">This station sent it (F2.1 behavior 13): listed marked <see cref="PacketMarkers.Sent"/>.</param>
    /// <returns>The packet as listed, or null when it wasn't.</returns>
    private PacketView? Accept(AprsPacket packet, string? sourceId = null, bool sent = false)
    {
        if (!packet.HasHeader)
        {
            return null; // server comments and noise
        }

        if (!sent)
        {
            CheckEchoes(packet);
            _messenger.Receive(packet, sourceId, IsOwnReceiver(sourceId)); // messages, acks, and who was heard where
        }
        var now = _time.GetUtcNow();
        var check = _duplicates.Check(packet, sourceId);
        if (
            !sent // each copy we send is listed with its own source (F2.1 behavior 13)
            && check.IsDuplicate
            && sourceId is not null
            && check.FirstSource is { } first
            && first != sourceId
        )
        {
            AddSeenOn(check.Identity, sourceId);
            _stations.SeenOn(packet, sourceId);
            _dashboard.RecordSource(sourceId, now); // still delivering, though another source was first
            return null;
        }
        var duplicate = check.IsDuplicate;
        var heardHere = IsOwnReceiver(sourceId); // a TNC: heard on RF by this station (F3.1 behavior 8)
        var view = PacketSummarizer.Summarize(packet, now, duplicate, heardHere) with { SourceId = sourceId };
        if (sent)
        {
            view = view with { Markers = PacketMarkers.Sent }; // not heard from anywhere: ours
        }
        if (!duplicate)
        {
            // F1.3 behavior 11: a duplicate is listed, not applied again. The track analysis marks the packet (F1.20).
            var anomalies = _stations.Apply(packet, now, sourceId, heardHere);
            view = anomalies == PacketAnomalies.None ? view : view with { Anomalies = anomalies };
        }
        EngineTelemetry.PacketsReceived.Add(1, new KeyValuePair<string, object?>("aprs.packet.type", view.Type));
        var (arrival, relays) = StationFacts.Route(view.Path, heardHere);
        _dashboard.Record(view, arrival, relays, now);
        lock (_gate)
        {
            _packets++;
            if (!duplicate && _seenEarly.Remove(check.Identity, out var early))
            {
                view = view with { SeenOn = [.. early.Sources] };
            }
            var node = _recent.AddLast(view);
            while (_recent.Count > _options.RecentPacketCapacity)
            {
                _recent.RemoveFirst();
            }
            Remember(check.Identity, node, now);
            foreach (var (watcher, filter) in _watchers)
            {
                if (filter?.Matches(view) ?? true)
                {
                    watcher.Writer.TryWrite(view);
                }
            }
        }
        return view;
    }

    /// <summary>
    /// Whether <paramref name="sourceId"/> is this station's own receiver: a KISS TNC (F3.1 behavior 8) or a radio read in
    /// its APRS mode (F3.2 behavior 2).
    /// </summary>
    private bool IsOwnReceiver(string? sourceId) =>
        sourceId is not null
        && _settings?.SourceList.FirstOrDefault(s => s.Id == sourceId)?.Kind
            is SourceKind.KissTcp
                or SourceKind.KissSerial
                or SourceKind.AprsTextSerial;

    /// <summary>Keeps <paramref name="node"/> findable by identity for one duplicate window.</summary>
    private void Remember(PacketIdentity identity, LinkedListNode<PacketView> node, DateTimeOffset now)
    {
        _listed[identity] = node;
        _listedOrder.Enqueue((now, identity, node));
        while (_listedOrder.TryPeek(out var oldest) && now - oldest.At > DuplicateFilter.DefaultWindow)
        {
            _listedOrder.Dequeue();
            // Only when the identity still points at this packet: a later copy may have replaced it.
            if (_listed.TryGetValue(oldest.Identity, out var listed) && ReferenceEquals(listed, oldest.Node))
            {
                _listed.Remove(oldest.Identity);
            }
        }
    }

    /// <summary>The packet listed for <paramref name="identity"/> was heard on <paramref name="sourceId"/> too.</summary>
    private void AddSeenOn(PacketIdentity identity, string sourceId)
    {
        lock (_gate)
        {
            if (!_listed.TryGetValue(identity, out var node) || node.List is null)
            {
                // The first copy is still on its way to the list (sources run side by side): it takes this when listed.
                ForgetEarly(_time.GetUtcNow());
                if (!_seenEarly.TryGetValue(identity, out var early))
                {
                    _seenEarly[identity] = early = (_time.GetUtcNow(), []);
                }
                if (!early.Sources.Contains(sourceId))
                {
                    early.Sources.Add(sourceId);
                }
            }
            else
            {
                var seen = node.Value.SeenOn ?? [];
                if (!seen.Contains(sourceId))
                {
                    var before = node.Value;
                    node.Value = before with { SeenOn = [.. seen, sourceId] };
                    // A list showing only this source hadn't been sent the packet: it was heard there too now (F3.1 behavior 10).
                    foreach (var (watcher, filter) in _watchers)
                    {
                        if (filter?.SourceId == sourceId && !filter.Matches(before) && filter.Matches(node.Value))
                        {
                            watcher.Writer.TryWrite(node.Value);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Drops early Seen on whose first copy never got listed, such as one cleared away meanwhile.</summary>
    private void ForgetEarly(DateTimeOffset now)
    {
        if (_seenEarly.Count == 0)
        {
            return;
        }
        foreach (var (identity, early) in _seenEarly.ToList())
        {
            if (now - early.At > DuplicateFilter.DefaultWindow)
            {
                _seenEarly.Remove(identity);
            }
        }
    }

    private CancellationToken RestartToken
    {
        get
        {
            lock (_gate)
            {
                return _restart.Token;
            }
        }
    }

    private void SignalRestart()
    {
        CancellationTokenSource restart;
        lock (_gate)
        {
            restart = _restart;
            _restart = new CancellationTokenSource();
        }
        // Not disposed: a session may still be linking to its token. It holds no timer, so the GC is enough.
        restart.Cancel();
    }

    /// <summary>Sends a changed filter on the live session; without one, the next login carries it anyway.</summary>
    /// <inheritdoc />
    public Task SetMapViewAsync(MapViewReport view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        SetMapView(LocalViewer, view);
        return Task.CompletedTask;
    }

    /// <summary>The viewer an in-process app reports its map as.</summary>
    internal const string LocalViewer = "local";

    /// <summary>
    /// Records what <paramref name="viewer"/>'s map shows (null when it left), and sends a new filter when that changes
    /// it (F1.2 behaviors 3–5). A view that barely moved is ignored, so a pan doesn't resend; the gateway reports each
    /// browser by its connection.
    /// </summary>
    public void SetMapView(string viewer, MapViewReport? view)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        lock (_gate)
        {
            var at = _views.FindIndex(v => v.Viewer == viewer);
            var last = at < 0 ? null : _views[at].View;
            if (view is not null && !FilterComposer.Differs(last, view))
            {
                return;
            }
            if (at >= 0)
            {
                _views.RemoveAt(at);
            }
            if (view is not null)
            {
                _views.Add((viewer, view));
            }
        }
        RefreshFilter();
    }

    /// <summary>The filter the active profile would be sent with now, for tests.</summary>
    internal string? PreviewFilter() => Station.Of(_settings) is { } station ? ComposeFilter(station) : null;

    /// <summary>The filter for <paramref name="profile"/> with the map views reported, most recently moved first.</summary>
    private string ComposeFilter(Station profile) => ComposeParts(profile).Text;

    /// <summary>The filter and its circles; a followed station's circle is centred where it was last heard.</summary>
    private (string Text, IReadOnlyList<FilterArea> Areas) ComposeParts(Station profile)
    {
        MapViewReport[] views;
        lock (_gate)
        {
            views = [.. _views.Select(v => v.View).Reverse()];
        }
        return FilterComposer.ComposeParts(
            profile.Profile,
            profile.Filter,
            views,
            name => _stations.Get(name) is { Latitude: { } lat, Longitude: { } lon } ? (lat, lon) : null
        );
    }

    /// <summary>
    /// Sends the filter again if it changed: at once, or when <see cref="EngineOptions.FilterInterval"/> has passed
    /// since the last send, merging any change made meanwhile (F1.2 behavior 5). Never a new login.
    /// </summary>
    private void RefreshFilter()
    {
        var profile = Station.Of(_settings);
        if (profile is null)
        {
            return;
        }
        TimeSpan wait;
        lock (_gate)
        {
            if (_source is null || _filterScheduled)
            {
                return; // not logged in (the login takes the current views), or a send is already waiting
            }
            wait = _filterSentAt + _options.FilterInterval - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                _filterScheduled = true;
            }
        }
        if (wait > TimeSpan.Zero)
        {
            _ = SendFilterLaterAsync(wait);
        }
        else
        {
            _ = SendFilterAsync(profile, CancellationToken.None);
        }
    }

    private async Task SendFilterLaterAsync(TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait, _time).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _filterScheduled = false;
            }
        }
        if (Station.Of(_settings) is { } profile)
        {
            await SendFilterAsync(profile, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SendFilterAsync(Station profile, CancellationToken cancellationToken)
    {
        var (filter, areas) = ComposeParts(profile);
        IPacketSource? source;
        lock (_gate)
        {
            source = _source;
            if (source is null)
            {
                return;
            }
            _filterAreas = areas; // a followed station moves its circle even when the text stays
            if (filter == _filter)
            {
                return;
            }
            _filter = filter;
            _filterSentAt = _time.GetUtcNow();
        }
        try
        {
            await source.SetFilterAsync(filter, cancellationToken).ConfigureAwait(false);
            LogFilterChanged(profile.Name, filter);
        }
        catch (ObjectDisposedException)
        {
            // The session ended meanwhile; the next one logs in with the current filter.
        }
    }

    /// <summary>The settings, loaded on first use; on first run they are created from the configured defaults.</summary>
    private async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_settings is not null)
            {
                return _settings;
            }
        }

        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadSettingsLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task<AppSettings> LoadSettingsLockedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_settings is not null)
            {
                return _settings;
            }
        }

        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            settings = FirstRunSettings();
            if (settings.Profiles.Count > 0)
            {
                await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            }
        }
        lock (_gate)
        {
            _settings = settings;
        }
        MeasureFrom(settings);
        await UseMessengerAsync(settings, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    /// <summary>The messenger takes up the active profile's conversations (docs/features/F2.4-messaging.md, behavior 5).</summary>
    private async Task UseMessengerAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var profile = settings.GetActiveProfile();
        await _messenger.UseAsync(profile?.Id, profile?.Callsign, cancellationToken).ConfigureAwait(false);
        _messenger.Pump(); // the Transmit switch or a source may have changed
        _beaconer.Poke(); // and beacons with them (F2.2 behavior 4)
    }

    /// <summary>Distances and bearings are from the active profile's location (F1.13 behavior 4).</summary>
    private void MeasureFrom(AppSettings settings)
    {
        var profile = settings.GetActiveProfile();
        _stations.SetHome(profile?.Latitude, profile?.Longitude, profile?.Callsign);
    }

    /// <summary>The first profile, from the configured defaults (appsettings.json or environment), if they name a callsign.</summary>
    private AppSettings FirstRunSettings()
    {
        if (string.IsNullOrWhiteSpace(_options.Callsign))
        {
            return AppSettings.Empty;
        }

        var callsign = _options.Callsign.Trim().ToUpperInvariant();
        var source = SourceSettings.AprsIsFor(
            _options.Server,
            string.IsNullOrWhiteSpace(_options.Filter) ? null : _options.Filter
        );
        var profile = new StationProfile(
            Guid.NewGuid().ToString("N"),
            callsign,
            callsign,
            _options.Passcode,
            [source.Id],
            null,
            null
        );
        var settings = new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [source]);
        try
        {
            SettingsValidator.Validate(settings, _time);
            return settings;
        }
        catch (ArgumentException ex)
        {
            LogDefaultsUnusable(ex.Message);
            return AppSettings.Empty;
        }
    }

    /// <summary>Runs one session until cancelled. Returns true when it ended because the server refused a new login.</summary>
    private async Task<bool> RunOnceAsync(Station profile, CancellationToken cancellationToken)
    {
        using var activity = EngineTelemetry.ActivitySource.StartActivity("aprs-is.session", ActivityKind.Client);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var refused = false;
        SetLink(new Link(ConnectionState.Connecting, profile.Server));
        var (filter, areas) = ComposeParts(profile);
        lock (_gate)
        {
            _filter = filter;
            _filterAreas = areas;
            _filterSentAt = _time.GetUtcNow();
        }
        var source = _sources.Create(profile.Profile, profile.Source, filter);
        await using (source.ConfigureAwait(false))
        {
            AttachTraffic(profile.Source.Id, source, always: false); // kept only while watched (F1.28 behavior 7)
            // Disposing the source stops its callbacks, so a session never reports after the next one has started.
            void OnEvent(PacketSourceEvent e)
            {
                switch (e.State)
                {
                    case ConnectionState.Connected:
                        Interlocked.Increment(ref _reconnects);
                        SetLink(new Link(ConnectionState.Connected, e.Login?.Server, e.Login?.Verified ?? false));
                        break;
                    case ConnectionState.Reconnecting:
                        EngineTelemetry.ConnectionFailures.Add(1);
                        SetRetry(profile, e.Error, e.RetryIn ?? TimeSpan.Zero);
                        break;
                    case ConnectionState.Refused:
                        EngineTelemetry.ConnectionFailures.Add(1);
                        refused = true;
                        SetLink(new Link(ConnectionState.Refused, Error: e.Error));
                        session.Cancel(); // end the session: the source would retry the refused login forever
                        break;
                    default:
                        SetLink(new Link(e.State, profile.Server));
                        break;
                }
            }

            try
            {
                var login = await source.StartAsync(OnEvent, session.Token).ConfigureAwait(false);
                activity?.SetTag("aprs-is.server", login.Server);
                Interlocked.Exchange(ref _reconnects, 0);
                lock (_gate)
                {
                    _source = source;
                }
                SetLink(new Link(ConnectionState.Connected, login.Server, login.Verified));
                LogConnected(login.Server);
                await foreach (var packet in source.ReadPacketsAsync(session.Token).ConfigureAwait(false))
                {
                    Accept(packet, profile.Source.Id);
                }
            }
            catch (OperationCanceledException) when (refused && !cancellationToken.IsCancellationRequested)
            {
                // Ended by the refusal.
            }
            finally
            {
                lock (_gate)
                {
                    if (_source == source)
                    {
                        _source = null;
                    }
                }
                DetachTraffic(profile.Source.Id, source);
            }
        }
        return refused;
    }

    private void SetRetry(Station? profile, EngineError? error, TimeSpan wait) =>
        SetLink(
            new Link(ConnectionState.Reconnecting, profile?.Server, RetryAt: _time.GetUtcNow() + wait, Error: error)
        );

    private void SetLink(Link link)
    {
        lock (_gate)
        {
            _link = link;
        }
    }

    /// <summary>The connection's part of <see cref="EngineStatus"/>, replaced as a whole so a poll never mixes two states.</summary>
    private sealed record Link(
        ConnectionState State,
        string? Server = null,
        bool Verified = false,
        DateTimeOffset? RetryAt = null,
        EngineError? Error = null,
        EngineError? Warning = null
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The APRS-IS server is a KISS TNC: add it as a TNC source instead. Not retrying."
    )]
    private partial void LogKissTnc();

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to APRS-IS server {Server}")]
    private partial void LogConnected(string server);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No station profile yet: set a callsign (first-run setup, or Engine:Callsign) to connect"
    )]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The configured first-run defaults are not usable: {Reason}")]
    private partial void LogDefaultsUnusable(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Station profile changed to {Profile}; reconnecting")]
    private partial void LogProfileChanged(string profile);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped a line that could not be parsed")]
    private partial void LogLineRejected(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings reset: every profile and setting was deleted")]
    private partial void LogReset();

    [LoggerMessage(Level = LogLevel.Information, Message = "APRS-IS turned {State} by the quick toggles")]
    private partial void LogToggled(string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleared {Count} ({Scope})")]
    private partial void LogCleared(ClearScope scope, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnect requested; reconnecting now")]
    private partial void LogReconnectRequested();

    [LoggerMessage(Level = LogLevel.Information, Message = "Transmit turned {State}.")]
    private partial void LogTransmit(string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Packet} on {Source}")]
    private partial void LogSent(AprsPacket packet, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A packet couldn't be sent on {Source}.")]
    private partial void LogSendFailed(Exception exception, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {Source} failed; trying again in {Delay}")]
    private partial void LogSourceFailed(Exception exception, string source, TimeSpan delay);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Station profile {Profile}'s filter is now {Filter}; sent without a new login"
    )]
    private partial void LogFilterChanged(string profile, string filter);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The APRS-IS server refused the login ({Reason}); not retrying until the profile changes or a reconnect is asked for"
    )]
    private partial void LogLoginRefused(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "APRS-IS connection failed; retrying in {Delay}")]
    private partial void LogConnectionFailed(Exception exception, TimeSpan delay);
}
