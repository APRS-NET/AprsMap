// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>How far back the dashboard looks (docs/features/F1.22-statistics.md, behavior 3).</summary>
public enum DashboardRange
{
    /// <summary>The last hour, a point a minute.</summary>
    Hour,

    /// <summary>The last 6 hours, a point every 5 minutes.</summary>
    SixHours,

    /// <summary>The last 24 hours, a point every 15 minutes.</summary>
    Day,
}

/// <summary>
/// The dashboard's numbers over one range (docs/features/F1.22-statistics.md, behaviors 3, 4). Every series has one
/// value a point, oldest first, the first at <see cref="Start"/> and each <see cref="Step"/> after; the last point is
/// the one in progress.
/// </summary>
/// <param name="At">When the numbers were taken.</param>
/// <param name="Range">The range asked for.</param>
/// <param name="Start">The first point's time.</param>
/// <param name="Step">The time between points.</param>
/// <param name="HistoryStart">
/// When counting began: the engine's start, or the last time packets were cleared. Points before it are zero because
/// nothing was counted, not because nothing arrived.
/// </param>
/// <param name="Packets">Packets received a point, duplicates included.</param>
/// <param name="PacketsPerMinute">The rate now: packets a minute over the last 5 minutes.</param>
/// <param name="PacketsBySource">Packets a point for each source (F1.25), keyed by source id; empty with one source.</param>
/// <param name="StationsHeard">Distinct stations heard a point.</param>
/// <param name="StationsHeardTotal">Distinct stations heard in the whole range.</param>
/// <param name="NewStations">Stations heard for the first time a point (behavior 4.2).</param>
/// <param name="NewStationsTotal">New stations in the whole range.</param>
/// <param name="Newest">The newest stations, newest first, at most five, with when each was first heard.</param>
/// <param name="Arrivals">Packets a point by how they arrived, keyed by <see cref="ArrivalKind"/> name.</param>
/// <param name="Kinds">Packets in the range by <see cref="PacketKind"/> name, most first.</param>
/// <param name="Problems">Invalid packets and rejected lines a point (behavior 4.6).</param>
/// <param name="ProblemsTotal">Invalid packets and rejected lines in the whole range.</param>
/// <param name="BusiestStations">The stations that sent the most packets in the range, most first, at most ten.</param>
/// <param name="BusiestRelays">
/// The digipeaters and IGates that relayed or gated the most RF packets in the range, most first, at most ten.
/// </param>
/// <param name="Sources">When each source last delivered a packet, keyed by source id (an empty id for none).</param>
/// <param name="FarthestDirect">
/// The longest direct RF hops among stations heard in the range: a station and the IGate that heard it with no
/// digipeater, farthest first, at most five (behavior 4.10).
/// </param>
/// <param name="Weather">The weather stations heard in the range, and their extremes (behavior 4.11).</param>
/// <param name="Tables">What's on the air among the stations held now (behavior 10).</param>
/// <param name="MyView">
/// Counted only for the stations a view filter shows (behavior 7), with points of at least 5 minutes. Packets by
/// source, the busiest relays and each source's last packet are the whole feed's, since they aren't kept per station.
/// </param>
/// <param name="Gps">
/// The station's GPS over the range (docs/features/F3.14-gps-dashboard.md); null when the profile had no GPS in it.
/// </param>
public sealed record Dashboard(
    DateTimeOffset At,
    DashboardRange Range,
    DateTimeOffset Start,
    TimeSpan Step,
    DateTimeOffset HistoryStart,
    IReadOnlyList<int> Packets,
    double PacketsPerMinute,
    IReadOnlyList<DashboardSeries> PacketsBySource,
    IReadOnlyList<int> StationsHeard,
    int StationsHeardTotal,
    IReadOnlyList<int> NewStations,
    int NewStationsTotal,
    IReadOnlyList<DashboardMoment> Newest,
    IReadOnlyList<DashboardSeries> Arrivals,
    IReadOnlyList<DashboardCount> Kinds,
    IReadOnlyList<int> Problems,
    int ProblemsTotal,
    IReadOnlyList<DashboardCount> BusiestStations,
    IReadOnlyList<DashboardCount> BusiestRelays,
    IReadOnlyList<DashboardMoment> Sources,
    IReadOnlyList<DashboardHop>? FarthestDirect = null,
    DashboardWeather? Weather = null,
    DashboardTables? Tables = null,
    bool MyView = false,
    DashboardGps? Gps = null
);

/// <summary>A station heard directly by an IGate, and how far apart they are.</summary>
public sealed record DashboardHop(string Station, string Gateway, double DistanceKm, DateTimeOffset At);

/// <summary>
/// The weather stations heard in the range and the extremes of their latest readings, in the engine's units (°C and
/// m/s); the UI converts them.
/// </summary>
/// <param name="Stations">How many reported weather.</param>
/// <param name="Coldest">The lowest temperature and its station.</param>
/// <param name="Warmest">The highest temperature and its station.</param>
/// <param name="Gustiest">The strongest gust and its station.</param>
public sealed record DashboardWeather(
    int Stations,
    DashboardReading? Coldest = null,
    DashboardReading? Warmest = null,
    DashboardReading? Gustiest = null
);

/// <summary>A station's reading.</summary>
public sealed record DashboardReading(string Station, double Value);

