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
/// (reconnecting with the reason and the wait) are passed on. Frames that aren't APRS are dropped by APRS.NET.
/// </summary>
internal sealed class KissPacketSource(KissConnection connection, string endpoint) : IPacketSource
{
    // The read loop runs ahead of the engine; a slow engine loses the oldest packets rather than stalling the TNC.
    private readonly Channel<AprsPacket> _packets = Channel.CreateBounded<AprsPacket>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
    );
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<PacketSourceLogin> _first = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private Action<PacketSourceEvent>? _changed;
    private Task? _pump;
    private bool _wasConnected;

    /// <inheritdoc />
    public async Task<PacketSourceLogin> StartAsync(
        Action<PacketSourceEvent> stateChanged,
        CancellationToken cancellationToken
    )
    {
        _changed = stateChanged;
        connection.StateChanged += OnStateChanged;
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
        _changed?.Invoke(
            new PacketSourceEvent(
                state,
                state == ConnectionState.Connected ? new PacketSourceLogin(endpoint, Verified: false) : null,
                e.RetryIn,
                error
            )
        );
    }

    /// <summary>Why the connection ended, as a code the UI words: closed, unreachable, or the link failed.</summary>
    private EngineError ErrorOf(KissConnectionStateChangedEventArgs e) =>
        e.Error is null ? new EngineError(EngineErrorCodes.KissClosed, "The TNC closed the connection.")
        : _wasConnected ? new EngineError(EngineErrorCodes.KissLinkFailed, e.Error.Message)
        : new EngineError(EngineErrorCodes.KissUnreachable, e.Error.Message);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        connection.StateChanged -= OnStateChanged;
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
