// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Beacons;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>What the beaconer needs from the engine: the station's state now and a way to send.</summary>
internal interface IBeaconHost
{
    /// <summary>
    /// The active profile, why beacons must wait (Transmit off, a profile that may not transmit, no position, a position
    /// not yet confirmed), and the profile's TX-enabled sources with whether each is RF.
    /// </summary>
    (StationProfile? Profile, EngineError? Held, IReadOnlyList<(string Id, bool IsRf)> Sources) BeaconState();

    /// <summary>Whether a packet can go out on <paramref name="sourceId"/> now.</summary>
    bool CanSendOn(string sourceId);

    /// <summary>The fresh fix from <paramref name="profile"/>'s GPS, for course and speed (F3.6 behavior 7.1); or null.</summary>
    GpsFix? Motion(StationProfile profile);

    /// <summary>Sends <paramref name="payload"/> on <paramref name="sourceId"/>, listed as Sent; false when it failed.</summary>
    Task<bool> SendAsync(string sourceId, AprsPayload payload, CancellationToken cancellationToken);

    /// <summary>
    /// Killed objects that have gone out killed <see cref="OwnObject.KillSends"/> times on each of their sources: the
    /// engine takes them out of the profile (docs/features/F2.7-objects-and-items.md, behavior 8).
    /// </summary>
    Task ObjectsGoneAsync(IReadOnlyCollection<string> ids);
}

/// <summary>
/// The active profile's position and status beacons (docs/features/F2.2-beacons.md) and its objects
/// (docs/features/F2.7-objects-and-items.md) on APRS.NET's beacon schedules: one schedule per beacon or object and
/// source, the spacing rules on top, and nothing sent while any F2.1 layer says no.
/// </summary>
internal sealed class Beaconer : IDisposable
{
    /// <summary>The least time between position beacons on one TNC (behavior 6).</summary>
    public static readonly TimeSpan RfFloor = TimeSpan.FromMinutes(10);

    /// <summary>The least time between two beacons on one source (behavior 6).</summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A decay sequence's first wait on APRS-IS (behavior 6). On a TNC it's <see cref="RfFloor"/>: the floor would hold
    /// back any shorter step anyway.
    /// </summary>
    public static readonly TimeSpan IsFirstStep = TimeSpan.FromSeconds(30);

    /// <summary>The longest random wait before a first beacon (behavior 8).</summary>
    public static readonly TimeSpan MaxStartWait = TimeSpan.FromMinutes(3);

    /// <summary>An object's first decay step on a TNC (F2.7 behavior 5); 30 seconds on APRS-IS, as positions.</summary>
    public static readonly TimeSpan ObjectRfFirstStep = TimeSpan.FromMinutes(2);

    /// <summary>How often a beacon that's due but can't go out yet is tried again.</summary>
    public static readonly TimeSpan RetryWait = TimeSpan.FromSeconds(5);

    private readonly IBeaconHost _host;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _ticking = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _timer;
    private readonly Dictionary<(BeaconKind Kind, string SourceId, string? ObjectId), Entry> _entries = [];
    private readonly Dictionary<string, DateTimeOffset> _lastBeacon = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastPosition = new(StringComparer.Ordinal);
    private int _disposed;
    private NextBeacon? _next;
    private EngineError? _held;

    private sealed class Entry(
        BeaconKind kind,
        string sourceId,
        bool isRf,
        BeaconSchedule schedule,
        DateTimeOffset notBefore,
        string signature,
        string? objectId = null
    )
    {
        public BeaconKind Kind { get; } = kind;
        public string SourceId { get; } = sourceId;
        public bool IsRf { get; } = isRf;
        public BeaconSchedule Schedule { get; } = schedule;
        public DateTimeOffset NotBefore { get; } = notBefore;

        /// <summary>What it was made from: a different one makes it again, so a change starts its schedule over.</summary>
        public string Signature { get; } = signature;

        /// <summary>The object it sends, for <see cref="BeaconKind.PlacedObject"/>.</summary>
        public string? ObjectId { get; } = objectId;

        /// <summary>How many times a killed object has gone out killed here.</summary>
        public int KillsSent { get; set; }
    }