/// <summary>The statistics tables over the stations held now (behavior 10), each row with its count, most first.</summary>
/// <param name="Total">The stations held.</param>
/// <param name="Platforms">Devices by class, vendor and model (G07).</param>
/// <param name="Symbols">Symbols, with their names and categories.</param>
/// <param name="Kinds">Stations, objects and items, by <see cref="StationKind"/> name.</param>
/// <param name="Reception">How they last arrived, by <see cref="ArrivalKind"/> name.</param>
public sealed record DashboardTables(
    int Total,
    IReadOnlyList<DashboardPlatform> Platforms,
    IReadOnlyList<DashboardSymbol> Symbols,
    IReadOnlyList<DashboardCount> Kinds,
    IReadOnlyList<DashboardCount> Reception
);

/// <summary>A device and how many stations use it; null names when the device list doesn't know it.</summary>
public sealed record DashboardPlatform(string? Class, string? Vendor, string? Model, int Count);

/// <summary>A symbol, its name and category, and how many stations show it.</summary>
public sealed record DashboardSymbol(string Symbol, string Name, string Category, int Count);

/// <summary>A named series, a value a point.</summary>
public sealed record DashboardSeries(string Key, IReadOnlyList<int> Values);

/// <summary>A name and how many.</summary>
public sealed record DashboardCount(string Key, int Count);

/// <summary>A name and a time.</summary>
public sealed record DashboardMoment(string Key, DateTimeOffset At);

/// <summary>
/// The station's GPS over the dashboard's range (docs/features/F3.14-gps-dashboard.md, behaviors 1–8): the GPS in use
/// sampled once a second. Times and counts cover the range; "now" values are the latest sample's. Each series has a
/// value a point like the dashboard's other series, null for a point with no samples (or none with a fix).
/// </summary>
/// <param name="SourceId">The GPS in use for most of the range.</param>
/// <param name="OtherInUse">Whether another GPS was in use for part of the range (behavior 5).</param>
/// <param name="Fix3D">Time with a 3D fix.</param>
/// <param name="Fix2D">Time with a 2D fix.</param>
/// <param name="NoFix">Time reporting without a fix.</param>
/// <param name="Silent">Time with nothing from the GPS for 5 seconds or more (behavior 1.3).</param>
/// <param name="Losses">Fixes lost in the range (behavior 2).</param>
/// <param name="LongestOutage">The longest time without a fix of the outages in the range; null with none.</param>
/// <param name="LastOutage">The latest outage in the range; null with none.</param>
/// <param name="SatellitesUsed">Satellites used: now, and the average, lowest and highest with a fix over the range.</param>
/// <param name="SatellitesInView">Satellites in view: now, and over the range.</param>
/// <param name="Constellations">Each system seen in the range, in the order of <see cref="GnssConstellation"/>.</param>
/// <param name="Dilutions">PDOP, HDOP, VDOP, TDOP and GDOP, keyed by lower-case name; those the GPS never gave are absent.</param>
/// <param name="Errors">
/// The GPS's estimated errors in metres, keyed horizontal, vertical, latitude and longitude; those never given are absent.
/// </param>
/// <param name="Signal">The average signal of the satellites used, in dB-Hz.</param>
/// <param name="WanderMeters">
/// The radius holding about 95% of the fixes while still, twice their root-mean-square distance from their average
/// (behavior 4); null with fewer than 10 such fixes.
/// </param>
/// <param name="WanderFixes">The fixes the wander is worked out from.</param>
/// <param name="FixShare">The share of each point's samples with a fix, 0 to 100.</param>
/// <param name="SatellitesSeries">The average satellites used a point.</param>
/// <param name="HdopSeries">The average HDOP a point.</param>
/// <param name="ErrorSeries">The average estimated horizontal error a point, in metres.</param>
public sealed record DashboardGps(
    string SourceId,
    bool OtherInUse,
    TimeSpan Fix3D,
    TimeSpan Fix2D,
    TimeSpan NoFix,
    TimeSpan Silent,
    int Losses,
    TimeSpan? LongestOutage,
    DashboardOutage? LastOutage,
    DashboardStat SatellitesUsed,
    DashboardStat SatellitesInView,
    IReadOnlyList<DashboardConstellation> Constellations,
    IReadOnlyList<DashboardValue> Dilutions,
    IReadOnlyList<DashboardValue> Errors,
    DashboardStat Signal,
    double? WanderMeters,
    int WanderFixes,
    IReadOnlyList<double?> FixShare,
    IReadOnlyList<double?> SatellitesSeries,
    IReadOnlyList<double?> HdopSeries,
    IReadOnlyList<double?> ErrorSeries
);

/// <summary>A time without a fix: when it began, how long it lasted (so far, while it goes on).</summary>
public sealed record DashboardOutage(DateTimeOffset Start, TimeSpan Length, bool Ongoing);

/// <summary>A value now and over a range; each null when not known.</summary>
public sealed record DashboardStat(double? Now, double? Average, double? Lowest, double? Highest);

/// <summary>A named <see cref="DashboardStat"/>.</summary>
public sealed record DashboardValue(string Key, DashboardStat Stat);

/// <summary>A satellite system seen in the range: used and in view now, and the average used with a fix.</summary>
public sealed record DashboardConstellation(GnssConstellation System, int UsedNow, int InViewNow, double? AverageUsed);
