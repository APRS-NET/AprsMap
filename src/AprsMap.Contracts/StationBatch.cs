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
public sealed record StationState(
    string Name,
    DateTimeOffset LastHeard,
    double Latitude,
    double Longitude,
    string Symbol
);

/// <summary>
/// One step of <see cref="IEngineService.WatchStationsAsync"/>: the stations that changed since the last batch (latest
/// state only) and the names that left the map.
/// </summary>
/// <param name="Changed">Stations that are new or changed.</param>
/// <param name="Removed">Names to take off the map, such as killed objects.</param>
public sealed record StationBatch(IReadOnlyList<StationState> Changed, IReadOnlyList<string> Removed);
