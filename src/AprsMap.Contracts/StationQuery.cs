// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// One page of the station list (docs/features/F1.7-station-list.md): the engine searches, filters, sorts and pages,
/// so a browser never downloads the whole store.
/// </summary>
/// <param name="Search">Part of the name, ignoring case (<c>W1A</c> finds <c>W1AW-9</c>, <c>-9</c> every SSID 9); null for all.</param>
/// <param name="Kinds">Stations, objects and items, or all.</param>
/// <param name="Sort">The order.</param>
/// <param name="Offset">How many rows to skip.</param>
/// <param name="Count">How many rows to return, at most <see cref="MaxCount"/>.</param>
public sealed record StationQuery(
    string? Search = null,
    StationKinds Kinds = StationKinds.All,
    StationSort Sort = StationSort.LastHeard,
    int Offset = 0,
    int Count = 100,
    StationFilter? Filter = null
)
{
    /// <summary>The most rows one page holds.</summary>
    public const int MaxCount = 500;
}

/// <summary>Which entries the station list shows (F1.7 behavior 4).</summary>
public enum StationKinds
{
    /// <summary>Everything.</summary>
    All,

    /// <summary>Stations only.</summary>
    Stations,

    /// <summary>Objects and items only.</summary>
    ObjectsAndItems,
}

/// <summary>The station list's order (F1.7 behavior 2).</summary>
public enum StationSort
{
    /// <summary>Most recently heard first.</summary>
    LastHeard,

    /// <summary>By name, A to Z.</summary>
    Name,

    /// <summary>Nearest to my location first; entries without a position, or with no location of mine, last.</summary>
    Distance,
}

/// <summary>One row of the station list (F1.7 behavior 1).</summary>
/// <param name="Name">The callsign, or the object's or item's name.</param>
/// <param name="Kind">A station, an object or an item.</param>
/// <param name="Symbol">The symbol as transmitted, when known.</param>
/// <param name="LastHeard">When it was last heard.</param>
/// <param name="IsStale">Not heard for the stale time: the row is greyed.</param>
/// <param name="DistanceKm">Distance from my location, when both are known.</param>
/// <param name="BearingDegrees">Bearing from my location, when both are known.</param>
/// <param name="Comment">The start of its comment, or of its status when it has no comment.</param>
/// <param name="HasPosition">Whether it has a position, so the map can centre on it.</param>
public sealed record StationRow(
    string Name,
    StationKind Kind,
    string? Symbol,
    DateTimeOffset LastHeard,
    bool IsStale,
    double? DistanceKm,
    int? BearingDegrees,
    string? Comment,
    bool HasPosition,
    bool HasProblems = false
);

/// <summary>A page of the station list, and how many rows match in all.</summary>
/// <param name="Rows">The rows, in order.</param>
/// <param name="Total">How many entries match the search and kinds.</param>
public sealed record StationPage(IReadOnlyList<StationRow> Rows, int Total);

/// <summary>
/// The viewer's View filter (docs/features/F1.9-view-filters.md, behavior 1): a station is shown when it matches every
/// condition set. Emergencies are always shown (behavior 3). Conditions left at their defaults don't filter.
/// </summary>
/// <param name="Kinds">Stations, objects and items, or some of them.</param>
/// <param name="Categories">Symbol categories shown (APRS.NET's <c>AprsSymbolCategory</c> names); null or empty for all.</param>
/// <param name="HeardWithinMinutes">Only those heard this recently; null for any time.</param>
/// <param name="Reception">How their last packet arrived.</param>
/// <param name="MessageCapable">Only stations whose position says they take messages.</param>
/// <param name="WithWeather">Only stations that send weather (F1.10).</param>
/// <param name="WithTrack">Only stations with a track of two or more positions (F1.5).</param>
/// <param name="WithTelemetry">Only stations that send telemetry (F1.11).</param>
/// <param name="HasProblems">Only stations whose packets had problems in the last hour (F1.20).</param>
/// <param name="DeviceClasses">Device classes shown (aprs-deviceid's, such as <c>Tracker</c>); null or empty for all.</param>
/// <param name="MicEStatuses">Mic-E statuses shown (APRS.NET's <c>MicEMessage</c> names); null or empty for all.</param>
/// <param name="Tags">Comment and status tags shown (<c>!word!</c> and <c>#word</c>, as written); null or empty for all.</param>
public sealed record StationFilter(
    StationKinds Kinds = StationKinds.All,
    IReadOnlyList<string>? Categories = null,
    int? HeardWithinMinutes = null,
    Reception Reception = Reception.Any,
    bool MessageCapable = false,
    bool WithWeather = false,
    bool WithTrack = false,
    IReadOnlyList<string>? DeviceClasses = null,
    IReadOnlyList<string>? MicEStatuses = null,
    IReadOnlyList<string>? Tags = null,
    bool WithTelemetry = false,
    bool HasProblems = false
)
{
    /// <summary>Shows everything.</summary>
    public static StationFilter None { get; } = new();

    /// <summary>Whether no condition is set.</summary>
    public bool IsEmpty =>
        Kinds == StationKinds.All
        && Categories is null or { Count: 0 }
        && HeardWithinMinutes is null
        && Reception == Reception.Any
        && !MessageCapable
        && !WithWeather
        && !WithTrack
        && DeviceClasses is null or { Count: 0 }
        && MicEStatuses is null or { Count: 0 }
        && Tags is null or { Count: 0 }
        && !WithTelemetry
        && !HasProblems;
}

/// <summary>How a station's last packet arrived, for the View filter (F1.9 behavior 1.4).</summary>
public enum Reception
{
    /// <summary>However it arrived.</summary>
    Any,

    /// <summary>Heard directly on RF by an IGate.</summary>
    Direct,

    /// <summary>Repeated by digipeaters on RF.</summary>
    Digipeated,

    /// <summary>Heard on RF, directly or through digipeaters.</summary>
    AnyRf,

    /// <summary>From the internet, never on RF.</summary>
    Internet,
}

/// <summary>
/// The values heard so far, with how many stations have each, for the View filter's lists (F1.9 behavior 2): only what
/// has been heard, so a list never offers an empty choice.
/// </summary>
public sealed record ViewFacets(
    IReadOnlyList<Facet> Categories,
    IReadOnlyList<Facet> DeviceClasses,
    IReadOnlyList<Facet> MicEStatuses,
    IReadOnlyList<Facet> Tags
);

/// <summary>One value of a <see cref="ViewFacets"/> list and how many stations have it.</summary>
public sealed record Facet(string Value, int Count);
