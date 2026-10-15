// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// The latest known state of one thing on the map: a station, or an object or item (keyed by its name). Compact on
/// purpose: the map stream carries thousands of these (docs/spikes/signalr-stream.md).
/// </summary>
/// <param name="Name">The station's callsign, or the object's or item's name.</param>
/// <param name="LastHeard">When the engine last received a position for it.</param>
/// <param name="Latitude">Latitude in degrees.</param>
/// <param name="Longitude">Longitude in degrees.</param>
/// <param name="Symbol">The symbol as transmitted: table or overlay character, then code (for example <c>/&gt;</c>).</param>
/// <param name="Kind">A station, an object or an item.</param>
/// <param name="IsStale">Not heard for the stale time (F1.3 behavior 7): the map fades it.</param>
public sealed record StationState(
    string Name,
    DateTimeOffset LastHeard,
    double Latitude,
    double Longitude,
    string Symbol,
    StationKind Kind = StationKind.Station,
    bool IsStale = false
);

/// <summary>What an entry in the station store is (APRS101 chapter 11).</summary>
public enum StationKind
{
    /// <summary>A station, keyed by its callsign with SSID.</summary>
    Station,

    /// <summary>An object, keyed by its name, owned by whoever sent it last.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "APRS's own term (APRS101 chapter 11)."
    )]
    Object,

    /// <summary>An item, keyed by its name, owned by whoever sent it last.</summary>
    Item,
}

/// <summary>
/// Everything the engine knows about one station, object or item (docs/features/F1.3-station-store.md, behavior 2),
/// fetched on demand by the details pane and the station list. Fields a packet didn't carry keep their last value.
/// </summary>
/// <param name="Name">The callsign, or the object's or item's name.</param>
/// <param name="Kind">A station, an object or an item.</param>
/// <param name="LastHeard">When the engine last received a packet for it.</param>
/// <param name="Packets">How many packets have updated it.</param>
/// <param name="IsStale">Not heard for the stale time.</param>
/// <param name="Latitude">Latitude in degrees, when a position has been heard.</param>
/// <param name="Longitude">Longitude in degrees, when a position has been heard.</param>
/// <param name="PositionAmbiguity">How many trailing digits of the position were left out (0–4).</param>
/// <param name="Symbol">The symbol as transmitted, when known.</param>
/// <param name="Comment">The latest position comment.</param>
/// <param name="Status">The latest status report's text.</param>
/// <param name="CourseDegrees">Course over ground, when reported.</param>
/// <param name="SpeedKmh">Speed in km/h, when reported (APRS sends knots).</param>
/// <param name="AltitudeMetres">Altitude in metres, when reported (APRS sends feet).</param>
/// <param name="Path">The path of the last packet, as received (for example <c>WIDE1-1,qAR,N0IGT</c>).</param>
/// <param name="Owner">For an object or item: the station that sent its latest packet.</param>
/// <param name="PreviousOwner">For an object or item: the owner before <paramref name="Owner"/>, if it changed hands.</param>
public sealed record StationDetail(
    string Name,
    StationKind Kind,
    DateTimeOffset LastHeard,
    long Packets,
    bool IsStale,
    double? Latitude,
    double? Longitude,
    int PositionAmbiguity,
    string? Symbol,
    string? Comment,
    string? Status,
    int? CourseDegrees,
    double? SpeedKmh,
    double? AltitudeMetres,
    string? Path,
    string? Owner,
    string? PreviousOwner
);

/// <summary>
/// One step of <see cref="IEngineService.WatchStationsAsync"/>: the stations that changed since the last batch (latest
/// state only) and the names that left the map.
/// </summary>
/// <param name="Changed">Stations that are new or changed.</param>
/// <param name="Removed">Names to take off the map, such as killed objects.</param>
public sealed record StationBatch(IReadOnlyList<StationState> Changed, IReadOnlyList<string> Removed);
