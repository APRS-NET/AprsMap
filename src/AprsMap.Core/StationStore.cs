// SPDX-License-Identifier: MIT
using System.Globalization;
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// One entry per station, object and item heard (docs/features/F1.3-station-store.md), and the map changes each
/// watcher has not collected yet. A watcher's pending changes hold one entry per name, so a slow watcher costs at most
/// one copy of the map, never a backlog of packets. Entries fade and leave by age (<see cref="Sweep"/>) and the oldest
/// go first beyond the cap, so memory stays bounded.
/// </summary>
// Duplicate packets (behavior 11) never reach the store: the engine drops them first (G05).
internal sealed class StationStore(int maxEntries = StationStore.DefaultMaxEntries)
{
    /// <summary>The most entries kept (behavior 12).</summary>
    public const int DefaultMaxEntries = 50_000;

    private const double KmhPerKnot = 1.852;
    private const double MetresPerFoot = 0.3048;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    // Least recently heard first, for the cap.
    private readonly LinkedList<Entry> _byLastHeard = new();
    private readonly List<StationWatcher> _watchers = [];
    private AprsPosition? _home;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Records <paramref name="packet"/> (which has a header) received at <paramref name="receivedAt"/>: every packet
    /// updates its station's entry, positioned objects and items their own, and a kill removes one. A third-party packet
    /// updates the station inside it, not the gateway that wrapped it.
    /// </summary>
    public void Apply(AprsPacket packet, DateTimeOffset receivedAt)
    {
        var raw = packet.Raw.ToArray(); // as received, third-party wrapper and all
        if (packet.Payload is ThirdPartyPayload { Packet: { HasHeader: true } inner })
        {
            packet = inner;
        }
        if (packet.Source is not { } source)
        {
            return;
        }

        lock (_gate)
        {
            switch (packet.Payload)
            {
                case ObjectReport report:
                    ApplyNamed(
                        report.Name,
                        StationKind.Object,
                        report.IsLive,
                        report,
                        packet,
                        source.Value,
                        receivedAt,
                        raw
                    );
                    return;
                case ItemReport report:
                    ApplyNamed(
                        report.Name,
                        StationKind.Item,
                        report.IsLive,
                        report,
                        packet,
                        source.Value,
                        receivedAt,
                        raw
                    );
                    return;
            }

            var entry = Touch(source.Value, StationKind.Station, packet, receivedAt, raw);
            switch (packet.Payload)
            {
                case PositionedPayload positioned:
                    SetPosition(entry, positioned);
                    break;
                case StatusReport status:
                    entry.Status = status.Text;
                    if (entry.Symbol is null && status.Symbol is { } symbol)
                    {
                        entry.Symbol = symbol.ToString();
                    }
                    break;
            }
            Publish(entry);
        }
    }

    /// <summary>
    /// The once-a-minute pass (behavior 10): entries not heard for <see cref="StationAging.StaleAfterMinutes"/> become
    /// stale, and those not heard for <see cref="StationAging.RemoveAfterMinutes"/> are removed, with each change
    /// streamed.
    /// </summary>
    public void Sweep(DateTimeOffset now, StationAging aging)
    {
        ArgumentNullException.ThrowIfNull(aging);
        var staleBefore = now - TimeSpan.FromMinutes(aging.StaleAfterMinutes);
        DateTimeOffset? removeBefore = aging.RemoveAfterMinutes is { } minutes
            ? now - TimeSpan.FromMinutes(minutes)
            : null;
        lock (_gate)
        {
            // Oldest first, so the pass can stop at the first entry heard recently enough.
            for (var node = _byLastHeard.First; node is not null; )
            {
                var next = node.Next;
                var entry = node.Value;
                if (entry.LastHeard >= staleBefore)
                {
                    break;
                }
                if (removeBefore is { } cutoff && entry.LastHeard < cutoff)
                {
                    Remove(entry);
                }
                else if (!entry.IsStale)
                {
                    entry.IsStale = true;
                    Publish(entry);
                }
                node = next;
            }
        }
    }

