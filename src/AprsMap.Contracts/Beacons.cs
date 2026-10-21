// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>When a position beacon goes out (docs/features/F2.2-beacons.md, behavior 1.1).</summary>
public enum PositionBeaconSchedule
{
    /// <summary>Every interval.</summary>
    Fixed,

    /// <summary>Soon after a send, then at doubling waits up to the interval.</summary>
    Decay,

    /// <summary>
    /// By speed and turns (SmartBeaconing) from the GPS's fixes (docs/features/F2.2-beacons.md, revised for #335); as
    /// <see cref="Fixed"/> while there's no fresh fix.
    /// </summary>
    Moving,
}

/// <summary>Which beacon (F2.2).</summary>
public enum BeaconKind
{
    /// <summary>The position beacon.</summary>
    Position,

    /// <summary>The status beacon.</summary>
    Status,

    /// <summary>An object or item the station places (docs/features/F2.7-objects-and-items.md).</summary>
    PlacedObject,
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
/// The moving schedule (SmartBeaconing, revised F2.2 for #335): every <paramref name="SlowRateSeconds"/> at or below
/// <paramref name="SlowSpeedKnots"/>, every <paramref name="FastRateSeconds"/> at or above
/// <paramref name="FastSpeedKnots"/>, in between inversely to the speed, and at once on a turn sharper than
/// <paramref name="TurnAngle"/> + <paramref name="TurnSlope"/> ÷ speed, no sooner than <paramref name="TurnTimeSeconds"/>
/// after the last beacon. The defaults are APRS.NET's (Dire Wolf's for a car).
/// </summary>
/// <param name="SlowSpeedKnots">At or below it, the slow rate: 5 mph.</param>
/// <param name="SlowRateSeconds">The interval when slow or stopped: 30 minutes.</param>
/// <param name="FastSpeedKnots">At or above it, the fast rate: 60 mph.</param>
/// <param name="FastRateSeconds">The interval at speed: 90 seconds.</param>
/// <param name="TurnAngle">The smallest turn at speed, in degrees: 30.</param>
/// <param name="TurnSlope">How much more a slow station must turn, degrees × knots: 221.6.</param>
/// <param name="TurnTimeSeconds">The shortest time from the last beacon to a turn beacon: 10 seconds.</param>
public sealed record MovingBeaconSettings(
    double SlowSpeedKnots = 5 / MovingBeaconSettings.MphPerKnot,
    int SlowRateSeconds = 30 * 60,
    double FastSpeedKnots = 60 / MovingBeaconSettings.MphPerKnot,
    int FastRateSeconds = 90,
    double TurnAngle = 30,
    double TurnSlope = 255 / MovingBeaconSettings.MphPerKnot,
    int TurnTimeSeconds = 10
)
{
    /// <summary>Miles per hour in a knot.</summary>
    public const double MphPerKnot = 1.150779;

    /// <summary>The defaults.</summary>
    public static MovingBeaconSettings Default { get; } = new();
}

/// <summary>
/// The Mic-E status a position goes with (docs/features/F2.12-mic-e-status.md, APRS101 chapter 10). Emergency is
/// deliberately absent: the app never sends it (decision 3).
/// </summary>
public enum MicEStatus
{
    /// <summary>M0: Off duty.</summary>
    OffDuty,

    /// <summary>M1: En route.</summary>
    EnRoute,

    /// <summary>M2: In service.</summary>
    InService,

    /// <summary>M3: Returning.</summary>
    Returning,

    /// <summary>M4: Committed.</summary>
    Committed,

    /// <summary>M5: Special.</summary>
    Special,

