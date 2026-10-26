// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// Everything the user sets, stored per user (docs/scope.md#settings-storage-and-profiles): station profiles, which one
/// is active, and settings shared by all profiles.
/// </summary>
/// <param name="Profiles">The station profiles, in the user's order. Empty until first-run setup.</param>
/// <param name="ActiveProfileId">The <see cref="StationProfile.Id"/> the engine connects with, or null with no profiles.</param>
/// <param name="Units">Display units, shared by every profile.</param>
/// <param name="ReadOnly">
/// Set by an engine that won't accept changes from this caller (a gateway without operator sign-in). Passcodes are
/// hidden (sent as -1) when it is set. Ignored on save.
/// </param>
/// <param name="Aging">When stations fade and leave (F1.3 behaviors 7–9); null means the defaults.</param>
/// <param name="Updates">The update check (docs/features/F4.15-updates.md); null means the defaults.</param>
/// <param name="Logging">Logging and telemetry (docs/features/F1.32-logging.md); null means the defaults.</param>
/// <param name="DeviceList">The device list's checks (docs/features/F4.16-device-list.md); null means the defaults.</param>
/// <param name="Sources">
/// Where traffic comes from (docs/features/F1.25-sources.md): the master list profiles pick from. Null only in settings
/// stored before sources existed, which the engine converts on load.
/// </param>
public sealed record AppSettings(
    IReadOnlyList<StationProfile> Profiles,
    string? ActiveProfileId,
    UnitSettings Units,
    bool ReadOnly = false,
    StationAging? Aging = null,
    IReadOnlyList<SourceSettings>? Sources = null,
    StationToggles? Toggles = null,
    CaptureSettings? Capture = null,
    UpdateSettings? Updates = null,
    LoggingSettings? Logging = null,
    DeviceListSettings? DeviceList = null
)
{
    /// <summary>The device list's settings in effect (docs/features/F4.16-device-list.md): <see cref="DeviceList"/>, or the defaults.</summary>
    public DeviceListSettings EffectiveDeviceList => DeviceList ?? DeviceListSettings.Default;

    /// <summary>Logging's settings in effect (docs/features/F1.32-logging.md): <see cref="Logging"/>, or the defaults.</summary>
    public LoggingSettings EffectiveLogging => Logging ?? LoggingSettings.Default;

    /// <summary>The update check's settings in effect (docs/features/F4.15-updates.md): <see cref="Updates"/>, or the defaults.</summary>
    public UpdateSettings EffectiveUpdates => Updates ?? UpdateSettings.Default;

    /// <summary>Packet capture's settings in effect (docs/features/F1.27-packet-capture.md): <see cref="Capture"/>, or off.</summary>
    public CaptureSettings EffectiveCapture => Capture ?? CaptureSettings.Off;

    /// <summary>The toggles in effect: <see cref="Toggles"/>, or everything on.</summary>
    public StationToggles EffectiveToggles => Toggles ?? StationToggles.AllOn;

    /// <summary>The sources, or none.</summary>
    public IReadOnlyList<SourceSettings> SourceList => Sources ?? [];

    /// <summary>The APRS-IS source <paramref name="profile"/> uses, or null when it uses none (F1.25 behavior 3).</summary>
    public SourceSettings? AprsIsSourceOf(StationProfile? profile) =>
        profile is null
            ? null
            : SourceList.FirstOrDefault(s => s.Kind == SourceKind.AprsIs && profile.SourceIds.Contains(s.Id));

    /// <summary>The aging in effect: <see cref="Aging"/>, or the defaults.</summary>
    public StationAging EffectiveAging => Aging ?? StationAging.Default;

    /// <summary>No profiles yet: the state before first-run setup.</summary>
    public static AppSettings Empty { get; } = new([], null, UnitSettings.Metric);

    /// <summary>The active profile, or null when none is set.</summary>
    public StationProfile? GetActiveProfile() => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);
}

