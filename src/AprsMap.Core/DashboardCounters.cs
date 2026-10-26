// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>
/// The dashboard's history (docs/features/F1.22-statistics.md, "Engine"): counters a minute for the last 24 hours in a
/// fixed ring, distinct stations counted a point for each of the three point sizes, and per-station and per-relay
/// counts in 5-minute steps. Each station's steps also keep how its packets arrived, their kind and their problems, so
/// My view (behavior 7) can count only the stations a view filter shows. Memory stays bounded: buckets are reused, and
/// stations not heard for 24 hours are dropped. Thread-safe; the engine records from its sessions and viewers read
/// from theirs.
/// </summary>
internal sealed class DashboardCounters
{
    /// <summary>How much history is kept.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private const int Minutes = 24 * 60;
    private const int CountStep = 5; // minutes a per-station count step covers
    private const int NewestKept = 5;
    private const int TopKept = 10;

    // The point sizes, in minutes, each with a ring of distinct-station counts covering the window.
    private static readonly int[] PointSizes = [1, 5, 15];

    private static readonly ArrivalKind[] ArrivalKinds = Enum.GetValues<ArrivalKind>();
    private static readonly PacketKind[] PacketKinds = Enum.GetValues<PacketKind>();

    private readonly Lock _gate = new();
    private readonly Bucket[] _minutes = new Bucket[Minutes];
    private readonly (long Slot, int Count)[][] _distinct =
    [
        .. PointSizes.Select(size => new (long, int)[Minutes / size]),
    ];
    private readonly Dictionary<string, Heard> _stations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(long Step, int Count)>> _relays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _sources = new(StringComparer.Ordinal);
    private readonly LinkedList<DashboardMoment> _newest = new();
    private long _prunedAt = long.MinValue;

    public DashboardCounters(DateTimeOffset start)
    {
        HistoryStart = start;
        for (var i = 0; i < _minutes.Length; i++)
        {
            _minutes[i] = new Bucket();
        }
    }

    /// <summary>When counting began: the engine's start, or the last <see cref="Clear"/>.</summary>
    public DateTimeOffset HistoryStart { get; private set; }

