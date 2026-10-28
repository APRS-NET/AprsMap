// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// The dashboard for each source (#454): the same counters kept again per source, so every APRS tile can show one
/// source's numbers, and a comparison of the sources. A packet two sources both bring counts for each of them; the whole
/// feed still counts it once.
/// </summary>
public sealed partial class Engine
{
    private readonly Lock _bySourceGate = new();
    private readonly Dictionary<string, DashboardCounters> _bySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sourceHoursSaved = new(StringComparer.Ordinal);

    /// <summary>Takes <paramref name="line"/> as if <paramref name="sourceId"/> brought it, for tests.</summary>
    internal void Accept(string line, string sourceId) => Accept(AprsPacket.Parse(line), sourceId);

    private DashboardCounters CountersFor(string sourceId)
    {
        lock (_bySourceGate)
        {
            if (!_bySource.TryGetValue(sourceId, out var counters))
            {
                _bySource[sourceId] = counters = new DashboardCounters(_time.GetUtcNow());
            }
            return counters;
        }
    }

    private List<(string Id, DashboardCounters Counters)> SourceCounters()
    {
        lock (_bySourceGate)
        {
            return [.. _bySource.Select(s => (s.Key, s.Value))];
        }
    }

    /// <summary>Counts a packet listed from <see cref="PacketView.SourceId"/> for that source too.</summary>
    private void RecordForSource(
        PacketView view,
        ArrivalKind arrival,
        IReadOnlyList<(string Name, bool IsGateway)> relays,
        DateTimeOffset now
    )
    {
        if (view.SourceId is { } sourceId)
        {
            CountersFor(sourceId).Record(view, arrival, relays, now);
        }
    }

    /// <summary>
    /// A copy another source brought first (F1.25 behavior 9): not counted again for the whole feed, but for this source
    /// it's a packet it heard (#454).
    /// </summary>
    private void RecordCopyForSource(AprsPacket packet, string sourceId, DateTimeOffset now)
    {
        var heardHere = IsOwnReceiver(sourceId);
        var view = PacketSummarizer.Summarize(packet, now, false, heardHere) with { SourceId = sourceId };
        var (arrival, relays) = StationFacts.Route(view.Path, heardHere);
        CountersFor(sourceId).Record(view, arrival, relays, now);
    }

    /// <inheritdoc />
    public Task<Dashboard> GetSourceDashboardAsync(
        DashboardRange range,
        string sourceId,
        StationFilter? filter = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        var now = _time.GetUtcNow();
        var counters = CountersFor(sourceId);
        Func<string, bool>? include =
            filter is { IsEmpty: false } && range != DashboardRange.Week
                ? _stations.Matching(filter, now).Contains
                : null;
        Dashboard dashboard;
        if (range == DashboardRange.Week)
        {
            IReadOnlyList<DashboardHour> kept = [];
            try
            {
                kept = _metrics?.Read(now.AddDays(-7), sourceId) ?? [];
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                LogHoursNotRead(_logger, ex);
            }
            dashboard = counters.ReadWeek(now, kept);
        }
        else
        {
            dashboard = counters.Read(range, now, include);
        }
        // The tiles read from the stations held aren't kept per source: the whole feed's, and the UI says so.
        return Task.FromResult(
            dashboard with
            {
                SourceId = sourceId,
                FarthestDirect = _stations.FarthestDirect(dashboard.Start, 5, include),
                Weather = _stations.WeatherSince(dashboard.Start, include),
                Tables = _stations.Statistics(include is null ? null : filter, now),
                Gps = _gpsHistory.Read(dashboard.Start, dashboard.Step, dashboard.Packets.Count, now),
            }
        );
    }

    /// <summary>
    /// The sources side by side over the range, or its last 24 hours over 7 days (#454): stations heard, packets, and the
    /// stations no other source heard. Null with fewer than two sources.
    /// </summary>
    private IReadOnlyList<DashboardSourceRow>? CompareSources(DashboardRange range, DateTimeOffset now)
    {
        var sources = SourceCounters();
        if (sources.Count < 2)
        {
            return null;
        }
        var read = range == DashboardRange.Week ? DashboardRange.Day : range;
        var heard = sources
            .Select(s =>
            {
                var dashboard = s.Counters.Read(read, now);
                return (s.Id, Names: s.Counters.HeardSince(dashboard.Start), Packets: dashboard.Packets.Sum());
            })
            .ToList();
        return
        [
            .. heard
                .Select(s =>
                {
                    var others = heard
                        .Where(o => o.Id != s.Id)
                        .SelectMany(o => o.Names)
                        .ToHashSet(StringComparer.Ordinal);
                    return new DashboardSourceRow(
                        s.Id,
                        s.Names.Count,
                        s.Packets,
                        s.Names.Count(n => !others.Contains(n))
                    );
                })
                .OrderBy(s => s.SourceId, StringComparer.Ordinal),
        ];
    }

    /// <summary>Each source's counters as kept between runs (#386, #454).</summary>
    private Dictionary<string, DashboardCountersState> ExportSources() =>
        SourceCounters().ToDictionary(s => s.Id, s => s.Counters.Export(), StringComparer.Ordinal);

    private void ImportSources(IReadOnlyDictionary<string, DashboardCountersState>? sources, DateTimeOffset now)
    {
        foreach (var (id, state) in sources ?? new Dictionary<string, DashboardCountersState>())
        {
            CountersFor(id).Import(state, now);
        }
    }

    private void ClearSources()
    {
        lock (_bySourceGate)
        {
            _bySource.Clear();
            _sourceHoursSaved.Clear();
        }
    }

    /// <summary>Each source's ended hours, written to <c>metrics.db</c> beside the whole feed's (F1.41 behavior 10).</summary>
    private void SaveSourceHours(MetricsStore metrics, DateTimeOffset now)
    {
        foreach (var (id, counters) in SourceCounters())
        {
            long saved;
            lock (_bySourceGate)
            {
                saved = _sourceHoursSaved.GetValueOrDefault(id, long.MinValue);
            }
            var hours = counters.CompletedHours(now).Where(h => h.Hour > saved).ToList();
            if (hours.Count == 0)
            {
                continue;
            }
            metrics.Save(hours, now, id);
            lock (_bySourceGate)
            {
                _sourceHoursSaved[id] = hours[^1].Hour;
            }
        }
    }
}