/// <summary>
/// The station's quick toggles (docs/features/F1.15-quick-toggles.md): what it may do now, saved with its settings so a
/// restart keeps a quiet station quiet (decision 2).
/// </summary>
/// <param name="Internet">The master switch: off stops APRS-IS, tile downloads and address lookup (decision 1).</param>
/// <param name="AprsIs">Connect to APRS-IS.</param>
/// <param name="Tiles">Download map tiles; cached ones still show.</param>
/// <param name="SourcesOff">
/// The ids of the other sources switched off (KISS TNCs, docs/features/F3.1-kiss-sources.md behavior 6); null or
/// empty when all are on. The Internet switch doesn't turn them off: a TNC is local equipment.
/// </param>
/// <param name="Transmit">
/// Transmitting (docs/features/F2.1-own-station.md, behaviors 4–8): off until the user turns it on and the engine has
/// checked the station may; saving settings never turns it on.
/// </param>
public sealed record StationToggles(
    bool Internet = true,
    bool AprsIs = true,
    bool Tiles = true,
    IReadOnlyList<string>? SourcesOff = null,
    bool Transmit = false
)
{
    /// <summary>Every receiving toggle on, and transmitting off.</summary>
    public static StationToggles AllOn { get; } = new();

    /// <summary>Whether the source with <paramref name="id"/> is switched on.</summary>
    public bool IsSourceOn(string id) => SourcesOff is null || !SourcesOff.Contains(id);

    /// <summary>These toggles with the source <paramref name="id"/> switched <paramref name="on"/> or off.</summary>
    public StationToggles WithSource(string id, bool on) =>
        this with
        {
            SourcesOff = on
                ? [.. (SourcesOff ?? []).Where(s => s != id)]
                : [.. (SourcesOff ?? []).Where(s => s != id), id],
        };

    /// <summary>Whether APRS-IS may connect: it and the Internet are on.</summary>
    public bool ConnectsAprsIs => Internet && AprsIs;

    /// <summary>Whether tiles may be downloaded: they and the Internet are on.</summary>
    public bool DownloadsTiles => Internet && Tiles;
}

/// <summary>A quick toggle (docs/features/F1.15-quick-toggles.md, behavior 1).</summary>
public enum Toggle
{
    /// <summary>The Internet master switch.</summary>
    Internet,

    /// <summary>APRS-IS.</summary>
    AprsIs,

    /// <summary>Map tile downloads.</summary>
    Tiles,

    /// <summary>Transmitting (docs/features/F2.1-own-station.md, behavior 4).</summary>
    Transmit,
}

/// <summary>
/// When stations, objects and items fade and leave the store (docs/features/F1.3-station-store.md, behaviors 7–9).
/// The station's setting, not the viewer's: the engine applies it for everyone.
/// </summary>
/// <param name="StaleAfterMinutes">Not heard for this long: faded on the map, greyed in lists.</param>
/// <param name="RemoveAfterMinutes">Not heard for this long: removed; null never removes.</param>
/// <param name="TrackPoints">The most positions a track keeps (docs/features/F1.5-tracks.md, behavior 1).</param>
/// <param name="TrackMinutes">How long a track keeps a position.</param>
public sealed record StationAging(
    int StaleAfterMinutes,
    int? RemoveAfterMinutes,
    int TrackPoints = StationAging.DefaultTrackPoints,
    int TrackMinutes = StationAging.DefaultTrackMinutes
)
{
    /// <summary>A track's default length (decision 1).</summary>
    public const int DefaultTrackPoints = 500;

    /// <summary>A track's default age, matching the default removal time (decision 1).</summary>
    public const int DefaultTrackMinutes = 120;

    /// <summary>The most positions a track may be set to keep.</summary>
    public const int MaxTrackPoints = 5_000;

    /// <summary>Stale after 30 minutes, removed after 2 hours, tracks of 500 points and 2 hours.</summary>
    public static StationAging Default { get; } = new(30, 120);
}

