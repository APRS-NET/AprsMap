// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsNet.AprsIsTcpClient;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AprsMap.Core;

/// <summary>
/// The in-process engine. <see cref="RunAsync"/> keeps an APRS-IS connection open, parses every line with APRS.NET,
/// remembers recent packets and fans them out to every <see cref="WatchPacketsAsync"/> caller, and keeps the latest
/// position of every station for <see cref="WatchStationsAsync"/>. It logs in with the active station profile from
/// <see cref="ISettingsStore"/>, waits while there is none, and reconnects when the profile changes.
/// </summary>
public sealed partial class Engine : IEngineService, IDisposable
{
    private readonly EngineOptions _options;
    private readonly IPacketLineSourceFactory _sources;
    private readonly ISettingsStore _settingsStore;
    private readonly IGeocoder _geocoder;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly TimeProvider _time;
    private readonly ILogger<Engine> _logger;
    private readonly Lock _gate = new();
    private readonly Queue<PacketView> _recent = new();
    private readonly List<Channel<PacketView>> _watchers = [];
    private readonly StationStore _stations = new();
    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _server;
    private long _packets;
    private AppSettings? _settings;

    // Cancelled (and replaced) when the active profile's login changes, to end the current session or wait.
    private CancellationTokenSource _profileChanged = new();

    /// <summary>Creates the engine.</summary>
    public Engine(
        IOptions<EngineOptions> options,
        IPacketLineSourceFactory sources,
        ISettingsStore settingsStore,
        TimeProvider time,
        ILogger<Engine> logger,
        IGeocoder? geocoder = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _sources = sources;
        _settingsStore = settingsStore;
        _geocoder = geocoder ?? NoGeocoder.Instance;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settingsLock.Dispose();
        lock (_gate)
        {
            _profileChanged.Dispose();
        }
    }

    /// <inheritdoc />
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var profile = _settings?.GetActiveProfile();
            return Task.FromResult(
                new EngineStatus(_state, _server, _packets, Brand.Version, profile?.Id, profile?.Callsign)
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
            var before = (await LoadSettingsLockedAsync(cancellationToken).ConfigureAwait(false)).GetActiveProfile();
            await _settingsStore.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _settings = saved;
            }