    public Beaconer(IBeaconHost host, TimeProvider time, Random? random = null)
    {
        _host = host;
        _time = time;
        _random = random ?? Random.Shared;
        _timer = time.CreateTimer(_ => Poke(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The next beacon and why beacons are waiting, for the status bar (behavior 10).</summary>
    public (NextBeacon? Next, EngineError? Held) Status
    {
        get
        {
            lock (_gate)
            {
                return (_next, _held);
            }
        }
    }

    /// <summary>Looks again now: the settings, the Transmit switch or a source changed.</summary>
    public void Poke() => _ = TickAsync();

    /// <summary>
    /// The GPS has a new fix (docs/features/F3.6-gps.md): moving schedules take its speed and course, which can make a
    /// beacon due at once (a turn, or speeding up), and a schedule starts or stops moving as the fix comes and goes.
    /// </summary>
    public void Moved(GpsFix fix)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.Schedule is SmartBeaconSchedule moving)
                {
                    moving.Update(fix);
                }
            }
        }
        Poke();
    }

    /// <summary>APRS.NET's SmartBeaconing settings for <paramref name="moving"/>.</summary>
    internal static SmartBeaconSettings SmartSettings(MovingBeaconSettings moving) =>
        new()
        {
            SlowSpeedKnots = moving.SlowSpeedKnots,
            SlowRate = TimeSpan.FromSeconds(moving.SlowRateSeconds),
            FastSpeedKnots = moving.FastSpeedKnots,
            FastRate = TimeSpan.FromSeconds(moving.FastRateSeconds),
            TurnAngle = moving.TurnAngle,
            TurnSlope = moving.TurnSlope,
            TurnTime = TimeSpan.FromSeconds(moving.TurnTimeSeconds),
        };

    /// <summary>
    /// A position was sent by hand on <paramref name="sourceIds"/> (behavior 11): it counts as that source's beacon, so the
    /// schedule carries on from it and the TNC spacing counts from it.
    /// </summary>
    public void SentByHand(IEnumerable<string> sourceIds)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var id in sourceIds)
            {
                _lastBeacon[id] = now;
                _lastPosition[id] = now;
                if (_entries.TryGetValue((BeaconKind.Position, id, null), out var entry))
                {
                    entry.Schedule.Sent();
                }
            }
        }
        Poke();
    }

    /// <summary>Sends what's due and sets the timer for the next one. Ticks run one at a time.</summary>
    internal async Task TickAsync()
    {
        try
        {
            await _ticking.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            var (profile, held, sources) = _host.BeaconState();
            var due = new List<Entry>();
            lock (_gate)
            {
                Plan(profile, sources);
                _held = _entries.Count == 0 ? null : held;
                if (held is null)
                {
                    var now = _time.GetUtcNow();
                    var taken = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var entry in _entries.Values.OrderBy(e => e.Schedule.Next.At).ThenBy(e => e.Kind))
                    {
                        if (EarliestAt(entry) <= now && _host.CanSendOn(entry.SourceId) && taken.Add(entry.SourceId))
                        {
                            due.Add(entry); // one beacon per source a tick: the next waits out the spacing
                        }
                    }
                }
            }
            foreach (var entry in due)
            {
                await SendAsync(profile!, entry).ConfigureAwait(false);
            }
            List<string> gone;
            lock (_gate)
            {
                // A killed object is done once it went out killed often enough on every source (F2.7 behavior 8).
                gone =
                [
                    .. _entries
                        .Values.Where(e => e.Kind == BeaconKind.PlacedObject)
                        .GroupBy(e => e.ObjectId!)
                        .Where(g =>
                            g.All(e => e.KillsSent >= OwnObject.KillSends)
                            && profile?.ObjectList.FirstOrDefault(o => o.Id == g.Key) is { Live: false }
                        )
                        .Select(g => g.Key),
                ];
                SetNext();
            }
            if (gone.Count > 0)
            {
                await _host.ObjectsGoneAsync(gone).ConfigureAwait(false);
            }
        }
        finally
        {
            _ticking.Release();
        }
    }

    /// <summary>
    /// Makes one schedule per beacon that's on, or object, and source it goes out on. One whose settings or source
    /// changed is made again, which starts its schedule over; the others carry on.
    /// </summary>
    private void Plan(StationProfile? profile, IReadOnlyList<(string Id, bool IsRf)> sources)
    {
        var position = profile?.EffectivePositionBeacon ?? PositionBeaconSettings.Off;
        var status = profile?.EffectiveStatusBeacon ?? StatusBeaconSettings.Off;
        var wanted = new Dictionary<(BeaconKind, string, string?), (string Signature, Func<Entry> Make)>();
        var now = _time.GetUtcNow();
        foreach (var (id, isRf) in sources)
        {
            var on = $"{profile?.Id}|{id}|{isRf}|";
            if (position.On && position.SendsVia(id))
            {
                var interval = TimeSpan.FromMinutes(position.IntervalMinutes);
                // Moving follows speed and turns while the GPS has a fresh fix, and is Fixed without one (#335).
                var smart =
                    position.Schedule == PositionBeaconSchedule.Moving && profile is not null
                        ? _host.Motion(profile)
                        : null;
                var signature = on + position.GetHashCode() + (smart is null ? string.Empty : "|moving");
                wanted[(BeaconKind.Position, id, null)] = (
                    signature,
                    () =>
                    {
                        if (smart is not null)
                        {
                            var schedule = new SmartBeaconSchedule(SmartSettings(position.EffectiveMoving), _time);
                            schedule.Update(smart);
                            return new Entry(BeaconKind.Position, id, isRf, schedule, now, signature);
                        }
                        if (position.Schedule == PositionBeaconSchedule.Decay)
                        {
                            var first = isRf ? RfFloor : IsFirstStep;
                            return new Entry(
                                BeaconKind.Position,
                                id,
                                isRf,
                                new DecayBeaconSchedule(first, interval, _time),
                                now + StartWait(first),
                                signature
                            );
                        }
                        return new Entry(
                            BeaconKind.Position,
                            id,
                            isRf,
                            new FixedBeaconSchedule(interval, Clamp(interval / 10), _time, _random),
                            now,
                            signature
                        );
                    }
                );
            }
            if (status.On && status.SendsVia(id))
            {
                var interval = TimeSpan.FromMinutes(status.IntervalMinutes);
                var signature = on + status.GetHashCode();
                wanted[(BeaconKind.Status, id, null)] = (
                    signature,
                    () =>
                        new Entry(
                            BeaconKind.Status,
                            id,
                            isRf,
                            new FixedBeaconSchedule(interval, Clamp(interval / 10), _time, _random),
                            now,
                            signature
                        )
                );
            }
            foreach (var placed in profile?.ObjectList ?? [])
            {
                if (!placed.SendsVia(id))
                {
                    continue;
                }
                // At once, then doubling from 30 s (APRS-IS) or 2 min (TNC) up to its interval (F2.7 behavior 5).
                var signature = on + placed.GetHashCode();
                var first = isRf ? ObjectRfFirstStep : IsFirstStep;
                var interval = TimeSpan.FromMinutes(placed.IntervalMinutes);
                wanted[(BeaconKind.PlacedObject, id, placed.Id)] = (
                    signature,
                    () =>
                        new Entry(
                            BeaconKind.PlacedObject,
                            id,
                            isRf,
                            new DecayBeaconSchedule(first, interval, _time),
                            now,
                            signature,
                            placed.Id
                        )
                );
            }
        }
        foreach (var key in _entries.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _entries.Remove(key);
        }
        foreach (var (key, (signature, make)) in wanted)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Signature != signature)
            {
                _entries[key] = make();
            }
        }
    }

    private static TimeSpan Clamp(TimeSpan wait) => wait < MaxStartWait ? wait : MaxStartWait;

    /// <summary>A random wait of up to a tenth of <paramref name="first"/>, at most three minutes (behavior 8).</summary>
    private TimeSpan StartWait(TimeSpan first) => Clamp(first / 10) * _random.NextDouble();

    /// <summary>When <paramref name="entry"/> may go out: when it's due, unless the spacing rules hold it (behavior 6).</summary>
    private DateTimeOffset EarliestAt(Entry entry)
    {
        var at = entry.Schedule.Next.At;
        if (entry.NotBefore > at)
        {
            at = entry.NotBefore;
        }
        if (_lastBeacon.TryGetValue(entry.SourceId, out var last) && last + Spacing > at)
        {
            at = last + Spacing;
        }
        if (
            entry.Kind == BeaconKind.Position
            && entry.IsRf
            && entry.Schedule is not SmartBeaconSchedule // moving keeps its own pace: its fast rate and turn time
            && _lastPosition.TryGetValue(entry.SourceId, out var lastPosition)
            && lastPosition + RfFloor > at
        )
        {
            at = lastPosition + RfFloor;
        }
        return at;
    }

    private async Task SendAsync(StationProfile profile, Entry entry)
    {
        var placed =
            entry.Kind == BeaconKind.PlacedObject
                ? profile.ObjectList.FirstOrDefault(o => o.Id == entry.ObjectId)
                : null;
        AprsPayload? payload = entry.Kind switch
        {
            BeaconKind.Position => PositionReport(profile, _host.Motion(profile)),
            BeaconKind.Status => StatusReport(profile, _time.GetUtcNow()),
            _ => placed is null ? null : ObjectReport(placed, _time.GetUtcNow()),
        };
        if (payload is null || !await _host.SendAsync(entry.SourceId, payload, _stop.Token).ConfigureAwait(false))
        {
            return; // still due: tried again shortly, once (behavior 7)
        }
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (placed is { Live: false })
            {
                entry.KillsSent++;
            }
            entry.Schedule.Sent();
            _lastBeacon[entry.SourceId] = now;
            if (entry.Kind == BeaconKind.Position)
            {
                _lastPosition[entry.SourceId] = now;
            }
        }
    }

    /// <summary>Works out the next beacon for the status, and sets the timer for it. Call under <see cref="_gate"/>.</summary>
    private void SetNext()
    {
        var now = _time.GetUtcNow();
        Entry? first = null;
        var firstAt = DateTimeOffset.MaxValue;
        foreach (var entry in _entries.Values)
        {
            var at = EarliestAt(entry);
            if (at < firstAt)
            {
                (first, firstAt) = (entry, at);
            }
        }
        _next = first is null
            ? null
            : new NextBeacon(
                firstAt,
                first.Kind,
                firstAt > first.Schedule.Next.At ? BeaconDueReason.Spacing
                    : first.Schedule.Next.Reason == BeaconReason.Start ? BeaconDueReason.Start
                    : first.Schedule.Next.Reason == BeaconReason.Interval ? BeaconDueReason.Interval
                    : BeaconDueReason.Decay,
                first.SourceId
            );
        if (_stop.IsCancellationRequested)
        {
            return;
        }
        var wake =
            first is null ? Timeout.InfiniteTimeSpan
            : _held is not null || firstAt <= now ? RetryWait // waiting on the station or a source: look again soon
            : firstAt - now;
        _timer.Change(wake, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The position a profile sends, by hand or as a beacon (F2.1 behavior 10, F2.2 behavior 2): a position report, or
    /// a Mic-E report with its status (docs/features/F2.12-mic-e-status.md, behaviors 1–3).
    /// </summary>
    /// <param name="profile">The profile, where it is now (its GPS position while the fix is fresh).</param>
    /// <param name="motion">
    /// The GPS's fresh fix, whose course, speed and altitude go with the position (docs/features/F3.6-gps.md, behavior
    /// 7.1); null for a station without one.
    /// </param>
    public static PositionedPayload PositionReport(StationProfile profile, GpsFix? motion = null)
    {
        var beacon = profile.EffectivePositionBeacon;
        var symbol = profile.EffectiveSymbol;
        // APRS courses run 1 to 360; north is 360, since 0 means unknown.
        var course = motion?.Course is { } c
            ? ((int)Math.Round(c) % 360 is 0 ? 360 : (int)Math.Round(c) % 360)
            : (int?)null;
        var speed = motion?.SpeedKnots;
        var altitudeFeet = motion?.AltitudeMeters is { } metres ? metres / 0.3048 : (double?)null;
        var moving = course is not null && speed is not null;
        if (beacon.MicE is { } status)
        {
            return new MicEReport
            {
                Position = new AprsPosition(profile.Latitude ?? 0, profile.Longitude ?? 0),
                Symbol = new AprsSymbol(symbol[0], symbol[1]),
                Comment = profile.Comment ?? string.Empty,
                Message = MicEMessageFor(status),
                IsMessagingCapable = true,
                Course = moving ? course : null,
                SpeedKnots = moving ? speed : null,
                AltitudeFeet = altitudeFeet,
            };
        }
        return new PositionReport
        {
            Position = new AprsPosition(profile.Latitude ?? 0, profile.Longitude ?? 0),
            Symbol = new AprsSymbol(symbol[0], symbol[1]),
            Comment = profile.Comment ?? string.Empty,
            IsMessagingCapable = true, // it answers messages (F2.4 behavior 15)
            IsCompressed = beacon.Compressed,
            // One data extension: the GPS's course and speed while it has them, otherwise PHG (APRS101 chapter 7).
            Course = moving ? course : null,
            SpeedKnots = moving ? speed : null,
            PowerHeightGain = beacon.Phg is { } phg && !beacon.Compressed && !moving ? PowerHeightGain(phg) : null,
            AltitudeFeet = altitudeFeet,
        };
    }

    /// <summary>
    /// The status a profile sends, by hand or as a beacon (docs/features/F2.11-status-reports.md, behavior 3): its text,
    /// with the grid square and symbol or the UTC time when chosen.
    /// </summary>
    public static StatusReport StatusReport(StationProfile profile, DateTimeOffset now)
    {
        var status = profile.EffectiveStatusBeacon;
        var symbol = profile.EffectiveSymbol;
        return status.Add switch
        {
            StatusAddition.Time => new StatusReport
            {
                Text = status.Text,
                Timestamp = AprsTimestamp.FromDateTimeOffset(now, AprsTimestampFormat.DayHourMinute),
            },
            StatusAddition.GridSquare when profile is { Latitude: { } lat, Longitude: { } lon } => new StatusReport
            {
                Text = " " + status.Text, // the text after a locator starts with a space (APRS101 chapter 16)
                MaidenheadLocator = MaidenheadLocator.FromPosition(lat, lon, 6).Value.ToUpperInvariant(),
                Symbol = new AprsSymbol(symbol[0], symbol[1]),
            },
            _ => new StatusReport { Text = status.Text },
        };
    }

    /// <summary>
    /// What an object or item sends (docs/features/F2.7-objects-and-items.md): an object with the UTC day, hour and
    /// minute, an item without; live or killed.
    /// </summary>
    public static PositionedPayload ObjectReport(OwnObject placed, DateTimeOffset now)
    {
        var position = new AprsPosition(placed.Latitude, placed.Longitude);
        var symbol = new AprsSymbol(placed.Symbol[0], placed.Symbol[1]);
        return placed.Kind == OwnObjectKind.Item
            ? new ItemReport
            {
                Name = placed.Name,
                IsLive = placed.Live,
                Position = position,
                Symbol = symbol,
                Comment = placed.Comment,
            }
            : new ObjectReport
            {
                Name = placed.Name,
                IsLive = placed.Live,
                Position = position,
                Symbol = symbol,
                Comment = placed.Comment,
                Timestamp = AprsTimestamp.FromDateTimeOffset(now, AprsTimestampFormat.DayHourMinute),
            };
    }

    /// <summary>APRS.NET's Mic-E message for <paramref name="status"/>.</summary>
    public static MicEMessage MicEMessageFor(MicEStatus status) =>
        status switch
        {
            MicEStatus.OffDuty => MicEMessage.OffDuty,
            MicEStatus.EnRoute => MicEMessage.EnRoute,
            MicEStatus.InService => MicEMessage.InService,
            MicEStatus.Returning => MicEMessage.Returning,
            MicEStatus.Committed => MicEMessage.Committed,
            MicEStatus.Special => MicEMessage.Special,
            MicEStatus.Priority => MicEMessage.Priority,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a Mic-E status the app sends."),
        };

    /// <summary>APRS.NET's PHG for <paramref name="phg"/>.</summary>
    public static PowerHeightGain PowerHeightGain(PhgSettings phg) =>
        new(phg.PowerWatts, phg.HeightFeet, phg.GainDbi, phg.Directivity);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _stop.Cancel();
        _timer.Dispose();
        _stop.Dispose();
    }
}
