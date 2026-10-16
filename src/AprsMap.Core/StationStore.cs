// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.RegularExpressions;
using AprsMap.Contracts;
using AprsNet.Packets;
using AprsNet.Symbols;

namespace AprsMap.Core;

/// <summary>
/// One entry per station, object and item heard (docs/features/F1.3-station-store.md), and the map changes each
/// watcher has not collected yet. A watcher's pending changes hold one entry per name, so a slow watcher costs at most
/// one copy of the map, never a backlog of packets. Entries fade and leave by age (<see cref="Sweep"/>) and the oldest
/// go first beyond the cap, so memory stays bounded.
/// </summary>
// Duplicate packets (behavior 11) never reach the store: the engine drops them first (G05).
internal sealed partial class StationStore(int maxEntries = StationStore.DefaultMaxEntries)
{
    /// <summary>The most entries kept (behavior 12).</summary>
    public const int DefaultMaxEntries = 50_000;

    private const double KmhPerKnot = 1.852;
    private const double MetresPerFoot = 0.3048;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    // Telemetry definitions by the station they define (the message's addressee, F1.11 behavior 1), kept apart from
    // the entries: they may come from another callsign, and before the station is heard.
    private readonly Dictionary<string, (TelemetryProfile Profile, DateTimeOffset At)> _telemetryProfiles = new(
        StringComparer.Ordinal
    );

    // Least recently heard first, for the cap.
    private readonly LinkedList<Entry> _byLastHeard = new();
    private readonly List<StationWatcher> _watchers = [];
    private AprsPosition? _home;
    private StationAging _aging = StationAging.Default;

    // RF links over the last hour (F1.21 behavior 4): each packet's consecutive hops, oldest first, and the counts.
    private readonly Queue<(DateTimeOffset At, string From, string To)> _linkEvents = new();
    private readonly Dictionary<(string From, string To), int> _links = [];

    /// <summary>How long a packet counts toward a link (decision 1).</summary>
    internal static readonly TimeSpan LinkWindow = TimeSpan.FromHours(1);

    // The latest time the store has seen, for the View filter's "heard within" (F1.9).
    private DateTimeOffset _now;

    // The source of the packet being applied, under the lock.
    private string? _sourceId;

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
    public PacketAnomalies Apply(AprsPacket packet, DateTimeOffset receivedAt, string? sourceId = null)
    {
        var raw = packet.Raw.ToArray(); // as received, third-party wrapper and all
        if (packet.Payload is ThirdPartyPayload { Packet: { HasHeader: true } inner })
        {
            packet = inner;
        }
        if (packet.Source is not { } source)
        {
            return PacketAnomalies.None;
        }

        var anomalies = PacketAnomalies.None;
        lock (_gate)
        {
            _now = receivedAt > _now ? receivedAt : _now;
            _sourceId = sourceId; // several sources apply at once: only under the lock
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
                    return PacketAnomalies.None;
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
                    return PacketAnomalies.None;
            }

            var entry = Touch(source.Value, StationKind.Station, packet, receivedAt, raw);
            CountLinks(entry, receivedAt);
            NoteProblems(entry, packet, receivedAt);
            switch (packet.Payload)
            {
                case PositionedPayload positioned:
                    anomalies = CheckTrack(entry, packet, positioned, receivedAt); // F1.20, before the move
                    SetPosition(entry, positioned, receivedAt);
                    break;
                case TelemetryReport telemetry:
                    AddTelemetry(entry, telemetry, receivedAt); // F1.11
                    break;
                case TelemetryDefinition definition:
                    Define(definition, receivedAt); // F1.11 behavior 1: for the station it's addressed to
                    break;
                case WeatherReport report:
                    SetWeather(entry, report.Weather, receivedAt); // a weather report without a position (F1.10)
                    break;
                case StatusReport status:
                    entry.Status = status.Text;
                    entry.SetTags(status.Text, entry.Comment);
                    if (entry.Symbol is null && status.Symbol is { } symbol)
                    {
                        entry.Symbol = symbol.ToString();
                    }
                    break;
            }
            Publish(entry);
        }
        return anomalies;
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
        var trackBefore = now - TimeSpan.FromMinutes(aging.TrackMinutes);
        lock (_gate)
        {
            _aging = aging;
            _now = now > _now ? now : _now;
            ExpireLinks(now);
            foreach (var watcher in _watchers.Where(w => w.Filter?.HeardWithinMinutes is not null))
            {
                foreach (var entry in _entries.Values.Where(e => e.HasPosition))
                {
                    watcher.Recheck(entry, _now); // "heard within" lets go as time passes
                }
            }
            ForgetTelemetryProfiles(now);
            foreach (var entry in _entries.Values)
            {
                TrimTelemetry(entry, now);
                TrimProblems(entry, now);
                var old = 0;
                while (old < entry.Track.Count && entry.Track[old].Time < trackBefore)
                {
                    old++;
                }
                DropTrack(entry, old); // F1.5 behavior 1: none older than the track's age
            }

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

    /// <summary>
    /// <paramref name="packet"/>, a duplicate of a report first heard on another source, was heard on
    /// <paramref name="sourceId"/> too: its station's Seen on gains it (F1.25 behavior 9).
    /// </summary>
    public void SeenOn(AprsPacket packet, string sourceId)
    {
        if (packet.Payload is ThirdPartyPayload { Packet: { HasHeader: true } inner })
        {
            packet = inner;
        }
        var name = packet.Payload switch
        {
            ObjectReport report => report.Name,
            ItemReport report => report.Name,
            _ => packet.Source?.Value,
        };
        lock (_gate)
        {
            if (name is not null && _entries.TryGetValue(name, out var entry) && entry.FirstHeardVia != sourceId)
            {
                entry.SeenOn.Add(sourceId);
            }
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
                if (query.Filter is { IsEmpty: false } filter && !entry.Matches(filter, _now))
                {
                    continue; // the View filter applies to the list too (F1.9 behavior 6)
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

    /// <summary>
    /// Empties the track of <paramref name="name"/> except its latest point (F1.5 behavior 10); false when there's no
    /// such entry.
    /// </summary>
    public bool ClearTrack(string name)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                return false;
            }
            if (entry.Track.Count > 1)
            {
                entry.Track.RemoveRange(0, entry.Track.Count - 1);
                entry.TrackPaths.RemoveRange(0, entry.TrackPaths.Count - 1);
                foreach (var watcher in _watchers)
                {
                    watcher.ResetTrack(name, entry.Track);
                }
            }
            return true;
        }
    }

    /// <summary>
    /// How a packet of <paramref name="query"/>'s station travelled (F1.21 behavior 2), with each hop's last known
    /// position; null when there's no such station or track point.
    /// </summary>
    public PacketRoute? Route(RouteQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            if (!_entries.TryGetValue(query.Name, out var entry))
            {
                return null;
            }
            var path = query.Path ?? entry.Path;
            double? latitude = entry.Latitude;
            double? longitude = entry.Longitude;
            if (query.Path is null && query.TrackPoint is { } time)
            {
                var index = entry.Track.FindIndex(p => p.Time == time);
                if (index < 0)
                {
                    return null;
                }
                path = entry.TrackPaths[index];
                (latitude, longitude) = (entry.Track[index].Latitude, entry.Track[index].Longitude);
            }
            if (query is { Latitude: { } lat, Longitude: { } lon })
            {
                (latitude, longitude) = (lat, lon);
            }
            var (kind, names) = StationFacts.Route(path);
            var hops = new List<RouteHop>(names.Count);
            AprsPosition? last = latitude is { } la && longitude is { } lo ? new AprsPosition(la, lo) : null;
            foreach (var (name, isGateway) in names)
            {
                if (_entries.TryGetValue(name, out var hop) && hop.HasPosition)
                {
                    var at = new AprsPosition(hop.Latitude!.Value, hop.Longitude!.Value);
                    hops.Add(
                        new RouteHop(
                            name,
                            isGateway,
                            at.Latitude,
                            at.Longitude,
                            last is { } from ? Geodesy.DistanceKm(from, at) : null
                        )
                    );
                    last = at;
                }
                else
                {
                    hops.Add(new RouteHop(name, isGateway));
                }
            }
            return new PacketRoute(entry.Name, kind, latitude, longitude, hops);
        }
    }