            var after = saved.GetActiveProfile();
            if (after is null ? before is not null : after.ConnectsDifferentlyFrom(before))
            {
                LogProfileChanged(after?.Name ?? "(none)");
                SignalProfileChanged();
            }
        }
        finally
        {
            _settingsLock.Release();
        }
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
        catch (ArgumentException ex)
        {
            return Task.FromException<int>(ex);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PacketView> WatchPacketsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Bounded so one slow client can't hold unbounded memory; it loses its oldest packets instead.
        var channel = Channel.CreateBounded<PacketView>(
            new BoundedChannelOptions(_options.RecentPacketCapacity) { FullMode = BoundedChannelFullMode.DropOldest }
        );
        lock (_gate)
        {
            foreach (var packet in _recent)
            {
                channel.Writer.TryWrite(packet);
            }
            _watchers.Add(channel);
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
                _watchers.Remove(channel);
            }
        }
    }

    /// <summary>How many stations the first batches of <see cref="WatchStationsAsync"/> carry each.</summary>
    internal const int SnapshotBatchSize = 1000;

    /// <inheritdoc />
    public async IAsyncEnumerable<StationBatch> WatchStationsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var watcher = _stations.Subscribe(out var snapshot);
        try
        {
            foreach (var chunk in snapshot.Chunk(SnapshotBatchSize))
            {
                yield return new StationBatch(chunk, []);
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
    /// Connects with the active profile, reads and reconnects until <paramref name="cancellationToken"/> is
    /// cancelled. With no profile it waits for one; a profile change ends the session and reconnects at once.
    /// </summary>
    // SHIM: G03 (resilient APRS-IS connection). Replace this loop when APRS.NET ships its connection supervisor.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = _options.InitialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var profileChanged = ProfileChangedToken;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, profileChanged);
            try
            {
                var profile = (await LoadSettingsAsync(cancellationToken).ConfigureAwait(false)).GetActiveProfile();
                if (profile is null)
                {
                    // Nothing to log in with: wait until settings supply a profile (ends with a cancellation).
                    SetState(ConnectionState.NotConfigured, null);
                    LogNotConfigured();
                    await Task.Delay(Timeout.InfiniteTimeSpan, _time, attempt.Token).ConfigureAwait(false);
                }
                else
                {
                    await RunOnceAsync(profile, attempt.Token).ConfigureAwait(false);
                    delay = _options.InitialRetryDelay; // a connection that ended cleanly resets the backoff
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (profileChanged.IsCancellationRequested)
            {
                delay = _options.InitialRetryDelay;
                continue; // the profile changed: connect with the new one now
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EngineTelemetry.ConnectionFailures.Add(1);
                SetState(ConnectionState.Faulted, null);
                LogConnectionFailed(ex, delay);
            }

            try
            {
                await Task.Delay(delay, _time, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                delay = _options.InitialRetryDelay;
                continue; // the profile changed while waiting to retry
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
        }
        SetState(ConnectionState.Disconnected, null);
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
        if (!packet.HasHeader)
        {
            return; // server comments and noise
        }

        var now = _time.GetUtcNow();
        var view = PacketSummarizer.Summarize(packet, now);
        _stations.Apply(packet, now);
        EngineTelemetry.PacketsReceived.Add(1, new KeyValuePair<string, object?>("aprs.packet.type", view.Type));
        lock (_gate)
        {
            _packets++;
            _recent.Enqueue(view);
            while (_recent.Count > _options.RecentPacketCapacity)
            {
                _recent.Dequeue();
            }
            foreach (var watcher in _watchers)
            {
                watcher.Writer.TryWrite(view);
            }
        }
    }

    private CancellationToken ProfileChangedToken
    {
        get
        {
            lock (_gate)
            {
                return _profileChanged.Token;
            }
        }
    }

    private void SignalProfileChanged()
    {
        CancellationTokenSource changed;
        lock (_gate)
        {
            changed = _profileChanged;
            _profileChanged = new CancellationTokenSource();
        }
        // Not disposed: a session may still be linking to its token. It holds no timer, so the GC is enough.
        changed.Cancel();
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
        return settings;
    }

    /// <summary>The first profile, from the configured defaults (appsettings.json or environment), if they name a callsign.</summary>
    private AppSettings FirstRunSettings()
    {
        if (string.IsNullOrWhiteSpace(_options.Callsign))
        {
            return AppSettings.Empty;
        }

        var callsign = _options.Callsign.Trim().ToUpperInvariant();
        var profile = new StationProfile(
            Guid.NewGuid().ToString("N"),
            callsign,
            callsign,
            _options.Passcode,
            _options.Server,
            string.IsNullOrWhiteSpace(_options.Filter) ? null : _options.Filter,
            null,
            null
        );
        var settings = new AppSettings([profile], profile.Id, UnitSettings.Metric);
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

    private async Task RunOnceAsync(StationProfile profile, CancellationToken cancellationToken)
    {
        using var activity = EngineTelemetry.ActivitySource.StartActivity("aprs-is.session", ActivityKind.Client);
        SetState(ConnectionState.Connecting, null);
        var source = _sources.Create(profile);
        await using (source.ConfigureAwait(false))
        {
            var server = await source.ConnectAsync(cancellationToken).ConfigureAwait(false);
            activity?.SetTag("aprs-is.server", server);
            SetState(ConnectionState.Connected, server);
            LogConnected(server);
            await foreach (var line in source.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                Accept(line);
            }
        }
    }

    private void SetState(ConnectionState state, string? server)
    {
        lock (_gate)
        {
            _state = state;
            _server = server;
        }
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "APRS-IS connection failed; retrying in {Delay}")]
    private partial void LogConnectionFailed(Exception exception, TimeSpan delay);
}
