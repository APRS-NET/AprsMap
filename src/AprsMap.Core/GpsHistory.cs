// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>What the GPS in use was doing at one sample (docs/features/F3.14-gps-dashboard.md, behavior 1).</summary>
internal enum GpsSampleState
{
    /// <summary>A fresh 3D fix with a position.</summary>
    Fix3D,

    /// <summary>A fresh 2D fix with a position.</summary>
    Fix2D,

    /// <summary>Reporting, without a position.</summary>
    NoFix,

    /// <summary>Nothing from it for <see cref="GpsHistory.SilentAfter"/>.</summary>
    Silent,
}

/// <summary>One sample of the GPS in use: its state, what it says of its accuracy and sky, and where and how fast.</summary>
internal sealed record GpsSample(
    string SourceId,
    GpsSampleState State,
    GpsAccuracy? Accuracy = null,
    IReadOnlyList<GpsSkySatellite>? Sky = null,
    double? Latitude = null,
    double? Longitude = null,
    double? SpeedKnots = null
)
{
    /// <summary>Whether the GPS had a fix.</summary>
    public bool HasFix => State is GpsSampleState.Fix3D or GpsSampleState.Fix2D;
}

/// <summary>
/// The station's GPS over the last 24 hours (docs/features/F3.14-gps-dashboard.md, behaviors 1–6): samples a minute at a
/// time in a fixed ring, like the dashboard's other history (<see cref="DashboardCounters"/>), and the outages. Memory
/// stays bounded: minutes are reused, and outages are capped. Thread-safe.
/// </summary>
internal sealed class GpsHistory
{
    /// <summary>How often the engine samples the GPS in use.</summary>
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a GPS may say nothing before it counts as silent (decision 1).</summary>
    internal static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(5);

    /// <summary>Under this speed the station is still, and its fixes count towards the wander (decision 2).</summary>
    internal const double StillKnots = 1;

    /// <summary>The fewest still fixes the wander is worked out from.</summary>
    internal const int WanderMinimum = 10;

    private const int Minutes = 24 * 60;
    private const int OutagesKept = 1000;
    private const double EarthRadiusMeters = 6_371_008.8;

    internal static readonly string[] DilutionKeys = ["pdop", "hdop", "vdop", "tdop", "gdop"];
    internal static readonly string[] ErrorKeys = ["horizontal", "vertical", "latitude", "longitude"];

    private static readonly GnssConstellation[] Systems = Enum.GetValues<GnssConstellation>();

    private readonly Lock _gate = new();
    private readonly Minute[] _minutes = new Minute[Minutes];
    private readonly List<(DateTimeOffset Start, DateTimeOffset? End)> _outages = [];
    private GpsSample? _last;
    private (double Latitude, double Longitude)? _origin; // the wander's offsets are from here

    public GpsHistory()
    {
        for (var i = 0; i < _minutes.Length; i++)
        {
            _minutes[i] = new Minute();
        }
    }

