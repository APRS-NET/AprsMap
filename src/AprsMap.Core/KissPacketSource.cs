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
internal sealed class KissPacketSource(KissConnection connection, string endpoint) : IPacketSource, IPacketSender
{
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        connection.StateChanged -= OnStateChanged;
        connection.Warning -= OnWarning;
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
