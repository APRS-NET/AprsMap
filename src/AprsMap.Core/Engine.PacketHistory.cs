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
    private readonly MetricsStore? _metrics;
    private long _hoursSavedThrough = long.MinValue;
    private (DateTimeOffset At, Dashboard Lists)? _weekLists;

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

    /// <summary>
    /// Writes the dashboard's hours that have ended since the last write to <c>metrics.db</c> (behavior 10): at start,
    /// every hour the minutes hold, which takes up an older <c>dashboard-history.json</c> (behavior 11); then once a
    /// minute.
    /// </summary>
    private void SaveDashboardHours()
    {
        if (_metrics is null)
        {
            return;
        }
        var now = _time.GetUtcNow();
        var hours = _dashboard.CompletedHours(now).Where(h => h.Hour > _hoursSavedThrough).ToList();
        try
        {
            if (hours.Count > 0)
            {
                _metrics.Save(hours, now);
                _hoursSavedThrough = hours[^1].Hour;
            }
            SaveSourceHours(_metrics, now); // #454
        }
        catch (Exception ex)
            when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            LogHoursNotSaved(_logger, ex); // tried again next minute
        }
    }

    /// <summary>
    /// The dashboard's 7 days (behavior 10): a point an hour from <c>metrics.db</c> and the minutes, with the stations
    /// heard and the busiest from the packet history where it has them, worked out at most every 5 minutes. My view
    /// isn't offered over 7 days: the per-station counts cover 24 hours.
    /// </summary>
    private async Task<Dashboard> ReadWeekAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<DashboardHour> kept = [];
        try
        {
            kept = _metrics?.Read(now.AddDays(-7)) ?? [];
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            LogHoursNotRead(_logger, ex);
        }
        var week = _dashboard.ReadWeek(now, kept);
        if (_history is not { Usable: true, Settings.On: true })
        {
            return week; // the lists cover the minutes' 24 hours, and say so
        }
        if (_weekLists is not { } cached || now - cached.At >= TimeSpan.FromMinutes(5))
        {
            var since = week.Start;
            var (stations, busiest) = await _history
                .StationsSinceAsync(since, 10, cancellationToken)
                .ConfigureAwait(false);
            var keptSince = await _history.KeptSinceAsync(cancellationToken).ConfigureAwait(false);
            var cover = keptSince is { } first && first > since ? now - first : (TimeSpan?)null;
            cached = (
                now,
                week with
                {
                    StationsHeardTotal = stations,
                    BusiestStations = [.. busiest.Select(b => new DashboardCount(b.Name, b.Count))],
                    ListsCover = cover is { } c && c < DashboardCounters.Window ? DashboardCounters.Window : cover,
                }
            );
            _weekLists = cached;
        }
        return week with
        {
            StationsHeardTotal = cached.Lists.StationsHeardTotal,
            BusiestStations = cached.Lists.BusiestStations,
            ListsCover = cached.Lists.ListsCover,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackPoint>> GetTrackAsync(
        string name,
        DateTimeOffset since,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_history is not { Usable: true, Settings.On: true })
        {
            return _stations.GetTrack(name); // the live track is all there is
        }
        var kept = await _history.PositionsAsync(name, since, cancellationToken).ConfigureAwait(false);
        return LongTrack(kept);
    }

    /// <summary>
    /// The points as F1.5 joins them: one within 20 m of the last moves its time on (behavior 2), then thinned evenly to
    /// <see cref="TrackPoint.MaxLongTrack"/>, keeping the first and the last.
    /// </summary>
    internal static IReadOnlyList<TrackPoint> LongTrack(IReadOnlyList<TrackPoint> kept)
    {
        var track = new List<TrackPoint>(kept.Count);
        foreach (var point in kept)
        {
            if (
                track.Count > 0
                && Geodesy.DistanceKm(
                    new AprsPosition(track[^1].Latitude, track[^1].Longitude),
                    new AprsPosition(point.Latitude, point.Longitude)
                ) < StationStore.TrackMoveKm
            )
            {
                track[^1] = track[^1] with { Time = point.Time };
                continue;
            }
            track.Add(point);
        }
        if (track.Count <= TrackPoint.MaxLongTrack)
        {
            return track;
        }
        var step = (track.Count - 1) / (double)(TrackPoint.MaxLongTrack - 1);
        return [.. Enumerable.Range(0, TrackPoint.MaxLongTrack).Select(i => track[(int)Math.Round(i * step)])];
    }

    /// <inheritdoc />
    public async Task<KeptStations> GetKeptStationsAsync(
        KeptStationsQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_history is not { Usable: true, Settings.On: true })
        {
            return KeptStations.None;
        }
        var last = await _history
            .LastPositionsAsync(query.Since, query.South, query.West, query.North, query.East, cancellationToken)
            .ConfigureAwait(false);
        var added = last.Where(r => _stations.Get(r.Name!) is null) // drawn live already
            .OrderByDescending(r => r.ReceivedAt)
            .ToList();
        var stations = new List<StationState>();
        foreach (var record in added.Take(Math.Max(0, query.Max)))
        {
            string symbol;
            try
            {
                symbol =
                    PacketSummarizer
                        .Summarize(
                            AprsPacket.Parse(Encoding.Latin1.GetString(record.Raw)),
                            record.ReceivedAt,
                            false,
                            false
                        )
                        .Symbol
                    ?? "/.";
            }
            catch (ArgumentException)
            {
                continue;
            }
            stations.Add(
                new StationState(
                    record.Name!,
                    record.ReceivedAt,
                    record.Latitude!.Value,
                    record.Longitude!.Value,
                    symbol,
                    record.Kind,
                    IsStale: true
                )
            );
        }
        return new KeptStations(stations, added.Count);
    }

    /// <inheritdoc />
    public async Task<StationDetail?> GetKeptStationAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_history is not { Usable: true })
        {
            return null;
        }
        // Its last packets, applied in order to a store of their own, describe it as the station store would have.
        var records = await _history.PacketsOfAsync(name, null, 50, cancellationToken).ConfigureAwait(false);
        var store = new StationStore();
        foreach (var record in records.Reverse())
        {
            try
            {
                store.Apply(
                    AprsPacket.Parse(Encoding.Latin1.GetString(record.Raw)),
                    record.ReceivedAt,
                    record.SourceId,
                    IsOwnReceiver(record.SourceId)
                );
            }
            catch (ArgumentException)
            {
                // kept as received; one that can't be read now adds nothing
            }
        }
        return store.Get(name) is { } detail ? detail with { IsStale = true } : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PacketView>> GetStationPacketsAsync(
        string name,
        DateTimeOffset? before,
        int count,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_history is not { Usable: true } || count < 1)
        {
            return [];
        }
        var records = await _history.PacketsOfAsync(name, before, count, cancellationToken).ConfigureAwait(false);
        var views = new List<PacketView>(records.Count);
        foreach (var record in records)
        {
            AprsPacket packet;
            try
            {
                packet = AprsPacket.Parse(Encoding.Latin1.GetString(record.Raw));
            }
            catch (ArgumentException)
            {
                continue;
            }
            var view = PacketSummarizer.Summarize(
                packet,
                record.ReceivedAt,
                record.Duplicate,
                IsOwnReceiver(record.SourceId)
            ) with
            {
                SourceId = record.SourceId,
            };
            views.Add(record.Own ? view with { Markers = PacketMarkers.Sent } : view);
        }
        return views;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The dashboard's hours couldn't be saved")]
    private static partial void LogHoursNotSaved(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The dashboard's hours couldn't be read")]
    private static partial void LogHoursNotRead(ILogger logger, Exception error);
}
