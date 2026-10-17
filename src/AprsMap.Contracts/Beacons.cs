// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>When a position beacon goes out (docs/features/F2.2-beacons.md, behavior 1.1).</summary>
public enum PositionBeaconSchedule
{
    /// <summary>Every interval.</summary>
    Fixed,

    /// <summary>Soon after a send, then at doubling waits up to the interval.</summary>
    Decay,

    /// <summary>By speed and turns; needs a GPS (F3.6), so until then it beacons as <see cref="Fixed"/>.</summary>
    Moving,
}

/// <summary>Which beacon (F2.2).</summary>
public enum BeaconKind
{
    /// <summary>The position beacon.</summary>
    Position,

    /// <summary>The status beacon.</summary>
    Status,
}

/// <summary>Why the next beacon is due when it is (F2.2 behavior 10).</summary>
public enum BeaconDueReason
{
    /// <summary>The first beacon, after a short random wait so stations that start together don't beacon together.</summary>
    Start,

    /// <summary>The interval has passed.</summary>
    Interval,

    /// <summary>The next step of a decay sequence.</summary>
    Decay,

    /// <summary>Held back by the 10 minutes between position beacons on a TNC, or the 10 seconds between beacons.</summary>
    Spacing,
}

/// <summary>
/// PHG: transmitter power, antenna height above average terrain, gain and directivity (APRS101 chapter 7), from the
/// values the format can carry.
/// </summary>
/// <param name="PowerWatts">0, 1, 4, 9 … 81 W.</param>
/// <param name="HeightFeet">10, 20, 40 … 5,120 ft.</param>
/// <param name="GainDbi">0 to 9 dB.</param>
/// <param name="Directivity">Degrees of the strongest direction, 45 to 360 in steps of 45; null for omnidirectional.</param>
public sealed record PhgSettings(int PowerWatts, int HeightFeet, int GainDbi, int? Directivity = null);

/// <summary>A profile's position beacon (F2.2 behaviors 1, 2).</summary>
/// <param name="On">Whether it beacons; off for every new and existing profile.</param>
/// <param name="Schedule">When it beacons.</param>
/// <param name="IntervalMinutes">The fixed interval, and a decay's longest wait: 10 minutes to 24 hours.</param>
/// <param name="SourceIds">Send via: the TX-enabled sources it goes out on; null for all of them.</param>
/// <param name="Compressed">Whether the position is sent compressed, which is shorter on air.</param>
/// <param name="Phg">PHG, or null for none. A compressed position can't carry it.</param>
public sealed record PositionBeaconSettings(
    bool On = false,
    PositionBeaconSchedule Schedule = PositionBeaconSchedule.Decay,
    int IntervalMinutes = PositionBeaconSettings.DefaultIntervalMinutes,
    IReadOnlyList<string>? SourceIds = null,
    bool Compressed = false,
    PhgSettings? Phg = null
)
{
    /// <summary>The interval a new position beacon has: 30 minutes.</summary>
    public const int DefaultIntervalMinutes = 30;

    /// <summary>A position beacon that's off.</summary>
    public static readonly PositionBeaconSettings Off = new();

    /// <summary>Whether it goes out on <paramref name="sourceId"/>.</summary>
    public bool SendsVia(string sourceId) => SourceIds is null || SourceIds.Contains(sourceId);

    /// <summary>Equal when every field is, comparing the source ids in order.</summary>
    public bool Equals(PositionBeaconSettings? other) =>
        other is not null
        && On == other.On
        && Schedule == other.Schedule
        && IntervalMinutes == other.IntervalMinutes
        && BeaconSettings.SameSources(SourceIds, other.SourceIds)
        && Compressed == other.Compressed
        && Phg == other.Phg;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            On,
            Schedule,
            IntervalMinutes,
            SourceIds is null ? null : string.Join(',', SourceIds),
            Compressed,
            Phg
        );
}

/// <summary>A profile's status beacon (F2.2 behavior 3).</summary>
/// <param name="On">Whether it beacons; off by default.</param>
/// <param name="Text">Up to 62 characters of printable ASCII.</param>
/// <param name="IntervalMinutes">10 minutes to 24 hours.</param>
/// <param name="SourceIds">Send via: the TX-enabled sources it goes out on; null for all of them.</param>
public sealed record StatusBeaconSettings(
    bool On = false,
    string Text = "",
    int IntervalMinutes = StatusBeaconSettings.DefaultIntervalMinutes,
    IReadOnlyList<string>? SourceIds = null
)
{
    /// <summary>The interval a new status beacon has: 60 minutes.</summary>
    public const int DefaultIntervalMinutes = 60;

    /// <summary>The most characters a status without a timestamp may have (APRS101 chapter 16).</summary>
    public const int MaxTextLength = 62;

    /// <summary>A status beacon that's off.</summary>
    public static readonly StatusBeaconSettings Off = new();

    /// <summary>Whether it goes out on <paramref name="sourceId"/>.</summary>
    public bool SendsVia(string sourceId) => SourceIds is null || SourceIds.Contains(sourceId);

    /// <summary>Equal when every field is, comparing the source ids in order.</summary>
    public bool Equals(StatusBeaconSettings? other) =>
        other is not null
        && On == other.On
        && Text == other.Text
        && IntervalMinutes == other.IntervalMinutes
        && BeaconSettings.SameSources(SourceIds, other.SourceIds);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(On, Text, IntervalMinutes, SourceIds is null ? null : string.Join(',', SourceIds));
}

/// <summary>Limits shared by both beacons.</summary>
public static class BeaconSettings
{
    /// <summary>The shortest interval: 10 minutes.</summary>
    public const int MinIntervalMinutes = 10;

    /// <summary>The longest interval: 24 hours.</summary>
    public const int MaxIntervalMinutes = 24 * 60;

    internal static bool SameSources(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);
}

/// <summary>The next beacon, for the status bar (F2.2 behavior 10).</summary>
/// <param name="At">When it's due; in the past while it's held.</param>
/// <param name="Kind">Which beacon.</param>
/// <param name="Reason">Why then.</param>
/// <param name="SourceId">The source it goes out on first.</param>
public sealed record NextBeacon(DateTimeOffset At, BeaconKind Kind, BeaconDueReason Reason, string SourceId);