    /// <summary>The full entry for <paramref name="name"/>, or null when there is none.</summary>
    public StationDetail? Get(string name)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(name, out var entry) ? entry.ToDetail(_home) : null;
        }
    }

    /// <summary>The longest comment start a station list row carries.</summary>
    internal const int RowCommentLength = 80;

    /// <summary>
    /// One page of the station list (F1.7): entries whose name contains the search, of the kinds asked for, in order.
    /// Only the page's rows are built, so a query over the whole store costs one pass and one sort.
    /// </summary>
    public StationPage Query(StationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var offset = Math.Max(0, query.Offset);
        var count = Math.Clamp(query.Count, 0, StationQuery.MaxCount);
        lock (_gate)
        {
            var home = _home;
            var matches = new List<(Entry Entry, double Distance)>();
            foreach (var entry in _entries.Values)
            {
                if (
                    query.Kinds switch
                    {
                        StationKinds.Stations => entry.Kind != StationKind.Station,
                        StationKinds.ObjectsAndItems => entry.Kind == StationKind.Station,
                        _ => false,
                    }
                )
                {
                    continue;
                }
                if (search is not null && !entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var distance =
                    query.Sort == StationSort.Distance && home is { } from && entry.HasPosition
                        ? Geodesy.DistanceKm(from, new AprsPosition(entry.Latitude!.Value, entry.Longitude!.Value))
                        : double.PositiveInfinity;
                matches.Add((entry, distance));
            }
            Comparison<(Entry Entry, double Distance)> order = query.Sort switch
            {
                StationSort.Name => (a, b) => string.CompareOrdinal(a.Entry.Name, b.Entry.Name),
                StationSort.Distance => (a, b) =>
                    a.Distance.CompareTo(b.Distance) is var c && c != 0
                        ? c
                        : string.CompareOrdinal(a.Entry.Name, b.Entry.Name),
                _ => (a, b) =>
                    b.Entry.LastHeard.CompareTo(a.Entry.LastHeard) is var c && c != 0
                        ? c
                        : string.CompareOrdinal(a.Entry.Name, b.Entry.Name),
            };
            matches.Sort(order);
            var rows = matches.Skip(offset).Take(count).Select(m => m.Entry.ToRow(home)).ToArray();
            return new StationPage(rows, matches.Count);
        }
    }

    /// <summary>
    /// Measures distance and bearing from my location (F1.13 behavior 4), or not with nulls. A change sends every
    /// station on the map again, with its new distance.
    /// </summary>
    public void SetHome(double? latitude, double? longitude)
    {
        AprsPosition? home = latitude is { } lat && longitude is { } lon ? new AprsPosition(lat, lon) : null;
        lock (_gate)
        {
            if (home == _home)
            {
                return;
            }
            _home = home;
            foreach (var entry in _entries.Values)
            {
                Publish(entry);
            }
        }
    }

    /// <summary>Forgets every entry, telling each watcher they're gone (F0.10).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values.Where(e => e.HasPosition))
            {
                foreach (var watcher in _watchers)
                {
                    watcher.Remove(entry.Name);
                }
            }
            _entries.Clear();
            _byLastHeard.Clear();
        }
    }

    /// <summary>Starts collecting changes for a new watcher and returns everything on the map so far.</summary>
    public StationWatcher Subscribe(out StationState[] snapshot)
    {
        var watcher = new StationWatcher();
        lock (_gate)
        {
            snapshot = [.. _entries.Values.Where(e => e.HasPosition).Select(e => e.ToState(_home))];
            _watchers.Add(watcher);
        }
        return watcher;
    }

    public void Unsubscribe(StationWatcher watcher)
    {
        lock (_gate)
        {
            _watchers.Remove(watcher);
        }
    }

    /// <summary>The watcher's changes since its last call, or <see langword="null"/> when nothing changed.</summary>
    public StationBatch? Drain(StationWatcher watcher)
    {
        lock (_gate)
        {
            return watcher.Drain();
        }
    }

    private void ApplyNamed(
        string name,
        StationKind kind,
        bool live,
        PositionedPayload report,
        AprsPacket packet,
        string sender,
        DateTimeOffset receivedAt,
        byte[] raw
    )
    {
        // The sender was heard too, whatever the object says.
        Publish(Touch(sender, StationKind.Station, packet, receivedAt, raw));
        if (!live)
        {
            // A kill removes it, whoever sent the kill (behavior 6); a later live packet brings it back.
            if (_entries.TryGetValue(name, out var killed) && killed.Kind != StationKind.Station)
            {
                Remove(killed);
            }
            return;
        }
        if (_entries.TryGetValue(name, out var existing) && existing.Kind == StationKind.Station)
        {
            return; // a station by that name heard already: a station's callsign isn't taken over by an object
        }
        var entry = Touch(name, kind, packet, receivedAt, raw);
        if (entry.Owner is { } owner && owner != sender)
        {
            entry.PreviousOwner = owner; // ownership moves (behavior 5)
        }
        entry.Owner = sender;
        SetPosition(entry, report);
        Publish(entry);
    }

    /// <summary>The entry for <paramref name="name"/>, created if new, marked heard now.</summary>
    private Entry Touch(string name, StationKind kind, AprsPacket packet, DateTimeOffset receivedAt, byte[] raw)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            if (_entries.Count >= maxEntries && _byLastHeard.First is { } oldest)
            {
                Remove(oldest.Value); // the cap: the least recently heard goes first (behavior 12)
            }
            entry = new Entry(name, kind);
            entry.Node = _byLastHeard.AddLast(entry);
            _entries[name] = entry;
        }
        else
        {
            _byLastHeard.Remove(entry.Node!);
            _byLastHeard.AddLast(entry.Node!);
        }
        entry.LastHeard = receivedAt;
        entry.IsStale = false;
        entry.Packets++;
        entry.Path = packet.Path is { } path ? string.Join(',', path.Select(p => p.ToString())) : null;
        entry.LastPacket = raw;
        return entry;
    }

    private static void SetPosition(Entry entry, PositionedPayload positioned)
    {
        if (!positioned.IsPositionUnknown)
        {
            entry.Latitude = positioned.Position.Latitude;
            entry.Longitude = positioned.Position.Longitude;
            entry.Ambiguity = positioned.Position.Ambiguity;
        }
        entry.Symbol = positioned.Symbol.ToString();
        entry.Comment = string.IsNullOrWhiteSpace(positioned.Comment) ? entry.Comment : positioned.Comment;
        entry.Course = positioned.Course ?? entry.Course;
        entry.SpeedKmh = positioned.SpeedKnots is { } knots ? knots * KmhPerKnot : entry.SpeedKmh;
        entry.AltitudeMetres = positioned.AltitudeFeet is { } feet ? feet * MetresPerFoot : entry.AltitudeMetres;
    }

    /// <summary>Queues the entry's map state for every watcher; entries without a position aren't on the map.</summary>
    private void Publish(Entry entry)
    {
        if (!entry.HasPosition)
        {
            return;
        }
        var state = entry.ToState(_home);
        foreach (var watcher in _watchers)
        {
            watcher.Change(state);
        }
    }

    private void Remove(Entry entry)
    {
        _entries.Remove(entry.Name);
        _byLastHeard.Remove(entry.Node!);
        if (entry.HasPosition)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Remove(entry.Name);
            }
        }
    }

    /// <summary>One entry. Only touched under the store's lock.</summary>
    private sealed class Entry(string name, StationKind kind)
    {
        public string Name { get; } = name;
        public StationKind Kind { get; } = kind;
        public LinkedListNode<Entry>? Node { get; set; }
        public DateTimeOffset LastHeard { get; set; }
        public bool IsStale { get; set; }
        public long Packets { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public int Ambiguity { get; set; }
        public string? Symbol { get; set; }
        public string? Comment { get; set; }
        public string? Status { get; set; }
        public int? Course { get; set; }
        public double? SpeedKmh { get; set; }
        public double? AltitudeMetres { get; set; }
        public string? Path { get; set; }
        public string? Owner { get; set; }
        public string? PreviousOwner { get; set; }

        /// <summary>The last packet's bytes as received; the details work out its device and route from them.</summary>
        public byte[]? LastPacket { get; set; }

        public bool HasPosition => Latitude is not null && Longitude is not null;

        public StationState ToState(AprsPosition? home)
        {
            var (distance, bearing) = From(home);
            return new(
                Name,
                LastHeard,
                Latitude!.Value,
                Longitude!.Value,
                Symbol ?? string.Empty,
                Kind,
                IsStale,
                distance,
                bearing
            );
        }

        /// <summary>Distance in km and whole-degree bearing from <paramref name="home"/>; nulls when either is unknown.</summary>
        private (double?, int?) From(AprsPosition? home)
        {
            if (home is not { } from || !HasPosition)
            {
                return (null, null);
            }
            var to = new AprsPosition(Latitude!.Value, Longitude!.Value);
            return (
                Geodesy.DistanceKm(from, to),
                (int)Math.Round(Geodesy.InitialBearing(from, to), MidpointRounding.AwayFromZero) % 360
            );
        }

        public StationRow ToRow(AprsPosition? home)
        {
            var (distance, bearing) = From(home);
            var text = Comment ?? Status;
            return new(
                Name,
                Kind,
                Symbol,
                LastHeard,
                IsStale,
                distance,
                bearing,
                text is { Length: > RowCommentLength } ? text[..RowCommentLength] : text,
                HasPosition
            );
        }

        public StationDetail ToDetail(AprsPosition? home)
        {
            var (distance, bearing) = From(home);
            var (device, arrival, text) = StationFacts.Of(LastPacket);
            return new(
                Name,
                Kind,
                LastHeard,
                Packets,
                IsStale,
                Latitude,
                Longitude,
                Ambiguity,
                Symbol,
                Comment,
                Status,
                Course,
                SpeedKmh,
                AltitudeMetres,
                Path,
                Owner,
                PreviousOwner,
                distance,
                bearing,
                HasPosition ? MaidenheadLocator.FromPosition(Latitude!.Value, Longitude!.Value, 6).Value : null,
                device,
                arrival,
                text
            );
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind} {Name}");
    }

    /// <summary>One watcher's uncollected changes. Only touched under the store's lock.</summary>
    internal sealed class StationWatcher
    {
        private readonly Dictionary<string, StationState> _changed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

        public void Change(StationState state)
        {
            _removed.Remove(state.Name);
            _changed[state.Name] = state;
        }

        public void Remove(string name)
        {
            _changed.Remove(name);
            _removed.Add(name);
        }

        public StationBatch? Drain()
        {
            if (_changed.Count == 0 && _removed.Count == 0)
            {
                return null;
            }
            var batch = new StationBatch([.. _changed.Values], [.. _removed]);
            _changed.Clear();
            _removed.Clear();
            return batch;
        }
    }
}
