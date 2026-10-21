// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text;
using AprsMap.Contracts;
using AprsNet.Gps;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// A GPS source (docs/features/F3.6-gps.md): it reads fixes rather than packets, and hands each one to
/// <see cref="Fixes"/>. It runs as any other source: started, read until it drops, and started again by the engine.
/// </summary>
internal interface IGpsSource
{
    /// <summary>Where each fix goes; set before the source starts. Called on the source's own task.</summary>
    Action<GpsFix>? Fixes { get; set; }

    /// <summary>
    /// Where the fix's quality and the satellites in view go when they change (docs/features/F3.12-gps-status.md);
    /// set before the source starts. Called on the source's own task.
    /// </summary>
    Action<GpsQuality, IReadOnlyList<GpsSatellite>>? Sky { get; set; }
}

/// <summary>
/// A GPS that sends NMEA sentences over a stream: a serial port, or a TCP connection (F3.6 behaviors 1.1, 1.2). APRS.NET
/// reads the sentences into fixes (<see cref="NmeaFixTracker"/>); opening the port or socket is the app's. Each line goes
/// to the Source console (behavior 6).
/// </summary>
/// <param name="open">Opens the port or connects; a failure is the engine's to retry.</param>
/// <param name="endpoint">Where it reads, for the pill: <c>/dev/ttyUSB0@4800</c> or <c>host:10110</c>.</param>
/// <param name="time">The clock the console's entries are stamped with.</param>
internal sealed class NmeaGpsSource(Func<CancellationToken, Task<Stream>> open, string endpoint, TimeProvider time)
    : IPacketSource,
        IGpsSource,
        ITrafficSource
{
    /// <summary>The longest line taken as a sentence; NMEA 0183's are at most 82 characters.</summary>
    private const int MaxLine = 256;

    private Stream? _stream;
    private Action<TrafficEntry>? _traffic;

    /// <inheritdoc />
    public Action<GpsFix>? Fixes { get; set; }

    /// <inheritdoc />
    public Action<GpsQuality, IReadOnlyList<GpsSatellite>>? Sky { get; set; }

    /// <inheritdoc />
    public async Task<PacketSourceLogin> StartAsync(
        Action<PacketSourceEvent> stateChanged,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _stream = await open(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or System.Net.Sockets.SocketException
                        or ArgumentException
            )
        {
            throw LinkFailed(ex.Message, ex);
        }
        return new PacketSourceLogin(endpoint, Verified: false);
    }

    /// <summary>Reads sentences until the GPS goes away; a GPS has no packets, so none come.</summary>
    public IAsyncEnumerable<AprsPacket> ReadPacketsAsync(CancellationToken cancellationToken) =>
        ReadAsync(cancellationToken);

    private async IAsyncEnumerable<AprsPacket> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await PumpAsync(cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("The GPS isn't open.");
        var tracker = new NmeaFixTracker();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                throw LinkFailed(ex.Message, ex);
            }
            if (line is null)
            {
                throw LinkFailed("The GPS closed the connection.", null);
            }
            line = line.Trim();
            if (line.Length is 0 or > MaxLine)
            {
                continue;
            }
            _traffic?.Invoke(
                new TrafficEntry(time.GetUtcNow(), string.Empty, TrafficDirection.In, TrafficKind.GpsSentence, line)
            );
            if (!NmeaSentence.TryParse(line, out var sentence))
            {
                continue;
            }
            var (quality, satellites) = (tracker.Quality, tracker.Satellites);
            if (tracker.Update(sentence))
            {
                Fixes?.Invoke(tracker.Fix);
            }
            // GSV and GST say nothing of the fix, only its quality and the sky (F3.12).
            if (!Equals(tracker.Quality, quality) || !ReferenceEquals(tracker.Satellites, satellites))
            {
                Sky?.Invoke(tracker.Quality, tracker.Satellites);
            }
        }
    }

    /// <summary>A GPS has no server filter.</summary>
    public ValueTask SetFilterAsync(string filter, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>The error a GPS that can't be read gives, for its pill.</summary>
    internal static EngineOperationException LinkFailed(string reason, Exception? inner) =>
        new(
            new EngineError(
                EngineErrorCodes.GpsLinkFailed,
                reason,
                new Dictionary<string, string> { [EngineErrorArgs.Reason] = reason }
            ),
            inner
        );

    /// <inheritdoc />
    public void WatchTraffic(Action<TrafficEntry>? sink) => _traffic = sink;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _traffic = null;
        if (_stream is { } stream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A GPS shared by gpsd, on this machine or another (F3.6 behavior 1.3), read with APRS.NET's
/// <see cref="GpsdConnection"/>, which reconnects by itself; its state goes to the source's pill.
/// </summary>
internal sealed class GpsdGpsSource(GpsdOptions options, TimeProvider time) : IPacketSource, IGpsSource, ITrafficSource
{
    private readonly GpsdConnection _gpsd = new(options);
    private readonly TaskCompletionSource<PacketSourceLogin> _first = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Threading.Channels.Channel<(GpsFix, GpsQuality, IReadOnlyList<GpsSatellite>)> _fixes =
        System.Threading.Channels.Channel.CreateBounded<(GpsFix, GpsQuality, IReadOnlyList<GpsSatellite>)>(
            new System.Threading.Channels.BoundedChannelOptions(16)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            }
        );
    private Action<PacketSourceEvent>? _changed;
    private Action<TrafficEntry>? _traffic;
    private Task? _pump;

    /// <inheritdoc />
    public Action<GpsFix>? Fixes { get; set; }

    /// <inheritdoc />
    public Action<GpsQuality, IReadOnlyList<GpsSatellite>>? Sky { get; set; }

    /// <inheritdoc />
    public void WatchTraffic(Action<TrafficEntry>? sink) => _traffic = sink;

    // gpsd's own lines, for the Source console (F3.12 behavior 6).
    private void OnLineRead(object? sender, GpsdLineEventArgs e) =>
        _traffic?.Invoke(
            new TrafficEntry(time.GetUtcNow(), string.Empty, TrafficDirection.In, TrafficKind.GpsdReport, e.Line)
        );

    /// <summary>Connects to gpsd; fails as the first attempt does, after which gpsd's own reconnects carry on.</summary>
    public async Task<PacketSourceLogin> StartAsync(
        Action<PacketSourceEvent> stateChanged,
        CancellationToken cancellationToken
    )
    {
        _changed = stateChanged;
        _gpsd.StateChanged += OnStateChanged;
        _gpsd.LineRead += OnLineRead;
        _pump = PumpAsync(_stop.Token);
        return await _first.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var fix in _gpsd.ReadFixesAsync(cancellationToken).ConfigureAwait(false))
            {
                _fixes.Writer.TryWrite((fix, _gpsd.Quality, _gpsd.Satellites)); // updated before each fix
            }
            _fixes.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _fixes.Writer.TryComplete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = NmeaGpsSource.LinkFailed(ex.Message, ex);
            _first.TrySetException(failed);
            _fixes.Writer.TryComplete(failed);
        }
    }

    private void OnStateChanged(object? sender, GpsdConnectionStateChangedEventArgs e)
    {
        switch (e.State)
        {
            case GpsdConnectionState.Connected:
                if (!_first.TrySetResult(new PacketSourceLogin(_gpsd.Server, Verified: false)))
                {
                    _changed?.Invoke(new PacketSourceEvent(ConnectionState.Connected));
                }
                break;
            case GpsdConnectionState.Reconnecting:
                var failed = NmeaGpsSource.LinkFailed(e.Error?.Message ?? "gpsd went away.", e.Error);
                var error = failed.EngineError;
                if (!_first.TrySetException(failed))
                {
                    _changed?.Invoke(
                        new PacketSourceEvent(ConnectionState.Reconnecting, RetryIn: e.RetryIn, Error: error)
                    );
                }
                break;
            case GpsdConnectionState.Connecting:
                if (_first.Task.IsCompleted)
                {
                    _changed?.Invoke(new PacketSourceEvent(ConnectionState.Connecting));
                }
                break;
        }
    }

    /// <summary>Hands gpsd's fixes on until it's stopped; a GPS has no packets, so none come.</summary>
    public IAsyncEnumerable<AprsPacket> ReadPacketsAsync(CancellationToken cancellationToken) =>
        ReadAsync(cancellationToken);

    private async IAsyncEnumerable<AprsPacket> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<GpsSatellite>? sky = null;
        GpsQuality? quality = null;
        await foreach (var (fix, q, satellites) in _fixes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Fixes?.Invoke(fix);
            if (!Equals(q, quality) || !ReferenceEquals(satellites, sky))
            {
                (quality, sky) = (q, satellites);
                Sky?.Invoke(q, satellites);
            }
        }
        yield break;
    }

    /// <summary>A GPS has no server filter.</summary>
    public ValueTask SetFilterAsync(string filter, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _gpsd.StateChanged -= OnStateChanged;
        _gpsd.LineRead -= OnLineRead;
        _traffic = null;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_pump is { } pump)
        {
            await pump.ConfigureAwait(false);
        }
        await _gpsd.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>A stream that closes what it came from (a socket or a serial port) when it's disposed.</summary>
internal sealed class OwningStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            owner.Dispose();
        }
        base.Dispose(disposing);
    }
}
