// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsNet.AprsIsTcpClient;
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
public sealed partial class Engine : IEngineService, IDisposable
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
    private readonly Queue<(DateTimeOffset At, PacketIdentity Identity, LinkedListNode<PacketView> Node)> _listedOrder =
        new();

    // The non-APRS-IS sources open, with their connection state (F1.25 behavior 11), by source id.
    private readonly Dictionary<string, (SourceSettings Source, Link Link)> _sourceLinks = new(StringComparer.Ordinal);
    private TaskCompletionSource _settingsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(Channel<PacketView> Channel, PacketFilter? Filter)> _watchers = [];
    private readonly StationStore _stations;
    private readonly DuplicateFilter _duplicates;

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
        IGeocoder? geocoder = null
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
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
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
                    PacketHistory: _packetHistory
                )
            );
        }
    }

    /// <inheritdoc />
    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SettingsValidator.Validate(settings, _time);
        var saved = settings with { ReadOnly = false };
        await _settingsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = Station.Of(await LoadSettingsLockedAsync(cancellationToken).ConfigureAwait(false));
            await _settingsStore.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _settings = saved;
            }
            MeasureFrom(saved);
            SignalSettingsChanged();

            var after = Station.Of(saved);
            if (after is null ? before is not null : after.NeedsNewLogin(before))
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
                _packets = 0;
            }
            _stations.Clear();
            MeasureFrom(AppSettings.Empty);
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
    ) => _geocoder.GeocodeAsync(query, language, cancellationToken);

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
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var watcher = _stations.Subscribe(out var snapshot, out var tracks);
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
                    : settings.SourceList.Where(s => s.Kind != SourceKind.AprsIs && profile.Uses(s)).ToList();
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
        }
    }

    /// <summary>One non-APRS-IS source, reconnecting with backoff until stopped; a kind not built yet stops at once.</summary>
    private async Task RunSourceAsync(
        StationProfile profile,
        SourceSettings source,
        CancellationToken cancellationToken
    )
    {
        var delay = _options.InitialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
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
                try
                {
                    SetSourceLink(source, new Link(ConnectionState.Connecting));
                    var opened = await connection
                        .StartAsync(
                            e => SetSourceLink(source, new Link(e.State, e.Login?.Server, Error: e.Error)),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    SetSourceLink(source, new Link(ConnectionState.Connected, opened.Server, opened.Verified));
                    delay = _options.InitialRetryDelay;
                    await foreach (var packet in connection.ReadPacketsAsync(cancellationToken).ConfigureAwait(false))
                    {
                        Accept(packet, source.Id);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SetSourceLink(
                        source,
                        new Link(
                            ConnectionState.Reconnecting,
                            RetryAt: _time.GetUtcNow() + delay,
                            Error: new EngineError(EngineErrorCodes.AprsIsLinkFailed, ex.Message)
                        )
                    );
                    LogSourceFailed(ex, source.Name, delay);
                }
            }
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
        }
    }

    private void SetSourceLink(SourceSettings source, Link link)
    {
        lock (_gate)
        {
            _sourceLinks[source.Id] = (source, link);
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
                    _link.Error
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
                v.Link.Error
            ))
        );
        return list;
    }

    /// <inheritdoc />
    public Task<StationDetail?> GetStationAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Task.FromResult(_stations.Get(name));
    }

    /// <inheritdoc />
    public Task<ClearCounts> GetClearCountsAsync(CancellationToken cancellationToken = default)
    {
        var (stations, stale, tracks) = _stations.Counts();
        int packets;
        lock (_gate)
        {
            packets = _recent.Count;
        }
        return Task.FromResult(new ClearCounts(stations, stale, tracks, packets));
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
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Not something Clear knows."),
        };
        LogCleared(scope, cleared);
        return Task.FromResult(cleared);

        int ClearPackets()
        {
            lock (_gate)
            {
                var count = _recent.Count;
                _recent.Clear();
                _listed.Clear();
                _listedOrder.Clear();
                _packetHistory++;
                return count;
            }
        }
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
                var profile = Station.Of(await LoadSettingsAsync(cancellationToken).ConfigureAwait(false));
                if (profile is null)
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
    private void Accept(AprsPacket packet, string? sourceId = null)
    {
        if (!packet.HasHeader)
        {
            return; // server comments and noise
        }

        var now = _time.GetUtcNow();
        var check = _duplicates.Check(packet, sourceId);
        if (check.IsDuplicate && sourceId is not null && check.FirstSource is { } first && first != sourceId)
        {
            AddSeenOn(check.Identity, sourceId);
            _stations.SeenOn(packet, sourceId);
            return;
        }
        var duplicate = check.IsDuplicate;
        var view = PacketSummarizer.Summarize(packet, now, duplicate) with { SourceId = sourceId };
        if (!duplicate)
        {
            _stations.Apply(packet, now, sourceId); // F1.3 behavior 11: a duplicate is listed, not applied again
        }
        EngineTelemetry.PacketsReceived.Add(1, new KeyValuePair<string, object?>("aprs.packet.type", view.Type));
        lock (_gate)
        {
            _packets++;
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
    }

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
            if (_listed.TryGetValue(identity, out var node) && node.List is not null)
            {
                var seen = node.Value.SeenOn ?? [];
                if (!seen.Contains(sourceId))
                {
                    node.Value = node.Value with { SeenOn = [.. seen, sourceId] };
                }
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
        return settings;
    }

    /// <summary>Distances and bearings are from the active profile's location (F1.13 behavior 4).</summary>
    private void MeasureFrom(AppSettings settings)
    {
        var profile = settings.GetActiveProfile();
        _stations.SetHome(profile?.Latitude, profile?.Longitude);
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
        EngineError? Error = null
    );

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleared {Count} ({Scope})")]
    private partial void LogCleared(ClearScope scope, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnect requested; reconnecting now")]
    private partial void LogReconnectRequested();

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
