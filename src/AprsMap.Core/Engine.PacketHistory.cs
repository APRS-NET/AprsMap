// SPDX-License-Identifier: MIT
using System.Text;
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>The packet history's service calls (docs/features/F1.41-persistent-history.md).</summary>
public sealed partial class Engine
{
    /// <summary>The most a restart puts back, when stations are never removed (F1.41 behavior 7).</summary>
    internal static readonly TimeSpan LongestRestore = TimeSpan.FromHours(24);

    private readonly PacketHistory? _history;

    /// <summary>
    /// After a restart or an upgrade, before the sources connect, applies again the packets kept from the last
    /// <see cref="StationAging.RemoveAfterMinutes"/> (at most <see cref="LongestRestore"/>), in order and with their
    /// times, so the map, the lists and the tracks come back as they were (F1.41 behavior 7). Nothing else is done with
    /// them: no messages, acks, alerts, digipeating, gating, transmitting or dashboard counts.
    /// </summary>
    internal async Task RestoreFromHistoryAsync(CancellationToken cancellationToken)
    {
        if (_history is null)
        {
            return;
        }
        var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false); // takes up the history's settings
        if (!_history.Settings.On)
        {
            return;
        }
        var window = settings.EffectiveAging.RemoveAfterMinutes is { } minutes
            ? TimeSpan.FromMinutes(Math.Min(minutes, LongestRestore.TotalMinutes))
            : LongestRestore;
        var now = _time.GetUtcNow();
        var listed = new LinkedList<PacketView>();
        var restored = 0;
        await foreach (var record in _history.ReadAsync(now - window, cancellationToken).ConfigureAwait(false))
        {
            AprsPacket packet;
            try
            {
                packet = AprsPacket.Parse(Encoding.Latin1.GetString(record.Raw));
            }
            catch (ArgumentException)
            {
                continue; // kept as received; if it can't be read now, it isn't put back
            }
            if (!packet.HasHeader)
            {
                continue;
            }
            var heardHere = IsOwnReceiver(record.SourceId);
            var anomalies = record.Duplicate
                ? PacketAnomalies.None
                : _stations.Apply(packet, record.ReceivedAt, record.SourceId, heardHere);
            var view = PacketSummarizer.Summarize(packet, record.ReceivedAt, record.Duplicate, heardHere) with
            {
                SourceId = record.SourceId,
                Anomalies = anomalies,
            };
            if (record.Own)
            {
                view = view with { Markers = PacketMarkers.Sent }; // as listed when sent (F2.1 behavior 13)
            }
            listed.AddLast(view);
            if (listed.Count > _options.RecentPacketCapacity)
            {
                listed.RemoveFirst();
            }
            restored++;
        }
        lock (_gate)
        {
            // Older than anything received since, which is nothing yet: the sources connect after this.
            for (var node = listed.Last; node is not null; node = node.Previous)
            {
                _recent.AddFirst(node.Value);
            }
            while (_recent.Count > _options.RecentPacketCapacity)
            {
                _recent.RemoveFirst();
            }
        }
        SweepNow(now); // stale and removed as they would be had the engine been running
        LogRestored(_logger, restored, (int)window.TotalMinutes);
    }

    /// <summary>Takes up the settings' history length and size limit; the configuration's win (behavior 2).</summary>
    private void UseHistory(AppSettings settings)
    {
        if (_history is not null)
        {
            _history.Settings = _options.History.Apply(settings.EffectiveHistory);
            _ = _history.MaintainAsync(); // a shorter length or a smaller limit takes effect now
        }
    }

    /// <summary>Keeps a packet the engine lists (behavior 1), never waiting for the disk.</summary>
    private void Keep(AprsPacket packet, PacketView view, string? sourceId, bool sent, bool duplicate)
    {
        if (_history is null)
        {
            return;
        }
        var (name, kind) = PacketHistory.EntryOf(packet);
        _history.Record(
            new HistoryRecord(
                view.ReceivedAt,
                sourceId,
                sent,
                duplicate,
                name,
                kind,
                view.Latitude,
                view.Longitude,
                packet.Raw.ToArray()
            )
        );
    }

    /// <inheritdoc />
    public Task<HistoryStatus> GetHistoryStatusAsync(CancellationToken cancellationToken = default) =>
        _history?.StatusAsync(_options.History.Configured, cancellationToken)
        ?? Task.FromResult(HistoryStatus.NotAvailable);

    /// <summary><paramref name="name"/>'s track as the store holds it, for tests.</summary>
    internal IReadOnlyList<TrackPoint> TrackOf(string name) => _stations.GetTrack(name);

    /// <summary>Waits until the packets received so far are in the history, for tests.</summary>
    internal Task FlushHistoryAsync() => _history?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>The history's once-a-minute pass now, for tests.</summary>
    internal Task MaintainHistoryAsync() => _history?.MaintainAsync() ?? Task.CompletedTask;

    /// <summary>The packets kept since <paramref name="since"/>, oldest first, for tests and the restart (F1.41 behavior 7).</summary>
    internal IAsyncEnumerable<HistoryRecord> ReadHistoryAsync(DateTimeOffset since) =>
        _history?.ReadAsync(since) ?? AsyncEnumerable.Empty<HistoryRecord>();

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored {Count} packets from the last {Minutes} minutes")]
    private static partial void LogRestored(ILogger logger, int count, int minutes);
}