/// <summary>
/// One station identity and how it connects: for example home, portable, or a club callsign. Switching the active
/// profile reconnects to APRS-IS with its login and filter.
/// </summary>
/// <param name="Id">Stable identifier; never shown.</param>
/// <param name="Name">What the user calls it.</param>
/// <param name="Callsign">The APRS-IS login and station callsign, with SSID (for example <c>N0CALL-9</c>).</param>
/// <param name="Passcode">The APRS-IS passcode, or -1 to log in receive-only.</param>
/// <param name="SourceIds">
/// The sources this profile uses (F1.25 behavior 4), by <see cref="SourceSettings.Id"/>: at most one APRS-IS source.
/// </param>
/// <param name="Latitude">The station's latitude in degrees, when set.</param>
/// <param name="Longitude">The station's longitude in degrees, when set.</param>
/// <param name="RangeKm">
/// How far around the station APRS-IS sends packets, in km (F1.2 behavior 1); 0 sends no range part, as an IGate wants.
/// </param>
/// <param name="Symbol">
/// The station's APRS symbol, table or overlay then code (docs/features/F2.1-own-station.md, behavior 1); null means
/// <see cref="DefaultSymbol"/>.
/// </param>
/// <param name="Comment">The comment sent with the station's positions (F2.1 behavior 2), or null for none.</param>
/// <param name="Licensed">
/// The user confirmed they're a licensed amateur who may transmit with this callsign (F2.1 behavior 4.1); asked once per
/// profile. A listener ID (<see cref="ListenerId"/>) is never licensed.
/// </param>
/// <param name="PositionBeacon">The position beacon (docs/features/F2.2-beacons.md, behavior 1); null is off.</param>
/// <param name="StatusBeacon">The status beacon (F2.2 behavior 3); null is off.</param>
/// <param name="PositionConfirmed">
/// Whether a position has been sent by hand since the callsign, symbol, comment or location last changed (F2.1 behavior
/// 12): automatic transmissions wait for it. Set by the engine; ignored on save.
/// </param>
/// <param name="Groups">
/// The ANSRVR groups joined (docs/features/F2.5-bulletins.md, behavior 10), kept alive while joined; set by joining and
/// leaving, null for none.
/// </param>
/// <param name="Ping">The ping responder (docs/features/F2.13-ping.md); null for off.</param>
/// <param name="AutoReply">The away message (docs/features/F2.10-auto-reply.md); null for off.</param>
/// <param name="IGate">Whether it's a receive-only IGate (docs/features/F3.4-rx-igate.md); null for off.</param>
/// <param name="AnswerQueries">Whether APRS queries are answered (docs/features/F2.8-queries.md); null for on.</param>
/// <param name="Objects">
/// The objects and items it places (docs/features/F2.7-objects-and-items.md); changed only by
/// <see cref="IEngineService.PlaceObjectAsync"/> and <see cref="IEngineService.RemoveObjectAsync"/>.
/// </param>
public sealed record StationProfile(
    string Id,
    string Name,
    string Callsign,
    int Passcode,
    IReadOnlyList<string> SourceIds,
    double? Latitude,
    double? Longitude,
    int RangeKm = StationProfile.DefaultRangeKm,
    string? Symbol = null,
    string? Comment = null,
    bool PositionConfirmed = false,
    bool Licensed = false,
    PositionBeaconSettings? PositionBeacon = null,
    StatusBeaconSettings? StatusBeacon = null,
    IReadOnlyList<string>? Groups = null,
    PingSettings? Ping = null,
    AutoReplySettings? AutoReply = null,
    IReadOnlyList<OwnObject>? Objects = null,
    bool? AnswerQueries = null,
    bool? IGate = null,
    IReadOnlyList<StationList>? StationLists = null,
    bool? IGateToRf = null
)
{
    /// <summary>
    /// Whether the IGate also passes messages from APRS-IS to RF (docs/features/F3.4-two-way-igate.md, behavior 1): off
    /// unless turned on, and only with <see cref="EffectiveIGate"/>.
    /// </summary>
    public bool EffectiveIGateToRf => EffectiveIGate && (IGateToRf ?? false);

    /// <summary>Its station lists (docs/features/F1.33-station-lists.md, behavior 1); none when unset.</summary>
    public IReadOnlyList<StationList> StationListSet => StationLists ?? [];

    /// <summary>
    /// Whether the profile gates what its TNCs hear to APRS-IS, as a receive-only IGate (docs/features/F3.4-rx-igate.md,
    /// behavior 1); off unless turned on.
    /// </summary>
    public bool EffectiveIGate => IGate ?? false;

    /// <summary>
    /// Whether APRS queries to the station are answered (docs/features/F2.8-queries.md, behavior 1): on unless turned
    /// off.
    /// </summary>
    public bool EffectiveAnswerQueries => AnswerQueries ?? true;

    /// <summary>The objects and items it places (docs/features/F2.7-objects-and-items.md); none when unset.</summary>
    public IReadOnlyList<OwnObject> ObjectList => Objects ?? [];

    /// <summary>The away message's settings in effect (docs/features/F2.10-auto-reply.md): <see cref="AutoReply"/>, or off.</summary>
    public AutoReplySettings EffectiveAutoReply => AutoReply ?? AutoReplySettings.Off;

    /// <summary>The ping responder's settings in effect (docs/features/F2.13-ping.md): <see cref="Ping"/>, or off.</summary>
    public PingSettings EffectivePing => Ping ?? PingSettings.Off;

    /// <summary>The range a new profile gets: 50 km (F1.2 behavior 1.1).</summary>
    public const int DefaultRangeKm = 50;

    /// <summary>The symbol a profile without one has: a house, <c>/-</c>.</summary>
    public const string DefaultSymbol = "/-";

    /// <summary>The most characters a comment may have (F2.1 behavior 2).</summary>
    public const int MaxCommentLength = 43;

    /// <summary>The symbol in effect: <see cref="Symbol"/>, or <see cref="DefaultSymbol"/>.</summary>
    public string EffectiveSymbol => string.IsNullOrEmpty(Symbol) ? DefaultSymbol : Symbol;

    /// <summary>The position beacon's settings, off when there are none.</summary>
    public PositionBeaconSettings EffectivePositionBeacon => PositionBeacon ?? PositionBeaconSettings.Off;

    /// <summary>The status beacon's settings, off when there are none.</summary>
    public StatusBeaconSettings EffectiveStatusBeacon => StatusBeacon ?? StatusBeaconSettings.Off;

    /// <summary>
    /// Whether going from <paramref name="before"/> to this changes what a position report says, which needs a new
    /// manual send before automatic ones (F2.1 behavior 12).
    /// </summary>
    public bool ChangesWhatIsSent(StationProfile? before) =>
        before is null
        || Callsign != before.Callsign
        || EffectiveSymbol != before.EffectiveSymbol
        || (Comment ?? string.Empty) != (before.Comment ?? string.Empty)
        || Latitude != before.Latitude
        || Longitude != before.Longitude
        || EffectivePositionBeacon.Compressed != before.EffectivePositionBeacon.Compressed
        || EffectivePositionBeacon.MicE is null != before.EffectivePositionBeacon.MicE is null // the format, not the status
        || EffectivePositionBeacon.Phg != before.EffectivePositionBeacon.Phg;

    /// <summary>Equal when every field is, comparing the source ids in order.</summary>
    public bool Equals(StationProfile? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Callsign == other.Callsign
        && Passcode == other.Passcode
        && SourceIds.SequenceEqual(other.SourceIds)
        && Latitude == other.Latitude
        && Longitude == other.Longitude
        && RangeKm == other.RangeKm
        && Symbol == other.Symbol
        && Comment == other.Comment
        && PositionConfirmed == other.PositionConfirmed
        && Licensed == other.Licensed
        && EffectivePositionBeacon == other.EffectivePositionBeacon
        && EffectiveStatusBeacon == other.EffectiveStatusBeacon
        && EffectivePing == other.EffectivePing
        && EffectiveAutoReply == other.EffectiveAutoReply
        && ObjectList.SequenceEqual(other.ObjectList)
        && EffectiveAnswerQueries == other.EffectiveAnswerQueries
        && EffectiveIGate == other.EffectiveIGate;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(Id, Name, Callsign, Passcode, string.Join(',', SourceIds), Latitude, Longitude, RangeKm),
            Symbol,
            Comment,
            PositionConfirmed,
            Licensed,
            HashCode.Combine(EffectivePositionBeacon, EffectiveStatusBeacon, EffectivePing, EffectiveAutoReply)
        );

    /// <summary>Whether this profile uses <paramref name="source"/>.</summary>
    public bool Uses(SourceSettings source) => source is not null && SourceIds.Contains(source.Id);
}

