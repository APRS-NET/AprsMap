// SPDX-License-Identifier: MIT
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsNet.Kiss;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// A KISS TNC as a source (docs/features/F3.1-kiss-sources.md, behaviors 4, 5, 7), receive only, over APRS.NET's
/// <see cref="KissConnection"/>, which reconnects by itself after the TNC drops. The first connection is waited for, so a
/// TNC that can't be reached at all falls back on the engine's own backoff; after it, the connection's state changes
/// (reconnecting with the reason and the wait) are passed on, with any warning the connection raises (behavior 13).
/// Frames that aren't APRS are dropped by APRS.NET.
/// </summary>
/// <param name="connection">The TNC.</param>
/// <param name="endpoint">Where it is, for the pill.</param>
/// <param name="setup">How it's switched into KISS, for a KISS TNC with a type.</param>
/// <param name="gps">
/// Whether the radio's GPS sentences (TNC text output only) place the station (docs/features/F3.13-tnc-gps.md): Off
/// listens to none; Auto passes them on; On also warns when none arrive.
/// </param>
/// <param name="time">The clock the On warning waits by.</param>
internal sealed class KissPacketSource(
    KissConnection connection,
    string endpoint,
    TncSetup? setup = null,
    TncGps gps = TncGps.Off,
    TimeProvider? time = null
) : IPacketSource, IPacketSender, ITrafficSource, ITncConsole, IGpsSource
{
    private readonly NmeaFixTracker _tracker = new();
    private bool _gpsMissing; // On, and no GPS sentences for the connection's timeout (behavior 3), under _lock
    private ITimer? _gpsWait;

    /// <inheritdoc />
    public Action<GpsFix>? Fixes { get; set; }

    /// <inheritdoc />
    public Action<GpsQuality, IReadOnlyList<GpsSatellite>>? Sky { get; set; }

    /// <summary>A sentence from the radio's GPS (behavior 2): into the tracker, and its fix and sky to the engine.</summary>
    private void OnSentence(object? sender, NmeaSentenceEventArgs e)
    {
        var (quality, satellites) = (_tracker.Quality, _tracker.Satellites);
        if (_tracker.Update(e.Sentence))
        {
            Fixes?.Invoke(_tracker.Fix);
        }
        if (!Equals(_tracker.Quality, quality) || !ReferenceEquals(_tracker.Satellites, satellites))
        {
            Sky?.Invoke(_tracker.Quality, _tracker.Satellites);
        }
    }

    /// <summary>The radio's GPS came or went: with On, gone (or never there) is a warning (behavior 3).</summary>
    private void OnPresence(object? sender, GpsPresenceChangedEventArgs e) => SetGpsMissing(!e.IsPresent);

    private void SetGpsMissing(bool missing)
    {
        PacketSourceEvent? last;
        lock (_lock)
        {
            if (gps != TncGps.On || _gpsMissing == missing)
            {
                return;
            }
            _gpsMissing = missing;
            last = _last;
        }
        Report(last ?? new PacketSourceEvent(ConnectionState.Connected, new PacketSourceLogin(endpoint, false)));
    }

    /// <summary>
    /// Connected with On: if no sentence has come by the connection's timeout, says so (APRS.NET only reports a GPS that
    /// went away, not one that never came).
    /// </summary>
    private void WaitForGps()
    {
        if (gps != TncGps.On)
        {
            return;
        }
        _gpsWait?.Dispose();
        _gpsWait = (time ?? TimeProvider.System).CreateTimer(
            _ => SetGpsMissing(!connection.IsGpsPresent),
            null,
            connection.GpsTimeout,
            Timeout.InfiniteTimeSpan
        );
    }

    // The read loop runs ahead of the engine; a slow engine loses the oldest packets rather than stalling the TNC.
    private readonly Channel<AprsPacket> _packets = Channel.CreateBounded<AprsPacket>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
    );
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<PacketSourceLogin> _first = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly Lock _lock = new();
    private readonly HashSet<KissWarning> _warnings = [];
    private Action<PacketSourceEvent>? _changed;
    private Task? _pump;
    private bool _wasConnected;
    private PacketSourceEvent? _last;

    /// <inheritdoc />
    public async Task<PacketSourceLogin> StartAsync(
        Action<PacketSourceEvent> stateChanged,
        CancellationToken cancellationToken
    )
    {
        _changed = stateChanged;
        connection.StateChanged += OnStateChanged;
        connection.Warning += OnWarning;
        if (gps != TncGps.Off)
        {
            connection.NmeaSentenceReceived += OnSentence;
            connection.GpsPresenceChanged += OnPresence;
        }
        _pump = PumpAsync(_stop.Token);
        return await _first.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AprsPacket> ReadPacketsAsync(CancellationToken cancellationToken) =>
        _packets.Reader.ReadAllAsync(cancellationToken);

    /// <summary>A TNC has no server filter: it passes on what the radio hears.</summary>
    public ValueTask SetFilterAsync(string filter, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in connection.ReadPacketsAsync(cancellationToken).ConfigureAwait(false))
            {
                _packets.Writer.TryWrite(packet);
            }
            _packets.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _packets.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _first.TrySetException(ex);
            _packets.Writer.TryComplete(ex);
        }
    }

    private void OnStateChanged(object? sender, KissConnectionStateChangedEventArgs e)
    {
        if (!_first.Task.IsCompleted)
        {
            if (e.State == KissConnectionState.Connected)
            {
                _wasConnected = true;
                WaitForGps();
                _first.TrySetResult(new PacketSourceLogin(endpoint, Verified: false));
            }
            else if (e.State == KissConnectionState.Reconnecting)
            {
                // Never reached: the engine reports it and waits its own backoff before a new source.
                _first.TrySetException(new KissUnreachableException(ErrorOf(e)));
            }
            return;
        }
        var error = e.State == KissConnectionState.Reconnecting ? ErrorOf(e) : null;
        if (e.State == KissConnectionState.Connected)
        {
            _wasConnected = true;
            WaitForGps();
        }
        var state = e.State switch
        {
            KissConnectionState.Connected => ConnectionState.Connected,
            KissConnectionState.Connecting => ConnectionState.Connecting,
            KissConnectionState.Reconnecting => ConnectionState.Reconnecting,
            _ => ConnectionState.Disconnected,
        };
        Report(
            new PacketSourceEvent(
                state,
                state == ConnectionState.Connected ? new PacketSourceLogin(endpoint, Verified: false) : null,
                e.RetryIn,
                error
            )
        );
    }

    /// <summary>
    /// A warning started or ended. APRS.NET keeps one across reconnects until it's over, so it's added to the last
    /// state and to every later one.
    /// </summary>
    private void OnWarning(object? sender, KissWarningEventArgs e)
    {
        PacketSourceEvent? last;
        lock (_lock)
        {
            if (e.IsActive ? !_warnings.Add(e.Warning) : !_warnings.Remove(e.Warning))
            {
                return;
            }
            last = _last;
        }
        // Before the first connection the engine hasn't a state to add it to; the connected one will carry it.
        Report(last ?? new PacketSourceEvent(ConnectionState.Connected, new PacketSourceLogin(endpoint, false)));
    }

    private void Report(PacketSourceEvent e)
    {
        lock (_lock)
        {
            e = e with { Warning = WarningNow() };
            _last = e;
        }
        if (_first.Task.IsCompleted)
        {
            _changed?.Invoke(e);
        }
    }

    /// <summary>The warning to show: not in KISS mode before quiet, since a TNC sending text isn't quiet.</summary>
    private EngineError? WarningNow() =>
        _warnings.Contains(KissWarning.TextNotKiss)
            ? new EngineError(EngineErrorCodes.KissTextNotKiss, "The TNC is sending text, not KISS frames.")
        : _warnings.Contains(KissWarning.Quiet)
            ? new EngineError(EngineErrorCodes.KissQuiet, "The TNC has sent nothing for a while.")
        : _gpsMissing
            ? new EngineError(
                EngineErrorCodes.TncGpsMissing,
                "No GPS sentences from the radio: some radios, such as the TH-D72A, send GPS only in APRS mode, not KISS."
            )
        : null;

    /// <summary>
    /// Why the connection ended, as a code the UI words: closed, unreachable, the link failed, or the TNC didn't switch
    /// into KISS.
    /// </summary>
    private EngineError ErrorOf(KissConnectionStateChangedEventArgs e) =>
        e.Error is null ? new EngineError(EngineErrorCodes.KissClosed, "The TNC closed the connection.")
        // The TNC didn't switch into KISS (F3.2 behavior 6): APRS.NET's message carries the type's hint.
        : e.Error.InnerException is TncScriptException
            ? new EngineError(
                EngineErrorCodes.KissSetupFailed,
                e.Error.Message,
                new Dictionary<string, string> { [EngineErrorArgs.Reason] = e.Error.Message }
            )
        : _wasConnected ? new EngineError(EngineErrorCodes.KissLinkFailed, e.Error.Message)
        : new EngineError(EngineErrorCodes.KissUnreachable, e.Error.Message);

    /// <summary>Sends <paramref name="packet"/> to the TNC as a KISS data frame (docs/features/F2.1-own-station.md, behavior 5.2).</summary>
    public ValueTask SendAsync(AprsPacket packet, CancellationToken cancellationToken) =>
        connection.SendAsync(packet, cancellationToken);

    private Action<TrafficEntry>? _traffic;

    /// <inheritdoc />
    public void WatchTraffic(Action<TrafficEntry>? sink)
    {
        lock (_lock)
        {
            if (_traffic is null && sink is not null)
            {
                connection.Traffic += OnTraffic;
            }
            else if (_traffic is not null && sink is null)
            {
                connection.Traffic -= OnTraffic;
            }
            _traffic = sink;
        }
    }

    private void OnTraffic(object? sender, KissTrafficEventArgs e) => _traffic?.Invoke(EntryOf(e, string.Empty));

    /// <summary>
    /// APRS.NET's traffic entry in the console's terms (F1.28 behavior 2): the packet's TNC-2 text, or the text that came,
    /// and why a frame was dropped or which setup step it was. The source id is filled in by the engine.
    /// </summary>
    internal static TrafficEntry EntryOf(KissTrafficEventArgs e, string sourceId)
    {
        var bytes = e.Bytes.Length > TrafficEntry.MaxBytes ? e.Bytes[..TrafficEntry.MaxBytes] : e.Bytes;
        return new TrafficEntry(
            e.At,
            sourceId,
            e.Direction == KissTrafficDirection.Sent ? TrafficDirection.Out : TrafficDirection.In,
            e.Kind switch
            {
                KissTrafficKind.Frame => TrafficKind.Packet,
                KissTrafficKind.NotAprs => TrafficKind.NotAprs,
                KissTrafficKind.OtherPort => TrafficKind.OtherPort,
                KissTrafficKind.Command => TrafficKind.KissCommand,
                KissTrafficKind.DroppedFrame => TrafficKind.DroppedFrame,
                KissTrafficKind.Unframed => TrafficKind.Unframed,
                KissTrafficKind.Sent => TrafficKind.Sent,
                KissTrafficKind.RawSent => TrafficKind.Typed,
                KissTrafficKind.SetupSent => TrafficKind.SetupSent,
                KissTrafficKind.SetupReceived => TrafficKind.SetupReceived,
                KissTrafficKind.SetupFailed => TrafficKind.SetupFailed,
                KissTrafficKind.Nmea => TrafficKind.GpsSentence, // the radio's GPS (F3.13 behavior 6)
                KissTrafficKind.DamagedNmea => TrafficKind.DamagedGpsSentence,
                _ => TrafficKind.Unframed,
            },
            e.Packet?.ToString() ?? e.Sentence?.Text ?? (e.Text.Length > 0 ? e.Text : null),
            bytes.ToArray(),
            e.Port,
            e.DropReason?.ToString() ?? e.SetupStep?.ToString(),
            e.EndsLine || e.Kind != KissTrafficKind.Unframed,
            e.Bytes.Length > TrafficEntry.MaxBytes
        );
    }

    /// <inheritdoc />
    public TncSetup? Setup => setup;

    /// <inheritdoc />
    public IDisposable BeginManual() => connection.BeginManual();

    /// <inheritdoc />
    public Task SendLineAsync(string line, CancellationToken cancellationToken) =>
        connection.SendLineAsync(line, cancellationToken);

    /// <inheritdoc />
    public Task SendRawAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        connection.SendRawAsync(bytes, cancellationToken);

    /// <inheritdoc />
    public Task RunScriptAsync(TncScript script, CancellationToken cancellationToken) =>
        connection.RunScriptAsync(script, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        WatchTraffic(null);
        connection.StateChanged -= OnStateChanged;
        connection.Warning -= OnWarning;
        connection.NmeaSentenceReceived -= OnSentence;
        connection.GpsPresenceChanged -= OnPresence;
        _gpsWait?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        if (_pump is { } pump)
        {
            await pump.ConfigureAwait(false);
        }
        _stop.Dispose();
    }
}

/// <summary>A KISS TNC couldn't be reached the first time; carries the code the engine reports.</summary>
internal sealed class KissUnreachableException(EngineError error) : IOException(error.Message), IHasEngineError
{
    /// <inheritdoc />
    public EngineError EngineError { get; } = error;
}