    /// <summary>
    /// Records one sample covering <paramref name="length"/> up to <paramref name="now"/>; true when it's a fix lost
    /// (behavior 2): no fix or silent straight after a fix.
    /// </summary>
    public bool Record(GpsSample sample, DateTimeOffset now, TimeSpan length)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            var minute = BucketAt(MinuteOf(now));
            minute.Seconds[(int)sample.State] += length.TotalSeconds;
            minute.BySource[sample.SourceId] = minute.BySource.GetValueOrDefault(sample.SourceId) + length.TotalSeconds;
            var lost = _last is { HasFix: true } && !sample.HasFix;
            if (lost)
            {
                minute.Losses++;
                _outages.Add((now, null));
                if (_outages.Count > OutagesKept)
                {
                    _outages.RemoveAt(0);
                }
            }
            else if (sample.HasFix && _outages.Count > 0 && _outages[^1] is (var start, null))
            {
                _outages[^1] = (start, now);
            }
            if (sample.HasFix)
            {
                AddFix(minute, sample);
            }
            _last = sample;
            return lost;
        }
    }

    private void AddFix(Minute minute, GpsSample sample)
    {
        minute.FixSamples++;
        var (used, inView, signal) = Count(sample.Sky);
        // The totals as the GPS gives them (GSA, gpsd's SKY), else from its satellites; those by system from the list.
        minute.Used.Add(UsedOf(sample, used));
        minute.InView.Add(InViewOf(sample, inView));
        for (var s = 0; s < Systems.Length; s++)
        {
            minute.UsedBySystem[s] += used[s];
            minute.SeenBySystem[s] += inView[s] > 0 ? 1 : 0;
        }
        minute.Signal.Add(signal);
        var dops = Dilutions(sample.Accuracy);
        var errors = Errors(sample.Accuracy);
        for (var i = 0; i < dops.Length; i++)
        {
            minute.Dilutions[i].Add(dops[i]);
        }
        for (var i = 0; i < errors.Length; i++)
        {
            minute.Errors[i].Add(errors[i]);
        }
        // The wander (behavior 4): offsets in metres from the first still fix, while the GPS says it's still.
        if (sample is { Latitude: { } lat, Longitude: { } lon, SpeedKnots: var speed } && (speed ?? 0) < StillKnots)
        {
            _origin ??= (lat, lon);
            var (lat0, lon0) = _origin.Value;
            var x = double.DegreesToRadians(lon - lon0) * EarthRadiusMeters * Math.Cos(double.DegreesToRadians(lat0));
            var y = double.DegreesToRadians(lat - lat0) * EarthRadiusMeters;
            minute.Wander.Add(x, y);
        }
    }

    /// <summary>
    /// The GPS from <paramref name="start"/> to <paramref name="now"/> in <paramref name="points"/> points of
    /// <paramref name="step"/> (the dashboard's), or null when nothing was sampled in it.
    /// </summary>
    public DashboardGps? Read(DateTimeOffset start, TimeSpan step, int points, DateTimeOffset now)
    {
        var first = MinuteOf(start);
        var last = MinuteOf(now);
        var size = Math.Max(1, (long)step.TotalMinutes);
        lock (_gate)
        {
            var seconds = new double[4];
            var bySource = new Dictionary<string, double>(StringComparer.Ordinal);
            var losses = 0;
            var fixSamples = 0;
            Stat used = default,
                inView = default,
                signal = default;
            var usedBySystem = new double[Systems.Length];
            var seenBySystem = new int[Systems.Length];
            var dilutions = new Stat[DilutionKeys.Length];
            var errors = new Stat[ErrorKeys.Length];
            Spread wander = default;
            var pointSeconds = new double[points];
            var pointFix = new double[points];
            var pointUsed = new Stat[points];
            var pointHdop = new Stat[points];
            var pointError = new Stat[points];
            for (var m = first; m <= last; m++)
            {
                var minute = _minutes[Index(m)];
                if (minute.Number != m)
                {
                    continue;
                }
                var point = (int)((m - first) / size);
                if (point < 0 || point >= points)
                {
                    continue;
                }
                for (var s = 0; s < seconds.Length; s++)
                {
                    seconds[s] += minute.Seconds[s];
                    pointSeconds[point] += minute.Seconds[s];
                }
                pointFix[point] +=
                    minute.Seconds[(int)GpsSampleState.Fix3D] + minute.Seconds[(int)GpsSampleState.Fix2D];
                foreach (var (source, time) in minute.BySource)
                {
                    bySource[source] = bySource.GetValueOrDefault(source) + time;
                }
                losses += minute.Losses;
                fixSamples += minute.FixSamples;
                used.Merge(minute.Used);
                inView.Merge(minute.InView);
                signal.Merge(minute.Signal);
                for (var s = 0; s < Systems.Length; s++)
                {
                    usedBySystem[s] += minute.UsedBySystem[s];
                    seenBySystem[s] += minute.SeenBySystem[s];
                }
                for (var i = 0; i < dilutions.Length; i++)
                {
                    dilutions[i].Merge(minute.Dilutions[i]);
                }
                for (var i = 0; i < errors.Length; i++)
                {
                    errors[i].Merge(minute.Errors[i]);
                }
                wander.Merge(minute.Wander);
                pointUsed[point].Merge(minute.Used);
                pointHdop[point].Merge(minute.Dilutions[1]);
                pointError[point].Merge(minute.Errors[0]);
            }
            if (seconds.Sum() <= 0)
            {
                return null;
            }

            // Now: the latest sample, while it has a fix.
            var current = _last is { HasFix: true } ? _last : null;
            var (usedNow, inViewNow, signalNow) = Count(current?.Sky);
            var dopsNow = Dilutions(current?.Accuracy);
            var errorsNow = Errors(current?.Accuracy);
            var outages = _outages.Where(o => (o.End ?? now) >= start).ToList();
            var latest = outages.Count > 0 ? outages[^1] : ((DateTimeOffset, DateTimeOffset?)?)null;
            return new DashboardGps(
                bySource.MaxBy(s => s.Value).Key,
                bySource.Count > 1,
                TimeSpan.FromSeconds(seconds[(int)GpsSampleState.Fix3D]),
                TimeSpan.FromSeconds(seconds[(int)GpsSampleState.Fix2D]),
                TimeSpan.FromSeconds(seconds[(int)GpsSampleState.NoFix]),
                TimeSpan.FromSeconds(seconds[(int)GpsSampleState.Silent]),
                losses,
                outages.Count > 0 ? outages.Max(o => (o.End ?? now) - o.Start) : null,
                latest is var (from, to) ? new DashboardOutage(from, (to ?? now) - from, to is null) : null,
                used.ToStat(current is null ? null : UsedOf(current, usedNow)),
                inView.ToStat(current is null ? null : InViewOf(current, inViewNow)),
                [
                    .. Systems
                        .Select((system, s) => (system, s))
                        .Where(x => seenBySystem[x.s] > 0 || inViewNow[x.s] > 0)
                        .Select(x => new DashboardConstellation(
                            x.system,
                            usedNow[x.s],
                            inViewNow[x.s],
                            fixSamples > 0 ? usedBySystem[x.s] / fixSamples : null
                        )),
                ],
                Values(DilutionKeys, dilutions, dopsNow),
                Values(ErrorKeys, errors, errorsNow),
                signal.ToStat(signalNow),
                wander.Count >= WanderMinimum ? wander.TwiceRms() : null,
                wander.Count,
                [.. pointSeconds.Select((total, i) => total > 0 ? pointFix[i] / total * 100 : (double?)null)],
                [.. pointUsed.Select(s => s.Average)],
                [.. pointHdop.Select(s => s.Average)],
                [.. pointError.Select(s => s.Average)]
            );
        }
    }

    private static DashboardValue[] Values(string[] keys, Stat[] stats, double?[] now) =>
        [
            .. keys.Select((key, i) => (key, i))
                .Where(x => stats[x.i].Count > 0 || now[x.i] is not null)
                .Select(x => new DashboardValue(x.key, stats[x.i].ToStat(now[x.i]))),
        ];

    /// <summary>
    /// Satellites used and in view by system, and the average signal of those used; a satellite seen on two signals
    /// counts once, used if either is (as the GPS panel draws it, F3.12 behavior 4).
    /// </summary>
    private static (int[] Used, int[] InView, double? Signal) Count(IReadOnlyList<GpsSkySatellite>? sky)
    {
        var used = new int[Systems.Length];
        var inView = new int[Systems.Length];
        if (sky is null)
        {
            return (used, inView, null);
        }
        double signal = 0;
        var signals = 0;
        foreach (var satellite in sky.GroupBy(s => (s.System, s.Id)))
        {
            var system = (int)satellite.Key.System;
            inView[system]++;
            if (satellite.Any(s => s.Used))
            {
                used[system]++;
                if (satellite.Where(s => s.Used).Max(s => s.SnrDbHz) is { } snr)
                {
                    signal += snr;
                    signals++;
                }
            }
        }
        return (used, inView, signals > 0 ? signal / signals : null);
    }

    private static int? UsedOf(GpsSample sample, int[] bySystem) =>
        sample.Accuracy?.SatellitesUsed ?? (sample.Sky is { Count: > 0 } ? bySystem.Sum() : null);

    private static int? InViewOf(GpsSample sample, int[] bySystem) =>
        sample.Accuracy?.SatellitesInView ?? (sample.Sky is { Count: > 0 } ? bySystem.Sum() : null);

    private static double?[] Dilutions(GpsAccuracy? a) => [a?.Pdop, a?.Hdop, a?.Vdop, a?.Tdop, a?.Gdop];

    private static double?[] Errors(GpsAccuracy? a) =>
        [a?.HorizontalErrorMeters, a?.VerticalErrorMeters, a?.LatitudeErrorMeters, a?.LongitudeErrorMeters];

    private Minute BucketAt(long number)
    {
        var minute = _minutes[Index(number)];
        if (minute.Number != number)
        {
            minute.Reset(number);
        }
        return minute;
    }

    private static int Index(long minute) => (int)(((minute % Minutes) + Minutes) % Minutes);

    private static long MinuteOf(DateTimeOffset time) =>
        (long)Math.Floor((time - DateTimeOffset.UnixEpoch).TotalMinutes);

    /// <summary>One minute of samples.</summary>
    private sealed class Minute
    {
        public long Number = long.MinValue;
        public readonly double[] Seconds = new double[4];
        public readonly Dictionary<string, double> BySource = new(StringComparer.Ordinal);
        public int Losses;
        public int FixSamples;
        public Stat Used;
        public Stat InView;
        public Stat Signal;
        public readonly double[] UsedBySystem = new double[Systems.Length];
        public readonly int[] SeenBySystem = new int[Systems.Length];
        public readonly Stat[] Dilutions = new Stat[DilutionKeys.Length];
        public readonly Stat[] Errors = new Stat[ErrorKeys.Length];
        public Spread Wander;

        public void Reset(long number)
        {
            Number = number;
            Array.Clear(Seconds);
            BySource.Clear();
            Losses = 0;
            FixSamples = 0;
            Used = default;
            InView = default;
            Signal = default;
            Array.Clear(UsedBySystem);
            Array.Clear(SeenBySystem);
            Array.Clear(Dilutions);
            Array.Clear(Errors);
            Wander = default;
        }
    }

    /// <summary>A running average, lowest and highest.</summary>
    private struct Stat
    {
        private double _sum;
        private double _lowest;
        private double _highest;

        public int Count { get; private set; }

        public readonly double? Average => Count > 0 ? _sum / Count : null;

        public void Add(double? value)
        {
            if (value is not { } v || double.IsNaN(v))
            {
                return;
            }
            _lowest = Count == 0 ? v : Math.Min(_lowest, v);
            _highest = Count == 0 ? v : Math.Max(_highest, v);
            _sum += v;
            Count++;
        }

        public void Merge(in Stat other)
        {
            if (other.Count == 0)
            {
                return;
            }
            _lowest = Count == 0 ? other._lowest : Math.Min(_lowest, other._lowest);
            _highest = Count == 0 ? other._highest : Math.Max(_highest, other._highest);
            _sum += other._sum;
            Count += other.Count;
        }

        public readonly DashboardStat ToStat(double? now) =>
            new(now, Average, Count > 0 ? _lowest : null, Count > 0 ? _highest : null);
    }

    /// <summary>Sums of offsets and their squares, for the spread of positions about their average.</summary>
    private struct Spread
    {
        private double _x;
        private double _y;
        private double _xx;
        private double _yy;

        public int Count { get; private set; }

        public void Add(double x, double y)
        {
            _x += x;
            _y += y;
            _xx += x * x;
            _yy += y * y;
            Count++;
        }

        public void Merge(in Spread other)
        {
            _x += other._x;
            _y += other._y;
            _xx += other._xx;
            _yy += other._yy;
            Count += other.Count;
        }

        /// <summary>2DRMS: twice the root-mean-square distance from the average, about 95% of fixes within it.</summary>
        public readonly double TwiceRms()
        {
            var mx = _x / Count;
            var my = _y / Count;
            var variance = Math.Max(0, (_xx / Count) - (mx * mx)) + Math.Max(0, (_yy / Count) - (my * my));
            return 2 * Math.Sqrt(variance);
        }
    }
}