    /// <summary>Counts one packet as the engine listed it.</summary>
    /// <param name="packet">The packet, summarized.</param>
    /// <param name="arrival">How it arrived, from its path.</param>
    /// <param name="relays">The digipeaters and the IGate it went through on RF.</param>
    /// <param name="now">When it arrived.</param>
    public void Record(
        PacketView packet,
        ArrivalKind arrival,
        IReadOnlyList<(string Name, bool IsGateway)> relays,
        DateTimeOffset now
    )
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(relays);
        var minute = MinuteOf(now);
        lock (_gate)
        {
            Prune(minute);
            var bucket = BucketAt(minute);
            bucket.Packets++;
            var source = packet.SourceId ?? string.Empty;
            bucket.BySource[source] = bucket.BySource.GetValueOrDefault(source) + 1;
            _sources[source] = now;
            bucket.Arrivals[(int)arrival]++;
            bucket.Kinds[(int)packet.Kind]++;
            if (packet.HasIssues)
            {
                bucket.Problems++;
            }
            Hear(packet.Source, minute, now, bucket, arrival, packet.Kind, packet.HasIssues);
            foreach (var (name, _) in relays)
            {
                if (!_relays.TryGetValue(name, out var counts))
                {
                    _relays[name] = counts = [];
                }
                Add(counts, minute / CountStep);
            }
        }
    }

    /// <summary>
    /// A source delivered a packet another source had already brought (F1.25 behavior 9): it isn't counted again, but
    /// the source isn't quiet.
    /// </summary>
    public void RecordSource(string sourceId, DateTimeOffset now)
    {
        lock (_gate)
        {
            _sources[sourceId] = now;
        }
    }

    /// <summary>Counts a line that couldn't be read as a packet (behavior 4.6).</summary>
    public void RecordRejected(DateTimeOffset now)
    {
        var minute = MinuteOf(now);
        lock (_gate)
        {
            BucketAt(minute).Problems++;
        }
    }

    /// <summary>Forgets everything counted (F1.19, with the packet history); counting starts again at <paramref name="now"/>.</summary>
    public void Clear(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var bucket in _minutes)
            {
                bucket.Reset(long.MinValue);
            }
            foreach (var ring in _distinct)
            {
                Array.Clear(ring);
            }
            _stations.Clear();
            _relays.Clear();
            _sources.Clear();
            _newest.Clear();
            HistoryStart = now;
        }
    }

    /// <summary>What's counted, to keep between runs (issue #386).</summary>
    public DashboardCountersState Export()
    {
        lock (_gate)
        {
            return new DashboardCountersState(
                HistoryStart,
                [
                    .. _minutes
                        .Where(b => b.Minute != long.MinValue)
                        .Select(b => new DashboardMinuteState(
                            b.Minute,
                            b.Packets,
                            b.Problems,
                            b.New,
                            new Dictionary<string, int>(b.BySource, StringComparer.Ordinal),
                            ByName(ArrivalKinds, b.Arrivals),
                            ByName(PacketKinds, b.Kinds)
                        )),
                ],
                [
                    .. _distinct.Select(ring =>
                        (IReadOnlyList<long[]>)[.. ring.Where(r => r.Count > 0).Select(r => new[] { r.Slot, r.Count })]
                    ),
                ],
                [
                    .. _stations.Select(s => new HeardState(
                        s.Key,
                        s.Value.FirstHeard,
                        s.Value.LastHeard,
                        [.. s.Value.LastSlots],
                        [
                            .. s.Value.Tallies.Select(t => new TallyState(
                                t.Step,
                                t.Count,
                                t.Problems,
                                ArrivalKinds[t.Arrival].ToString(),
                                PacketKinds[t.Kind].ToString()
                            )),
                        ]
                    )),
                ],
                _relays.ToDictionary(
                    r => r.Key,
                    r => r.Value.Select(c => new[] { c.Step, (long)c.Count }).ToArray(),
                    StringComparer.Ordinal
                ),
                new Dictionary<string, DateTimeOffset>(_sources, StringComparer.Ordinal),
                [.. _newest]
            );
        }
    }

    /// <summary>
    /// Takes up what was kept (issue #386), dropping what's older than the window at <paramref name="now"/>; what's
    /// counted already stays, and the history starts at the earlier of the two.
    /// </summary>
    public void Import(DashboardCountersState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var minute = MinuteOf(now);
        var oldest = minute - Minutes + 1;
        lock (_gate)
        {
            foreach (var kept in state.Minutes.Where(m => m.Minute >= oldest && m.Minute <= minute))
            {
                var bucket = BucketAt(kept.Minute);
                bucket.Packets += kept.Packets;
                bucket.Problems += kept.Problems;
                bucket.New += kept.New;
                foreach (var (source, count) in kept.BySource)
                {
                    bucket.BySource[source] = bucket.BySource.GetValueOrDefault(source) + count;
                }
                AddByName(ArrivalKinds, bucket.Arrivals, kept.Arrivals);
                AddByName(PacketKinds, bucket.Kinds, kept.Kinds);
            }
            for (var i = 0; i < PointSizes.Length && i < state.Distinct.Count; i++)
            {
                var ring = _distinct[i];
                var first = oldest / PointSizes[i];
                foreach (var pair in state.Distinct[i].Where(p => p.Length == 2 && p[0] >= first))
                {
                    var at = Index(pair[0], ring.Length);
                    ring[at] =
                        ring[at].Slot == pair[0] ? (pair[0], ring[at].Count + (int)pair[1]) : (pair[0], (int)pair[1]);
                }
            }
            var cutoff = now - Window;
            foreach (var kept in state.Stations.Where(s => s.LastHeard >= cutoff && !_stations.ContainsKey(s.Name)))
            {
                var heard = new Heard(kept.FirstHeard) { LastHeard = kept.LastHeard };
                for (var i = 0; i < heard.LastSlots.Length && i < kept.LastSlots.Length; i++)
                {
                    heard.LastSlots[i] = kept.LastSlots[i];
                }
                foreach (var tally in kept.Tallies)
                {
                    if (
                        Enum.TryParse<ArrivalKind>(tally.Arrival, out var arrival)
                        && Enum.TryParse<PacketKind>(tally.Kind, out var kind)
                    )
                    {
                        heard.Tallies.Add(
                            new Tally(tally.Step, tally.Count, tally.Problems, (byte)arrival, (byte)kind)
                        );
                    }
                }
                _stations[kept.Name] = heard;
            }
            foreach (var (name, counts) in state.Relays.Where(r => !_relays.ContainsKey(r.Key)))
            {
                _relays[name] = [.. counts.Where(c => c.Length == 2).Select(c => (c[0], (int)c[1]))];
            }
            foreach (var (source, at) in state.Sources)
            {
                _sources.TryAdd(source, at);
            }
            foreach (var moment in state.Newest.Where(n => n.At >= cutoff)) // newest first, after this run's own
            {
                if (_newest.Count < NewestKept && _newest.All(n => n.Key != moment.Key))
                {
                    _newest.AddLast(moment);
                }
            }
            if (state.HistoryStart < HistoryStart)
            {
                HistoryStart = state.HistoryStart;
            }
            _prunedAt = long.MinValue; // the next read prunes what's grown old
        }
    }

    private static Dictionary<string, int> ByName<T>(T[] names, int[] counts)
        where T : struct, Enum =>
        names
            .Select((name, i) => (name, i))
            .Where(x => counts[x.i] > 0)
            .ToDictionary(x => x.name.ToString(), x => counts[x.i], StringComparer.Ordinal);

    private static void AddByName<T>(T[] names, int[] counts, IReadOnlyDictionary<string, int> kept)
        where T : struct, Enum
    {
        foreach (var (name, count) in kept)
        {
            if (Enum.TryParse<T>(name, out var value) && Array.IndexOf(names, value) is var i and >= 0)
            {
                counts[i] += count;
            }
        }
    }

    /// <summary>
    /// The numbers over <paramref name="range"/> up to <paramref name="now"/>: the whole feed, or with
    /// <paramref name="include"/> only the stations it says yes to (My view, behavior 7).
    /// </summary>
    public Dashboard Read(DashboardRange range, DateTimeOffset now, Func<string, bool>? include = null)
    {
        if (include is not null)
        {
            return ReadFor(range, now, include);
        }
        var size = range switch
        {
            DashboardRange.Hour => 1,
            DashboardRange.SixHours => 5,
            DashboardRange.Day => 15,
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Not a dashboard range."),
        };
        var points = range switch
        {
            DashboardRange.Hour => 60,
            DashboardRange.SixHours => 72,
            _ => 96,
        };
        var minute = MinuteOf(now);
        var lastSlot = minute / size;
        var firstSlot = lastSlot - points + 1;
        var firstMinute = firstSlot * size;
        var start = DateTimeOffset.UnixEpoch.AddMinutes(firstMinute);
        lock (_gate)
        {
            Prune(minute);
            var packets = new int[points];
            var problems = new int[points];
            var fresh = new int[points];
            var arrivals = ArrivalKinds.ToDictionary(k => k, _ => new int[points]);
            var bySource = new Dictionary<string, int[]>(StringComparer.Ordinal);
            var kinds = new int[PacketKinds.Length];
            var recent = 0;
            for (var m = firstMinute; m <= minute; m++)
            {
                var bucket = _minutes[Index(m, Minutes)];
                if (bucket.Minute != m)
                {
                    continue;
                }
                var point = (int)((m - firstMinute) / size);
                packets[point] += bucket.Packets;
                problems[point] += bucket.Problems;
                fresh[point] += bucket.New;
                foreach (var kind in ArrivalKinds)
                {
                    arrivals[kind][point] += bucket.Arrivals[(int)kind];
                }
                foreach (var (source, count) in bucket.BySource)
                {
                    if (!bySource.TryGetValue(source, out var series))
                    {
                        bySource[source] = series = new int[points];
                    }
                    series[point] += count;
                }
                for (var k = 0; k < kinds.Length; k++)
                {
                    kinds[k] += bucket.Kinds[k];
                }
                if (m > minute - 5)
                {
                    recent += bucket.Packets;
                }
            }
            var ring = _distinct[Array.IndexOf(PointSizes, size)];
            var heard = new int[points];
            for (var slot = firstSlot; slot <= lastSlot; slot++)
            {
                var (at, count) = ring[Index(slot, ring.Length)];
                if (at == slot)
                {
                    heard[slot - firstSlot] = count;
                }
            }
            var firstStep = firstMinute / CountStep;
            return new Dashboard(
                now,
                range,
                start,
                TimeSpan.FromMinutes(size),
                HistoryStart,
                packets,
                recent / 5.0,
                bySource.Count > 1
                    ? [.. bySource.OrderBy(s => s.Key).Select(s => new DashboardSeries(s.Key, s.Value))]
                    : [],
                heard,
                _stations.Values.Count(s => s.LastHeard >= start),
                fresh,
                fresh.Sum(),
                [.. _newest.Where(n => n.At >= start)],
                [.. arrivals.Select(a => new DashboardSeries(a.Key.ToString(), a.Value))],
                [
                    .. PacketKinds
                        .Select(k => new DashboardCount(k.ToString(), kinds[(int)k]))
                        .Where(c => c.Count > 0)
                        .OrderByDescending(c => c.Count)
                        .ThenBy(c => c.Key, StringComparer.Ordinal),
                ],
                problems,
                problems.Sum(),
                Top(_stations.Select(s => (s.Key, s.Value.Total(firstStep)))),
                Top(_relays.Select(r => (r.Key, r.Value.Where(c => c.Step >= firstStep).Sum(c => c.Count)))),
                [
                    .. _sources
                        .OrderBy(s => s.Key, StringComparer.Ordinal)
                        .Select(s => new DashboardMoment(s.Key, s.Value)),
                ]
            );
        }
    }

    /// <summary>
    /// My view (behavior 7): the numbers from the stations <paramref name="include"/> says yes to, read from their
    /// 5-minute steps, so points are at least 5 minutes. Packets by source, the busiest relays and each source's last
    /// packet stay the whole feed's: they aren't kept per station.
    /// </summary>
    private Dashboard ReadFor(DashboardRange range, DateTimeOffset now, Func<string, bool> include)
    {
        var size = range == DashboardRange.Day ? 15 : CountStep;
        var points = range switch
        {
            DashboardRange.Hour => 12,
            DashboardRange.SixHours => 72,
            _ => 96,
        };
        var minute = MinuteOf(now);
        var lastSlot = minute / size;
        var firstSlot = lastSlot - points + 1;
        var firstMinute = firstSlot * size;
        var firstStep = firstMinute / CountStep;
        var recentStep = (minute - 4) / CountStep; // the steps covering the last 5 minutes
        var start = DateTimeOffset.UnixEpoch.AddMinutes(firstMinute);
        lock (_gate)
        {
            Prune(minute);
            var packets = new int[points];
            var problems = new int[points];
            var fresh = new int[points];
            var heard = new int[points];
            var arrivals = ArrivalKinds.ToDictionary(k => k, _ => new int[points]);
            var kinds = new int[PacketKinds.Length];
            var recent = 0;
            var stations = _stations.Where(s => include(s.Key)).ToList();
            foreach (var (_, station) in stations)
            {
                var lastPoint = -1;
                foreach (var tally in station.Tallies)
                {
                    if (tally.Step < firstStep)
                    {
                        continue;
                    }
                    var point = (int)((tally.Step * CountStep - firstMinute) / size);
                    packets[point] += tally.Count;
                    problems[point] += tally.Problems;
                    arrivals[ArrivalKinds[tally.Arrival]][point] += tally.Count;
                    kinds[tally.Kind] += tally.Count;
                    if (point != lastPoint)
                    {
                        heard[point]++; // once a point, however many packets
                        lastPoint = point;
                    }
                    if (tally.Step >= recentStep)
                    {
                        recent += tally.Count;
                    }
                }
                if (station.FirstHeard >= start)
                {
                    fresh[(int)((MinuteOf(station.FirstHeard) - firstMinute) / size)]++;
                }
            }
            var recentMinutes = minute - (recentStep * CountStep) + 1;
            return new Dashboard(
                now,
                range,
                start,
                TimeSpan.FromMinutes(size),
                HistoryStart,
                packets,
                recent / (double)recentMinutes,
                [],
                heard,
                stations.Count(s => s.Value.LastHeard >= start),
                fresh,
                fresh.Sum(),
                [
                    .. stations
                        .Where(s => s.Value.FirstHeard >= start)
                        .OrderByDescending(s => s.Value.FirstHeard)
                        .Take(NewestKept)
                        .Select(s => new DashboardMoment(s.Key, s.Value.FirstHeard)),
                ],
                [.. arrivals.Select(a => new DashboardSeries(a.Key.ToString(), a.Value))],
                [
                    .. PacketKinds
                        .Select(k => new DashboardCount(k.ToString(), kinds[(int)k]))
                        .Where(c => c.Count > 0)
                        .OrderByDescending(c => c.Count)
                        .ThenBy(c => c.Key, StringComparer.Ordinal),
                ],
                problems,
                problems.Sum(),
                Top(stations.Select(s => (s.Key, s.Value.Total(firstStep)))),
                Top(_relays.Select(r => (r.Key, r.Value.Where(c => c.Step >= firstStep).Sum(c => c.Count)))),
                [
                    .. _sources
                        .OrderBy(s => s.Key, StringComparer.Ordinal)
                        .Select(s => new DashboardMoment(s.Key, s.Value)),
                ],
                MyView: true
            );
        }
    }

    private static List<DashboardCount> Top(IEnumerable<(string Name, int Count)> all) =>
        [
            .. all.Select(a => new DashboardCount(a.Name, a.Count))
                .Where(c => c.Count > 0)
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .Take(TopKept),
        ];

    private void Hear(
        string name,
        long minute,
        DateTimeOffset now,
        Bucket bucket,
        ArrivalKind arrival,
        PacketKind kind,
        bool problem
    )
    {
        if (!_stations.TryGetValue(name, out var station))
        {
            _stations[name] = station = new Heard(now);
            bucket.New++;
            _newest.AddFirst(new DashboardMoment(name, now));
            if (_newest.Count > NewestKept)
            {
                _newest.RemoveLast();
            }
        }
        station.LastHeard = now;
        station.Add(minute / CountStep, arrival, kind, problem);
        for (var i = 0; i < PointSizes.Length; i++)
        {
            var slot = minute / PointSizes[i];
            if (station.LastSlots[i] == slot)
            {
                continue; // already counted in this point
            }
            station.LastSlots[i] = slot;
            var ring = _distinct[i];
            var at = Index(slot, ring.Length);
            ring[at] = ring[at].Slot == slot ? (slot, ring[at].Count + 1) : (slot, 1);
        }
    }

    /// <summary>One more in the step <paramref name="step"/>; steps older than the window drop off the front.</summary>
    private static void Add(List<(long Step, int Count)> counts, long step)
    {
        if (counts.Count > 0 && counts[^1].Step == step)
        {
            counts[^1] = (step, counts[^1].Count + 1);
        }
        else
        {
            counts.Add((step, 1));
        }
        var oldest = step - Minutes / CountStep;
        var drop = 0;
        while (drop < counts.Count && counts[drop].Step <= oldest)
        {
            drop++;
        }
        counts.RemoveRange(0, drop);
    }

    /// <summary>Once a minute, forgets stations and relays not heard for the window, so memory follows the feed.</summary>
    private void Prune(long minute)
    {
        if (minute == _prunedAt)
        {
            return;
        }
        _prunedAt = minute;
        var cutoff = DateTimeOffset.UnixEpoch.AddMinutes(minute) - Window;
        foreach (var name in _stations.Where(s => s.Value.LastHeard < cutoff).Select(s => s.Key).ToList())
        {
            _stations.Remove(name);
        }
        var oldest = minute / CountStep - Minutes / CountStep;
        foreach (
            var name in _relays.Where(r => r.Value.Count == 0 || r.Value[^1].Step <= oldest).Select(r => r.Key).ToList()
        )
        {
            _relays.Remove(name);
        }
    }

    private Bucket BucketAt(long minute)
    {
        var bucket = _minutes[Index(minute, Minutes)];
        if (bucket.Minute != minute)
        {
            bucket.Reset(minute);
        }
        return bucket;
    }

    private static long MinuteOf(DateTimeOffset time) =>
        (long)Math.Floor((time - DateTimeOffset.UnixEpoch).TotalMinutes);

    private static int Index(long slot, int length) => (int)(((slot % length) + length) % length);

    private sealed class Bucket
    {
        public long Minute { get; private set; } = long.MinValue;

        public int Packets { get; set; }

        public int Problems { get; set; }

        public int New { get; set; }

        public Dictionary<string, int> BySource { get; } = new(StringComparer.Ordinal);

        public int[] Arrivals { get; } = new int[ArrivalKinds.Length];

        public int[] Kinds { get; } = new int[PacketKinds.Length];

        public void Reset(long minute)
        {
            Minute = minute;
            Packets = 0;
            Problems = 0;
            New = 0;
            BySource.Clear();
            Array.Clear(Arrivals);
            Array.Clear(Kinds);
        }
    }

    private sealed class Heard(DateTimeOffset firstHeard)
    {
        /// <summary>When it was first heard in the window: "new" for the new-stations tile.</summary>
        public DateTimeOffset FirstHeard { get; } = firstHeard;

        public DateTimeOffset LastHeard { get; set; }

        public long[] LastSlots { get; } = [.. PointSizes.Select(_ => long.MinValue)];

        /// <summary>Its packets a 5-minute step, one tally per way they arrived and kind, oldest first.</summary>
        public List<Tally> Tallies { get; } = [];

        /// <summary>One more packet in <paramref name="step"/>; steps older than the window drop off the front.</summary>
        public void Add(long step, ArrivalKind arrival, PacketKind kind, bool problem)
        {
            var i = Tallies.Count - 1;
            while (
                i >= 0
                && Tallies[i].Step == step
                && (Tallies[i].Arrival != (byte)arrival || Tallies[i].Kind != (byte)kind)
            )
            {
                i--;
            }
            if (i >= 0 && Tallies[i].Step == step)
            {
                Tallies[i] = Tallies[i] with
                {
                    Count = Tallies[i].Count + 1,
                    Problems = Tallies[i].Problems + (problem ? 1 : 0),
                };
            }
            else
            {
                Tallies.Add(new Tally(step, 1, problem ? 1 : 0, (byte)arrival, (byte)kind));
            }
            var oldest = step - Minutes / CountStep;
            var drop = 0;
            while (drop < Tallies.Count && Tallies[drop].Step <= oldest)
            {
                drop++;
            }
            Tallies.RemoveRange(0, drop);
        }

        /// <summary>Its packets from <paramref name="firstStep"/> on.</summary>
        public int Total(long firstStep) => Tallies.Where(t => t.Step >= firstStep).Sum(t => t.Count);
    }

    /// <summary>A station's packets in one 5-minute step that arrived one way and were one kind.</summary>
    private readonly record struct Tally(long Step, int Count, int Problems, byte Arrival, byte Kind);
}
