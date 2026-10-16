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
    int Count = 100
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
    bool HasPosition
);

/// <summary>A page of the station list, and how many rows match in all.</summary>
/// <param name="Rows">The rows, in order.</param>
/// <param name="Total">How many entries match the search and kinds.</param>
public sealed record StationPage(IReadOnlyList<StationRow> Rows, int Total);