/// <summary>Display units, chosen per quantity.</summary>
public sealed record UnitSettings(
    AltitudeUnit Altitude,
    DistanceUnit Distance,
    PressureUnit Pressure,
    RainUnit Rain,
    TemperatureUnit Temperature,
    SpeedUnit Wind
)
{
    /// <summary>Metric throughout.</summary>
    public static UnitSettings Metric { get; } =
        new(
            AltitudeUnit.Metres,
            DistanceUnit.Kilometres,
            PressureUnit.Hectopascals,
            RainUnit.Millimetres,
            TemperatureUnit.Celsius,
            SpeedUnit.KilometresPerHour
        );

    /// <summary>US customary units.</summary>
    public static UnitSettings Imperial { get; } =
        new(
            AltitudeUnit.Feet,
            DistanceUnit.Miles,
            PressureUnit.InchesOfMercury,
            RainUnit.Inches,
            TemperatureUnit.Fahrenheit,
            SpeedUnit.MilesPerHour
        );
}

/// <summary>Altitude unit.</summary>
public enum AltitudeUnit
{
    /// <summary>Metres.</summary>
    Metres,

    /// <summary>Feet.</summary>
    Feet,
}

/// <summary>Distance unit.</summary>
public enum DistanceUnit
{
    /// <summary>Kilometres.</summary>
    Kilometres,

    /// <summary>Statute miles.</summary>
    Miles,

    /// <summary>Nautical miles.</summary>
    NauticalMiles,
}

/// <summary>Air pressure unit.</summary>
public enum PressureUnit
{
    /// <summary>Hectopascals (the same as millibars).</summary>
    Hectopascals,

    /// <summary>Inches of mercury.</summary>
    InchesOfMercury,
}

/// <summary>Rainfall unit.</summary>
public enum RainUnit
{
    /// <summary>Millimetres.</summary>
    Millimetres,

    /// <summary>Inches.</summary>
    Inches,
}

/// <summary>Temperature unit.</summary>
public enum TemperatureUnit
{
    /// <summary>Degrees Celsius.</summary>
    Celsius,

    /// <summary>Degrees Fahrenheit.</summary>
    Fahrenheit,
}

/// <summary>Speed unit, used for wind.</summary>
public enum SpeedUnit
{
    /// <summary>Kilometres per hour.</summary>
    KilometresPerHour,

    /// <summary>Miles per hour.</summary>
    MilesPerHour,

    /// <summary>Knots.</summary>
    Knots,

    /// <summary>Metres per second.</summary>
    MetresPerSecond,
}