    /// <summary>M6: Priority.</summary>
    Priority,
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
/// <param name="Moving">The speeds, rates and turns of the <see cref="PositionBeaconSchedule.Moving"/> schedule; null for the defaults.</param>
/// <param name="MicE">
/// Mic-E with this status (docs/features/F2.12-mic-e-status.md), or null for an uncompressed or compressed position.
/// Mic-E is never compressed and carries no PHG.
/// </param>
public sealed record PositionBeaconSettings(
    bool On = false,
    PositionBeaconSchedule Schedule = PositionBeaconSchedule.Decay,
    int IntervalMinutes = PositionBeaconSettings.DefaultIntervalMinutes,
    IReadOnlyList<string>? SourceIds = null,
    bool Compressed = false,
    PhgSettings? Phg = null,
    MicEStatus? MicE = null,
    MovingBeaconSettings? Moving = null
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
        && Phg == other.Phg
        && MicE == other.MicE
        && EffectiveMoving == other.EffectiveMoving;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            On,
            Schedule,
            IntervalMinutes,
            SourceIds is null ? null : string.Join(',', SourceIds),
            Compressed,
            Phg,
            MicE,
            EffectiveMoving
        );

    /// <summary>The moving schedule's settings in effect: <see cref="Moving"/>, or the defaults.</summary>
    public MovingBeaconSettings EffectiveMoving => Moving ?? MovingBeaconSettings.Default;
}

/// <summary>What a status carries besides its text (docs/features/F2.11-status-reports.md, behavior 3).</summary>
public enum StatusAddition
{
    /// <summary>The text alone.</summary>
    None,

    /// <summary>The 6-character Maidenhead locator of the profile's location and its symbol.</summary>
    GridSquare,

    /// <summary>The UTC day, hour and minute it's sent.</summary>
    Time,
}

/// <summary>A profile's status beacon (F2.2 behavior 3), and its saved texts (F2.11).</summary>
/// <param name="On">Whether it beacons; off by default.</param>
/// <param name="Text">Up to 62 characters of printable ASCII.</param>
/// <param name="IntervalMinutes">10 minutes to 24 hours.</param>
/// <param name="SourceIds">Send via: the TX-enabled sources it goes out on; null for all of them.</param>
/// <param name="SavedTexts">Texts kept to choose from, at most 20 (F2.11 behavior 1); null for none.</param>
/// <param name="Add">The grid square or the time, or neither (F2.11 behavior 3).</param>
public sealed record StatusBeaconSettings(
    bool On = false,
    string Text = "",
    int IntervalMinutes = StatusBeaconSettings.DefaultIntervalMinutes,
    IReadOnlyList<string>? SourceIds = null,
    IReadOnlyList<string>? SavedTexts = null,
    StatusAddition Add = StatusAddition.None
)
{
    /// <summary>The interval a new status beacon has: 60 minutes.</summary>
    public const int DefaultIntervalMinutes = 60;

    /// <summary>The most characters a status without a timestamp may have (APRS101 chapter 16).</summary>
    public const int MaxTextLength = 62;

    /// <summary>The most saved texts (F2.11 behavior 1).</summary>
    public const int MaxSavedTexts = 20;

    /// <summary>The saved texts, none when unset.</summary>
    public IReadOnlyList<string> SavedTextList => SavedTexts ?? [];

    /// <summary>
    /// The most characters the text may have with <paramref name="add"/> (F2.11 behavior 4): 62, 55 with the time
    /// (seven for <c>DDHHMMz</c>), 53 with the grid square (six, the symbol's two and a space).
    /// </summary>
    public static int MaxTextLengthWith(StatusAddition add) =>
        add switch
        {
            StatusAddition.Time => MaxTextLength - 7,
            StatusAddition.GridSquare => MaxTextLength - 9,
            _ => MaxTextLength,
        };

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
        && BeaconSettings.SameSources(SourceIds, other.SourceIds)
        && SavedTextList.SequenceEqual(other.SavedTextList)
        && Add == other.Add;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            On,
            Text,
            IntervalMinutes,
            SourceIds is null ? null : string.Join(',', SourceIds),
            string.Join('\n', SavedTextList),
            Add
        );
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
