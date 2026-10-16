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
    IReadOnlyList<SourceSettings>? Sources = null
)
{
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
/// When stations, objects and items fade and leave the store (docs/features/F1.3-station-store.md, behaviors 7–9).
/// The station's setting, not the viewer's: the engine applies it for everyone.
/// </summary>
/// <param name="StaleAfterMinutes">Not heard for this long: faded on the map, greyed in lists.</param>
/// <param name="RemoveAfterMinutes">Not heard for this long: removed; null never removes.</param>
public sealed record StationAging(int StaleAfterMinutes, int? RemoveAfterMinutes)
{
    /// <summary>Stale after 30 minutes, removed after 2 hours.</summary>
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
public sealed record StationProfile(
    string Id,
    string Name,
    string Callsign,
    int Passcode,
    IReadOnlyList<string> SourceIds,
    double? Latitude,
    double? Longitude,
    int RangeKm = StationProfile.DefaultRangeKm
)
{
    /// <summary>The range a new profile gets: 150 km.</summary>
    public const int DefaultRangeKm = 150;

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
        && RangeKm == other.RangeKm;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Id, Name, Callsign, Passcode, string.Join(',', SourceIds), Latitude, Longitude, RangeKm);

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
