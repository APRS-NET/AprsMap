// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>The packet history's service calls (docs/features/F1.41-persistent-history.md).</summary>
public sealed partial class Engine
{
    private readonly PacketHistory? _history;

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

    /// <summary>Waits until the packets received so far are in the history, for tests.</summary>
    internal Task FlushHistoryAsync() => _history?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>The history's once-a-minute pass now, for tests.</summary>
    internal Task MaintainHistoryAsync() => _history?.MaintainAsync() ?? Task.CompletedTask;

    /// <summary>The packets kept since <paramref name="since"/>, oldest first, for tests and the restart (F1.41 behavior 7).</summary>
    internal IAsyncEnumerable<HistoryRecord> ReadHistoryAsync(DateTimeOffset since) =>
        _history?.ReadAsync(since) ?? AsyncEnumerable.Empty<HistoryRecord>();
}
