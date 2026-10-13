// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AprsMap.Core;

/// <summary>
/// The in-process engine. <see cref="RunAsync"/> keeps an APRS-IS connection open, parses every line with APRS.NET,
/// remembers recent packets and fans them out to every <see cref="WatchPacketsAsync"/> caller.
/// </summary>
public sealed partial class Engine : IEngineService
{
    private readonly EngineOptions _options;
    private readonly IPacketLineSourceFactory _sources;
    private readonly TimeProvider _time;
    private readonly ILogger<Engine> _logger;
    private readonly Lock _gate = new();
    private readonly Queue<PacketView> _recent = new();
    private readonly List<Channel<PacketView>> _watchers = [];
    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _server;
    private long _packets;

    /// <summary>Creates the engine.</summary>
    public Engine(
        IOptions<EngineOptions> options,
        IPacketLineSourceFactory sources,
        TimeProvider time,
        ILogger<Engine> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _sources = sources;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(new EngineStatus(_state, _server, _packets, Brand.Version));
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

    /// <summary>Connects, reads and reconnects until <paramref name="cancellationToken"/> is cancelled.</summary>
    // SHIM: G03 (resilient APRS-IS connection). Replace this loop when APRS.NET ships its connection supervisor.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = _options.InitialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                delay = _options.InitialRetryDelay; // a connection that ended cleanly resets the backoff
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EngineTelemetry.ConnectionFailures.Add(1);
                SetState(ConnectionState.Faulted, null);
                LogConnectionFailed(ex, delay);
            }

            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
        }
        SetState(ConnectionState.Disconnected, null);
    }

    /// <summary>Handles one received line. Public for hosts that feed lines from elsewhere (and for tests).</summary>
    public void Accept(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var packet = AprsPacket.Parse(line);
        if (!packet.HasHeader)
        {
            return; // server comments and noise
        }

        var view = PacketSummarizer.Summarize(packet, _time.GetUtcNow());
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

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var activity = EngineTelemetry.ActivitySource.StartActivity("aprs-is.session", ActivityKind.Client);
        SetState(ConnectionState.Connecting, null);
        var source = _sources.Create(_options);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "APRS-IS connection failed; retrying in {Delay}")]
    private partial void LogConnectionFailed(Exception exception, TimeSpan delay);
}