    /// <summary>The track of <paramref name="name"/>, oldest first; empty when there's none.</summary>
    public IReadOnlyList<TrackPoint> GetTrack(string name)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(name, out var entry) ? [.. entry.Track] : [];
        }
    }

    /// <summary>How many entries there are, how many are stale, and how many have a track to clear (F1.19).</summary>
    public (int Entries, int Stale, int Tracks, int Telemetry) Counts()
    {
        lock (_gate)
        {
            return (
                _entries.Count,
                _entries.Values.Count(e => e.IsStale),
                _entries.Values.Count(e => e.Track.Count > 1),
                _entries.Values.Count(e => e.Telemetry is { Count: > 0 })
            );
        }
    }

    /// <summary>Removes every stale entry, telling each watcher (F1.19 behavior 1); returns how many went.</summary>
    public int ClearStale()
    {
        lock (_gate)
        {
            var stale = _entries.Values.Where(e => e.IsStale).ToList();
            foreach (var entry in stale)
            {
                Remove(entry);
            }
            return stale.Count;
        }
    }

    /// <summary>Empties every track except each latest point (F1.19 behavior 1); returns how many tracks it cleared.</summary>
    public int ClearTracks()
    {
        lock (_gate)
        {
            var cleared = 0;
            foreach (var entry in _entries.Values.Where(e => e.Track.Count > 1))
            {
                entry.Track.RemoveRange(0, entry.Track.Count - 1);
                entry.TrackPaths.RemoveRange(0, entry.TrackPaths.Count - 1);
                foreach (var watcher in _watchers)
                {
                    watcher.ResetTrack(entry.Name, entry.Track);
                }
                cleared++;
            }
            return cleared;
        }
    }

    /// <summary>Forgets every entry, telling each watcher they're gone (F0.10); returns how many there were.</summary>
    public int Clear()
    {
        lock (_gate)
        {
            var count = _entries.Count;
            foreach (var entry in _entries.Values.Where(e => e.HasPosition))
            {
                foreach (var watcher in _watchers)
                {
                    watcher.Remove(entry.Name);
                }
            }
            _entries.Clear();
            _byLastHeard.Clear();
            return count;
        }
    }

    /// <summary>Starts collecting changes for a new watcher and returns everything on the map so far.</summary>
    public StationWatcher Subscribe(out StationState[] snapshot) => Subscribe(out snapshot, out _);

    /// <summary>Starts collecting changes, with tracks, for a watcher that shows everything.</summary>
    public StationWatcher Subscribe(out StationState[] snapshot, out TrackUpdate[] tracks) =>
        Subscribe(null, out snapshot, out tracks);

    /// <summary>
    /// Starts collecting changes for a new watcher and returns everything on the map so far, with every track as a
    /// reset (F1.5: a late viewer gets the snapshot first).
    /// </summary>
    /// <remarks>Only what passes <paramref name="filter"/> (F1.9), and later only its changes.</remarks>
    public StationWatcher Subscribe(StationFilter? filter, out StationState[] snapshot, out TrackUpdate[] tracks)
    {
        var watcher = new StationWatcher(filter is { IsEmpty: false } ? filter : null);
        lock (_gate)
        {
            var shown = _entries.Values.Where(e => e.HasPosition && watcher.Wants(e, _now)).ToList();
            snapshot = [.. shown.Select(e => e.ToState(_home))];
            tracks =
            [
                .. shown.Where(e => e.Track.Count > 0).Select(e => new TrackUpdate(e.Name, [.. e.Track], Reset: true)),
            ];
            watcher.Showing(shown.Select(e => e.Name));
            _watchers.Add(watcher);
        }
        return watcher;
    }

    /// <summary>The values heard with their counts, for the View filter's lists (F1.9 behavior 2).</summary>
    public ViewFacets Facets()
    {
        lock (_gate)
        {
            static IReadOnlyList<Facet> Count(IEnumerable<string?> values) =>
                [
                    .. values
                        .OfType<string>()
                        .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new Facet(g.Key, g.Count()))
                        .OrderByDescending(f => f.Count)
                        .ThenBy(f => f.Value, StringComparer.OrdinalIgnoreCase),
                ];
            var entries = _entries.Values.Where(e => e.HasPosition).ToList();
            return new ViewFacets(
                Count(entries.Select(e => e.Category)),
                Count(entries.Select(e => e.DeviceClass)),
                Count(entries.Select(e => e.MicE?.ToString())),
                Count(entries.SelectMany(e => e.Tags))
            );
        }
    }

    /// <summary>The statistics tables over the stations held now (F1.22 behavior 10).</summary>
    public DashboardTables Statistics()
    {
        lock (_gate)
        {
            var entries = _entries.Values.ToList();
            static List<DashboardCount> Counts(IEnumerable<string> keys) =>
                [
                    .. keys.GroupBy(k => k, StringComparer.Ordinal)
                        .Select(g => new DashboardCount(g.Key, g.Count()))
                        .OrderByDescending(c => c.Count)
                        .ThenBy(c => c.Key, StringComparer.Ordinal),
                ];
            return new DashboardTables(
                entries.Count,
                [
                    .. entries
                        .Where(e => e.Kind == StationKind.Station)
                        .GroupBy(e => (e.Device?.Class, e.Device?.Vendor, e.Device?.Model))
                        .Select(g => new DashboardPlatform(g.Key.Class, g.Key.Vendor, g.Key.Model, g.Count()))
                        .OrderByDescending(p => p.Count)
                        .ThenBy(p => p.Vendor, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(p => p.Model, StringComparer.OrdinalIgnoreCase),
                ],
                [
                    .. entries
                        .Where(e => e.Symbol is { Length: 2 })
                        .GroupBy(e => e.Symbol!, StringComparer.Ordinal)
                        .Select(g =>
                        {
                            var symbol = AprsSymbolCatalog.Get(new AprsSymbol(g.Key[0], g.Key[1]));
                            return new DashboardSymbol(g.Key, symbol.Name, symbol.Category.ToString(), g.Count());
                        })
                        .OrderByDescending(s => s.Count)
                        .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
                ],
                Counts(entries.Select(e => e.Kind.ToString())),
                Counts(entries.Where(e => e.Kind == StationKind.Station).Select(e => e.Arrival.ToString()))
            );
        }
    }

    /// <summary>
    /// The weather stations heard since <paramref name="since"/> and the extremes of their latest readings (F1.22
    /// behavior 4.11).
    /// </summary>
    public DashboardWeather WeatherSince(DateTimeOffset since)
    {
        lock (_gate)
        {
            var reporting = _entries.Values.Where(e => e.LastHeard >= since && e.Weather is not null).ToList();
            var temperatures = reporting
                .Where(e => e.Weather!.Temperature is not null)
                .Select(e => new DashboardReading(e.Name, e.Weather!.Temperature!.Value))
                .ToList();
            var gusts = reporting
                .Where(e => e.Weather!.WindGust is not null)
                .Select(e => new DashboardReading(e.Name, e.Weather!.WindGust!.Value))
                .ToList();
            return new DashboardWeather(
                reporting.Count,
                temperatures.MinBy(t => t.Value),
                temperatures.MaxBy(t => t.Value),
                gusts.MaxBy(g => g.Value)
            );
        }
    }

    /// <summary>
    /// The longest direct RF hops among stations heard since <paramref name="since"/> whose IGate's position is known,
    /// farthest first (F1.22 behavior 4.10).
    /// </summary>
    public IReadOnlyList<DashboardHop> FarthestDirect(DateTimeOffset since, int count)
    {
        List<(string Name, DateTimeOffset At)> direct;
        lock (_gate)
        {
            direct =
            [
                .. _entries
                    .Values.Where(e => e.LastHeard >= since && e.Arrival == ArrivalKind.Direct && e.HasPosition)
                    .Select(e => (e.Name, e.LastHeard)),
            ];
        }
        return
        [
            .. direct
                .Select(d => (d.Name, d.At, Route: Route(new RouteQuery(d.Name))))
                .Where(d => d.Route is { Kind: ArrivalKind.Direct, Hops: [{ IsGateway: true, DistanceKm: not null }] })
                .Select(d => new DashboardHop(d.Name, d.Route!.Hops[0].Name, d.Route.Hops[0].DistanceKm!.Value, d.At))
                .OrderByDescending(h => h.DistanceKm)
                .Take(count),
        ];
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
        var senderEntry = Touch(sender, StationKind.Station, packet, receivedAt, raw);
        CountLinks(senderEntry, receivedAt);
        Publish(senderEntry);
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
        SetPosition(entry, report, receivedAt);
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
        entry.FirstHeardVia = _sourceId; // a new report: seen on its source alone, so far (F1.25 behavior 9)
        entry.Arrival = StationFacts.Route(entry.Path).Kind; // for the View filter's reception (F1.9)
        if (packet.Destination?.Value is { } destination && destination != entry.Destination)
        {
            entry.Destination = destination; // the device changes with the destination (or Mic-E's), not every packet
            var device = StationFacts.Device(packet);
            entry.DeviceClass = device?.Class;
            entry.Device = device; // for the dashboard's platforms (F1.22 behavior 10)
        }
        entry.SeenOn.Clear();
        return entry;
    }

    private void SetPosition(Entry entry, PositionedPayload positioned, DateTimeOffset receivedAt)
    {
        if (!positioned.IsPositionUnknown)
        {
            entry.Latitude = positioned.Position.Latitude;
            entry.Longitude = positioned.Position.Longitude;
            entry.Ambiguity = positioned.Position.Ambiguity;
            if (positioned.Position.Ambiguity == 0) // an ambiguous position isn't a point on a route (F1.5 behavior 2)
            {
                AddTrackPoint(entry, new TrackPoint(entry.Latitude.Value, entry.Longitude.Value, receivedAt));
            }
        }
        entry.Area = positioned.Area is { } area ? AreaOf(area) : entry.Area;
        if (positioned.Telemetry is { } comment)
        {
            AddTelemetry(entry, comment, receivedAt); // telemetry in a position's comment (F1.11)
        }
        entry.Messaging = positioned switch
        {
            PositionReport report => report.IsMessagingCapable,
            MicEReport micE => micE.IsMessagingCapable,
            _ => entry.Messaging,
        };
        entry.MicE = positioned is MicEReport { Message: var message } ? message : entry.MicE;
        entry.Category = AprsSymbolCatalog.Get(positioned.Symbol).Category.ToString();
        entry.SetTags(entry.Status, string.IsNullOrWhiteSpace(positioned.Comment) ? entry.Comment : positioned.Comment);
        entry.Signpost = positioned.Signpost ?? entry.Signpost;
        if (positioned.Weather is { } weather)
        {
            SetWeather(entry, weather, receivedAt);
            // A weather station's course and speed are the wind (APRS101 ch. 12), not how it moves.
            entry.Symbol = positioned.Symbol.ToString();
            entry.Comment = string.IsNullOrWhiteSpace(positioned.Comment) ? entry.Comment : positioned.Comment;
            entry.AltitudeMetres = positioned.AltitudeFeet is { } wxFeet
                ? wxFeet * MetresPerFoot
                : entry.AltitudeMetres;
            return;
        }
        entry.Symbol = positioned.Symbol.ToString();
        entry.Comment = string.IsNullOrWhiteSpace(positioned.Comment) ? entry.Comment : positioned.Comment;
        entry.Course = positioned.Course ?? entry.Course;
        entry.SpeedKmh = positioned.SpeedKnots is { } knots ? knots * KmhPerKnot : entry.SpeedKmh;
        entry.AltitudeMetres = positioned.AltitudeFeet is { } feet ? feet * MetresPerFoot : entry.AltitudeMetres;
    }

    /// <summary>The most telemetry reports a station keeps (F1.11 behavior 1).</summary>
    internal const int TelemetryReports = 100;

    /// <summary>The oldest telemetry report a station keeps.</summary>
    internal static readonly TimeSpan TelemetryAge = TimeSpan.FromHours(24);

    private static void AddTelemetry(Entry entry, TelemetryReport report, DateTimeOffset at)
    {
        // Kept as sent, so definitions that come later apply to it too (F1.11 behavior 3).
        (entry.Telemetry ??= []).Add((at, report));
        TrimTelemetry(entry, at);
    }

    /// <summary>A <c>PARM.</c>, <c>UNIT.</c>, <c>EQNS.</c> or <c>BITS.</c> replaces the earlier one of its kind (G09).</summary>
    private void Define(TelemetryDefinition definition, DateTimeOffset at)
    {
        var name = definition.Addressee.Trim();
        if (name.Length == 0)
        {
            return;
        }
        var profile = _telemetryProfiles.TryGetValue(name, out var held) ? held.Profile : TelemetryProfile.Empty;
        _telemetryProfiles[name] = (profile.With(definition), at);
    }

    /// <summary>Definitions for a station that isn't held, and not renewed for a day, are let go.</summary>
    private void ForgetTelemetryProfiles(DateTimeOffset now)
    {
        foreach (
            var name in _telemetryProfiles
                .Where(p => p.Value.At < now - TelemetryAge && !_entries.ContainsKey(p.Key))
                .Select(p => p.Key)
                .ToList()
        )
        {
            _telemetryProfiles.Remove(name);
        }
    }

    /// <summary>The station's held reports with its definitions applied, and what the definitions say.</summary>
    private TelemetryView ViewOf(string name, List<(DateTimeOffset At, TelemetryReport Report)> reports)
    {
        var defined = _telemetryProfiles.TryGetValue(name, out var held);
        var profile = defined ? held.Profile : TelemetryProfile.Empty;
        TelemetryReading? last = null;
        var samples = new List<TelemetrySample>(reports.Count);
        foreach (var (at, report) in reports)
        {
            last = profile.Apply(report);
            samples.Add(
                new TelemetrySample(
                    at,
                    string.IsNullOrEmpty(report.Sequence) ? null : report.Sequence,
                    [.. last.Analog.Select(a => a.Value)],
                    [.. last.Digital.Select(d => d.IsOn)]
                )
            );
        }
        return new TelemetryView(samples, defined && last is { } reading ? DefinitionsOf(profile, reading) : null);
    }

    private static TelemetryDefinitions DefinitionsOf(TelemetryProfile profile, TelemetryReading reading) =>
        new(
            [
                .. Enumerable
                    .Range(0, TelemetryProfile.AnalogChannels)
                    .Select(i =>
                        i < reading.Analog.Count
                            ? new TelemetryAnalogChannel(
                                Blank(reading.Analog[i].Name),
                                Blank(reading.Analog[i].Unit),
                                reading.Analog[i].IsScaled
                            )
                            : new TelemetryAnalogChannel(
                                Blank(profile.Names.ElementAtOrDefault(i)),
                                Blank(profile.Units.ElementAtOrDefault(i)),
                                i < profile.Equations.Count
                            )
                    ),
            ],
            [
                .. Enumerable
                    .Range(0, TelemetryProfile.DigitalChannels)
                    .Select(i => new TelemetryDigitalChannel(
                        Blank(profile.Names.ElementAtOrDefault(TelemetryProfile.AnalogChannels + i)),
                        Blank(profile.Units.ElementAtOrDefault(TelemetryProfile.AnalogChannels + i))
                    )),
            ],
            Blank(profile.ProjectName)
        );

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static void TrimTelemetry(Entry entry, DateTimeOffset now)
    {
        if (entry.Telemetry is not { } samples)
        {
            return;
        }
        var old = samples.FindIndex(s => s.At >= now - TelemetryAge);
        samples.RemoveRange(0, old < 0 ? samples.Count : old);
        if (samples.Count > TelemetryReports)
        {
            samples.RemoveRange(0, samples.Count - TelemetryReports);
        }
    }

    // Track anomaly limits (F1.20 behavior 1): APRS.NET's defaults, with 1,000 km/h for anything that isn't an
    // aircraft or balloon.
    private static readonly TrackAnalysisOptions AirLimits = TrackAnalysisOptions.Default;
    private static readonly TrackAnalysisOptions GroundLimits = TrackAnalysisOptions.Default with
    {
        MaxSpeedKmh = 1000,
    };

    /// <summary>The most recent reports a station keeps for the track analysis.</summary>
    internal const int TrackReportsKept = 50;

    /// <summary>
    /// Checks a station's new position report against its recent ones with APRS.NET's track analysis (G24), counts
    /// what it finds, and returns it so the packet can be marked.
    /// </summary>
    private static PacketAnomalies CheckTrack(
        Entry entry,
        AprsPacket packet,
        PositionedPayload positioned,
        DateTimeOffset at
    )
    {
        if (positioned.IsPositionUnknown || TrackReport.FromPacket(packet, at) is not { } report)
        {
            return PacketAnomalies.None;
        }
        var limits =
            AprsSymbolCatalog.Get(positioned.Symbol).Category == AprsSymbolCategory.Aircraft ? AirLimits : GroundLimits;
        var check = TrackAnalysis.Check(entry.Reports, report, limits);
        entry.Reports.Add(report);
        var keepAfter = at - limits.LateCopyInterval;
        var old = 0;
        while (
            old < entry.Reports.Count - 1
            && (entry.Reports[old].ReceivedAt < keepAfter || entry.Reports.Count - old > TrackReportsKept)
        )
        {
            old++;
        }
        entry.Reports.RemoveRange(0, old);
        var found = (PacketAnomalies)(int)check.Anomalies;
        foreach (
            var flag in Enum.GetValues<PacketAnomalies>().Where(f => f != PacketAnomalies.None && found.HasFlag(f))
        )
        {
            entry.Anomalies.Enqueue((at, KindOf(flag), ExampleOf(flag, check)));
        }
        TrimProblems(entry, at);
        return found;
    }

    private static string KindOf(PacketAnomalies flag) =>
        flag switch
        {
            PacketAnomalies.RepeatedPosition => StationProblem.RepeatedPosition,
            PacketAnomalies.ImpossibleSpeed => StationProblem.ImpossibleSpeed,
            PacketAnomalies.RapidRepeat => StationProblem.RapidRepeat,
            PacketAnomalies.TimeBackwards => StationProblem.TimeBackwards,
            _ => StationProblem.AmbiguityJump,
        };

    /// <summary>The figures behind a flag, as an engine error the UI words (<c>anomaly.*</c>).</summary>
    private static EngineError ExampleOf(PacketAnomalies flag, TrackCheck check)
    {
        var kind = KindOf(flag);
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (check.Interval is { } interval)
        {
            args["seconds"] = ((long)Math.Round(interval.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            args["milliseconds"] = ((long)Math.Round(interval.TotalMilliseconds)).ToString(
                CultureInfo.InvariantCulture
            );
        }
        if (check.DistanceKm is { } km)
        {
            args["distance"] = Math.Round(km, 1).ToString(CultureInfo.InvariantCulture);
        }
        if (check.SpeedKmh is { } speed && double.IsFinite(speed))
        {
            args["speed"] = Math.Round(speed).ToString(CultureInfo.InvariantCulture);
        }
        var message = flag switch
        {
            PacketAnomalies.RepeatedPosition => "The same position was sent again too soon, or as a late copy.",
            PacketAnomalies.ImpossibleSpeed => "Further from the previous position than the station could have gone.",
            PacketAnomalies.RapidRepeat => "Less than a second after the previous report.",
            PacketAnomalies.TimeBackwards => "The report's time is earlier than the previous report's.",
            _ => "The position moved only because its ambiguity changed.",
        };
        return new EngineError("anomaly." + kind, message, args);
    }

    /// <summary>How long a problem counts toward a station's (F1.20 behavior 3).</summary>
    internal static readonly TimeSpan ProblemWindow = TimeSpan.FromHours(1);

    /// <summary>A packet with parse errors counts as invalid for its station (F1.20 behavior 2).</summary>
    private static void NoteProblems(Entry entry, AprsPacket packet, DateTimeOffset at)
    {
        var errors = PacketSummarizer
            .Issues(packet)
            .Where(e => e.Arg(EngineErrorArgs.Severity) == EngineErrorArgs.SeverityError)
            .ToList();
        if (errors.Count > 0)
        {
            entry.Invalid.Enqueue((at, errors[0]));
        }
        TrimProblems(entry, at);
    }

    private static void TrimProblems(Entry entry, DateTimeOffset now)
    {
        while (entry.Invalid.TryPeek(out var oldest) && oldest.At < now - ProblemWindow)
        {
            entry.Invalid.Dequeue();
        }
        while (entry.Anomalies.TryPeek(out var oldest) && oldest.At < now - ProblemWindow)
        {
            entry.Anomalies.Dequeue();
        }
    }

    /// <summary>Counts each link the packet just heard from <paramref name="sender"/> crossed on RF (F1.21 behavior 4).</summary>
    private void CountLinks(Entry sender, DateTimeOffset at)
    {
        var (kind, hops) = StationFacts.Route(sender.Path);
        if (kind is not (ArrivalKind.Direct or ArrivalKind.Digipeated))
        {
            return;
        }
        var from = sender.Name;
        foreach (var (name, _) in hops)
        {
            _linkEvents.Enqueue((at, from, name));
            _links[(from, name)] = _links.GetValueOrDefault((from, name)) + 1;
            from = name;
        }
    }

    /// <summary>Lets links' packets older than the window go (F1.21 behavior 4).</summary>
    private void ExpireLinks(DateTimeOffset now)
    {
        while (_linkEvents.TryPeek(out var oldest) && oldest.At < now - LinkWindow)
        {
            _linkEvents.Dequeue();
            var key = (oldest.From, oldest.To);
            if (--_links[key] == 0)
            {
                _links.Remove(key);
            }
        }
    }

    /// <summary>The links over the last hour whose ends have known positions (F1.21 behavior 4).</summary>
    public IReadOnlyList<NetworkLink> Links()
    {
        lock (_gate)
        {
            ExpireLinks(_now);
            var links = new List<NetworkLink>(_links.Count);
            foreach (var ((from, to), count) in _links)
            {
                if (
                    _entries.TryGetValue(from, out var a)
                    && a.HasPosition
                    && _entries.TryGetValue(to, out var b)
                    && b.HasPosition
                )
                {
                    links.Add(
                        new NetworkLink(
                            from,
                            a.Latitude!.Value,
                            a.Longitude!.Value,
                            to,
                            b.Latitude!.Value,
                            b.Longitude!.Value,
                            count
                        )
                    );
                }
            }
            return links;
        }
    }

    /// <summary>A station's held telemetry (F1.11), or null without any.</summary>
    public TelemetryView? Telemetry(string name)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(name, out var entry) && entry.Telemetry is { Count: > 0 } reports
                ? ViewOf(name, reports)
                : null;
        }
    }

    /// <summary>Forgets every station's telemetry (F1.19); returns how many stations had some.</summary>
    public int ClearTelemetry()
    {
        lock (_gate)
        {
            var cleared = 0;
            foreach (var entry in _entries.Values.Where(e => e.Telemetry is { Count: > 0 }))
            {
                entry.Telemetry = null;
                cleared++;
            }
            _telemetryProfiles.Clear(); // the definitions go with the reports
            return cleared;
        }
    }

    /// <summary>An area object's shape for the map (F1.12 behavior 1).</summary>
    private static AreaInfo AreaOf(AreaShape area) =>
        new(
            area.Kind switch
            {
                AreaShapeKind.Circle => AreaKind.Circle,
                AreaShapeKind.Line => area.Type == 6 ? AreaKind.LineLeft : AreaKind.LineRight,
                AreaShapeKind.Ellipse => AreaKind.Ellipse,
                AreaShapeKind.Triangle => AreaKind.Triangle,
                _ => AreaKind.Box,
            },
            area.IsFilled,
            area.Color,
            area.LatitudeOffsetDegrees,
            area.LongitudeOffsetDegrees,
            area.CorridorMiles is { } miles ? AprsUnits.Convert(miles, LengthUnit.Miles, LengthUnit.Kilometers) : null
        );

    /// <summary>
    /// Keeps each reading the report carries, with its own time, converted to the engine's units (F1.10 behaviors 1,
    /// 2); readings it doesn't carry keep their last value.
    /// </summary>
    private static void SetWeather(Entry entry, WeatherData data, DateTimeOffset at)
    {
        var w = entry.Weather ?? new StationWeather();
        WeatherReading? Read(double? value) => value is { } v ? new WeatherReading(v, at) : null;
        double? Mm(double? inches) =>
            inches is { } i ? AprsUnits.Convert(i, LengthUnit.Inches, LengthUnit.Millimeters) : null;
        double? Ms(double? mph) =>
            mph is { } m
                ? AprsUnits.Convert(
                    m,
                    AprsNet.Packets.SpeedUnit.MilesPerHour,
                    AprsNet.Packets.SpeedUnit.MetersPerSecond
                )
                : null;
        entry.Weather = new StationWeather(
            Read(data.WindDirection) ?? w.WindDirection,
            Read(Ms(data.WindSpeedMph)) ?? w.WindSpeed,
            Read(Ms(data.WindGustMph)) ?? w.WindGust,
            Read(
                data.TemperatureF is { } f
                    ? AprsUnits.Convert(
                        f,
                        AprsNet.Packets.TemperatureUnit.Fahrenheit,
                        AprsNet.Packets.TemperatureUnit.Celsius
                    )
                    : null
            ) ?? w.Temperature,
            Read(Mm(data.RainLastHourInches)) ?? w.RainLastHour,
            Read(Mm(data.RainLast24HoursInches)) ?? w.RainLast24Hours,
            Read(Mm(data.RainSinceMidnightInches)) ?? w.RainSinceMidnight,
            Read(data.HumidityPercent) ?? w.Humidity,
            Read(data.PressureMillibars) ?? w.Pressure, // a millibar is a hectopascal
            Read(data.LuminosityWattsPerSquareMeter) ?? w.Luminosity,
            Read(Mm(data.SnowLast24HoursInches)) ?? w.Snow
        );
    }

    /// <summary>How close a report must be to the track's last point to count as not moving (F1.5 behavior 2).</summary>
    internal const double TrackMoveKm = 0.02;

    /// <summary>
    /// Adds a point to the entry's track (F1.5 behaviors 1, 2): a report within 20 m of the last point moves that
    /// point's time on instead, and the oldest points go beyond the track's length. Every point is joined until
    /// APRS.NET's track anomaly analysis (G24) can say which aren't (behavior 3).
    /// </summary>
    private void AddTrackPoint(Entry entry, TrackPoint point)
    {
        var track = entry.Track;
        if (
            track.Count > 0
            && Geodesy.DistanceKm(
                new AprsPosition(track[^1].Latitude, track[^1].Longitude),
                new AprsPosition(point.Latitude, point.Longitude)
            ) < TrackMoveKm
        )
        {
            track[^1] = track[^1] with { Time = point.Time };
            entry.TrackPaths[^1] = entry.Path;
            return;
        }
        track.Add(point);
        entry.TrackPaths.Add(entry.Path);
        foreach (var watcher in _watchers)
        {
            watcher.AddTrackPoint(entry.Name, point);
        }
        DropTrack(entry, track.Count - _aging.TrackPoints);
    }

    /// <summary>Drops the <paramref name="count"/> oldest points of the entry's track, telling every watcher.</summary>
    private void DropTrack(Entry entry, int count)
    {
        if (count <= 0)
        {
            return;
        }
        entry.Track.RemoveRange(0, count);
        entry.TrackPaths.RemoveRange(0, count);
        foreach (var watcher in _watchers)
        {
            watcher.DropTrackPoints(entry.Name, count);
        }
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
            watcher.Change(entry, state, _now);
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

    /// <summary>A <c>!word!</c> or <c>#word</c> tag (F1.9 behavior 1.8).</summary>
    [GeneratedRegex(@"![A-Za-z0-9_-]{2,20}!|(?<![\w&])#[A-Za-z][A-Za-z0-9_-]{1,19}")]
    private static partial Regex TagPattern();

    /// <summary>One entry. Only touched under the store's lock.</summary>
    internal sealed class Entry(string name, StationKind kind)
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

        /// <summary>The source the latest report arrived on first.</summary>
        public string? FirstHeardVia { get; set; }

        /// <summary>Other sources the latest report was heard on.</summary>
        public SortedSet<string> SeenOn { get; } = new(StringComparer.Ordinal);

        /// <summary>Whether its position says it takes messages (F1.9).</summary>
        public bool Messaging { get; set; }

        /// <summary>Its Mic-E status, when it sends Mic-E (F1.9).</summary>
        public MicEMessage? MicE { get; set; }

        /// <summary>Its symbol's category, APRS.NET's name for it (F1.9).</summary>
        public string? Category { get; set; }

        /// <summary>How its last packet arrived (F1.9).</summary>
        public ArrivalKind Arrival { get; set; }

        /// <summary>The destination its device was identified from, so it's identified again only when that changes.</summary>
        public string? Destination { get; set; }

        /// <summary>Its device's class, from the aprs-deviceid list (F1.9).</summary>
        public string? DeviceClass { get; set; }

        /// <summary>The device's vendor, model and class from the destination or Mic-E code (G07); null when unknown.</summary>
        public StationDevice? Device { get; set; }

        /// <summary><c>!word!</c> and <c>#word</c> tags in its comment and status, as written (F1.9 behavior 1.8).</summary>
        public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>An emergency: Mic-E emergency, or the emergency symbol (F1.9 behavior 3).</summary>
        public bool IsEmergency => MicE == MicEMessage.Emergency || Symbol == "\\!";

        public void SetTags(string? status, string? comment)
        {
            Tags.Clear();
            foreach (var text in new[] { status, comment })
            {
                if (text is null)
                {
                    continue;
                }
                foreach (Match match in TagPattern().Matches(text))
                {
                    Tags.Add(match.Value);
                }
            }
        }

        /// <summary>Whether <paramref name="filter"/> shows it at <paramref name="now"/> (F1.9 behaviors 1, 3).</summary>
        public bool Matches(StationFilter filter, DateTimeOffset now)
        {
            if (IsEmergency)
            {
                return true;
            }
            return filter.Kinds switch
                {
                    StationKinds.Stations => Kind == StationKind.Station,
                    StationKinds.ObjectsAndItems => Kind != StationKind.Station,
                    _ => true,
                }
                && (filter.Categories is not { Count: > 0 } categories || categories.Contains(Category ?? string.Empty))
                && (filter.HeardWithinMinutes is not { } minutes || now - LastHeard <= TimeSpan.FromMinutes(minutes))
                && filter.Reception switch
                {
                    Reception.Direct => Arrival == ArrivalKind.Direct,
                    Reception.Digipeated => Arrival == ArrivalKind.Digipeated,
                    Reception.AnyRf => Arrival is ArrivalKind.Direct or ArrivalKind.Digipeated,
                    Reception.Internet => Arrival == ArrivalKind.Internet,
                    _ => true,
                }
                && (!filter.MessageCapable || Messaging)
                && (!filter.WithWeather || Weather is not null)
                && (!filter.WithTrack || Track.Count > 1)
                && (!filter.WithTelemetry || Telemetry is { Count: > 0 })
                && (!filter.HasProblems || HasProblems)
                && (filter.DeviceClasses is not { Count: > 0 } classes || classes.Contains(DeviceClass ?? string.Empty))
                && (
                    filter.MicEStatuses is not { Count: > 0 } statuses
                    || MicE is { } status && statuses.Contains(status.ToString())
                )
                && (filter.Tags is not { Count: > 0 } tags || tags.Any(Tags.Contains));
        }

        /// <summary>The shape it draws, for an area object (F1.12).</summary>
        public AreaInfo? Area { get; set; }

        /// <summary>What it shows, for a signpost (F1.12).</summary>
        public string? Signpost { get; set; }

        /// <summary>Its invalid packets over the last hour, oldest first, each with its first error (F1.20).</summary>
        public Queue<(DateTimeOffset At, EngineError Issue)> Invalid { get; } = new();

        /// <summary>Track anomalies over the last hour, oldest first (F1.20 behavior 3).</summary>
        public Queue<(DateTimeOffset At, string Kind, EngineError Example)> Anomalies { get; } = new();

        /// <summary>The station's recent position reports, for the track analysis (F1.20 behavior 1).</summary>
        public List<TrackReport> Reports { get; } = [];

        /// <summary>Whether its packets had problems in the last hour (F1.20).</summary>
        public bool HasProblems => Invalid.Count > 0 || Anomalies.Count > 0;

        /// <summary>Its telemetry reports, oldest first (F1.11), once it has sent any.</summary>
        public List<(DateTimeOffset At, TelemetryReport Report)>? Telemetry { get; set; }

        /// <summary>Its latest weather readings (F1.10), once it has sent any.</summary>
        public StationWeather? Weather { get; set; }

        /// <summary>Where it has been, oldest first (F1.5).</summary>
        public List<TrackPoint> Track { get; } = [];

        /// <summary>The path of the packet behind each track point, for its route (F1.21 behavior 2).</summary>
        public List<string?> TrackPaths { get; } = [];

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
                bearing,
                Weather is { } w
                    ? new WeatherSummary(
                        w.Temperature?.Value,
                        w.WindDirection is { } d ? (int)d.Value : null,
                        w.WindSpeed?.Value
                    )
                    : null,
                Area,
                Signpost
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
                HasPosition,
                HasProblems
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
                text,
                FirstHeardVia,
                SeenOn.Count == 0 ? null : [.. SeenOn],
                Weather,
                HasProblems ? Problems() : null
            );
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind} {Name}");

        /// <summary>Each kind of problem in the last hour, with its count and latest example (F1.20 behavior 5).</summary>
        private List<StationProblem> Problems()
        {
            var problems = new List<StationProblem>();
            if (Invalid.Count > 0)
            {
                problems.Add(new StationProblem(StationProblem.Invalid, Invalid.Count, Invalid.Last().Issue));
            }
            problems.AddRange(
                Anomalies
                    .GroupBy(a => a.Kind, StringComparer.Ordinal)
                    .Select(g => new StationProblem(g.Key, g.Count(), g.Last().Example))
            );
            return problems;
        }
    }

    /// <summary>One watcher's uncollected changes. Only touched under the store's lock.</summary>
    internal sealed class StationWatcher(StationFilter? filter = null)
    {
        private readonly Dictionary<string, StationState> _changed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingTrack> _tracks = new(StringComparer.Ordinal);

        // With a filter: the names this viewer is shown, so one that stops matching can be taken off (F1.9).
        private readonly HashSet<string> _shown = new(StringComparer.Ordinal);

        /// <summary>The View filter, or null to show everything.</summary>
        public StationFilter? Filter { get; } = filter;

        internal bool Wants(Entry entry, DateTimeOffset now) => Filter is null || entry.Matches(Filter, now);

        internal void Showing(IEnumerable<string> names) => _shown.UnionWith(names);

        private bool Shows(string name) => Filter is null || _shown.Contains(name);

        /// <summary>The entry changed: sent if it matches; taken off if it stopped; sent with its track if it started.</summary>
        internal void Change(Entry entry, StationState state, DateTimeOffset now)
        {
            if (Filter is null)
            {
                Change(state);
                return;
            }
            if (entry.Matches(Filter, now))
            {
                if (_shown.Add(entry.Name))
                {
                    ResetTrack(entry.Name, entry.Track);
                }
                Change(state);
            }
            else if (_shown.Contains(entry.Name))
            {
                Remove(entry.Name);
            }
        }

        /// <summary>Takes the entry off if it no longer matches (time-based conditions).</summary>
        internal void Recheck(Entry entry, DateTimeOffset now)
        {
            if (Filter is not null && _shown.Contains(entry.Name) && !entry.Matches(Filter, now))
            {
                Remove(entry.Name);
            }
        }

        public void Change(StationState state)
        {
            _removed.Remove(state.Name);
            _changed[state.Name] = state;
        }

        public void Remove(string name)
        {
            if (Filter is not null && !_shown.Remove(name))
            {
                return; // never shown to this viewer
            }
            _changed.Remove(name);
            _removed.Add(name);
            // The viewer drops the track with the station; one heard again in the same batch starts a new track.
            _tracks[name] = new PendingTrack { Reset = true };
        }

        public void AddTrackPoint(string name, TrackPoint point)
        {
            if (Shows(name))
            {
                Track(name).Added.Add(point);
            }
        }

        public void DropTrackPoints(string name, int count)
        {
            if (Shows(name))
            {
                Track(name).Dropped += count;
            }
        }

        public void ResetTrack(string name, IEnumerable<TrackPoint> points)
        {
            if (Filter is null || _shown.Contains(name))
            {
                _tracks[name] = new PendingTrack { Reset = true, Added = [.. points] };
            }
        }

        public StationBatch? Drain()
        {
            if (_changed.Count == 0 && _removed.Count == 0 && _tracks.Count == 0)
            {
                return null;
            }
            List<TrackUpdate>? tracks = null;
            foreach (var (name, pending) in _tracks)
            {
                if (!_removed.Contains(name))
                {
                    (tracks ??= []).Add(new TrackUpdate(name, pending.Added, pending.Dropped, pending.Reset));
                }
            }
            var batch = new StationBatch([.. _changed.Values], [.. _removed], tracks);
            _changed.Clear();
            _removed.Clear();
            _tracks.Clear();
            return batch;
        }

        private PendingTrack Track(string name)
        {
            if (!_tracks.TryGetValue(name, out var pending))
            {
                _tracks[name] = pending = new PendingTrack();
            }
            return pending;
        }

        /// <summary>
        /// A track's changes since the last drain. Applied as reset, then added, then dropped from the oldest end, they
        /// give the store's track: drops only ever take the oldest points, so they commute with later additions.
        /// </summary>
        private sealed class PendingTrack
        {
            public bool Reset { get; init; }
            public List<TrackPoint> Added { get; init; } = [];
            public int Dropped { get; set; }
        }
    }
}
