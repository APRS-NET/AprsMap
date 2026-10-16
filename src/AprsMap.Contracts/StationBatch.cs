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
/// <param name="DistanceKm">Great-circle distance from my location (F1.13 behavior 4); null without one.</param>
/// <param name="BearingDegrees">Initial bearing from my location, 0–359 (F1.13 behavior 4); null without one.</param>
public sealed record StationState(
    string Name,
    DateTimeOffset LastHeard,
    double Latitude,
    double Longitude,
    string Symbol,
    StationKind Kind = StationKind.Station,
    bool IsStale = false,
    double? DistanceKm = null,
    int? BearingDegrees = null
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
/// <param name="DistanceKm">Great-circle distance from my location (F1.13 behavior 4); null when either is unknown.</param>
/// <param name="BearingDegrees">Initial bearing from my location, 0–359; null when either position is unknown.</param>
/// <param name="Locator">The position's Maidenhead locator, 6 characters, when a position has been heard.</param>
/// <param name="Device">What sent the last packet, from the aprs-deviceid list (G07), when known.</param>
/// <param name="Arrival">How the last packet reached the engine, from its path (G20).</param>
/// <param name="LastPacket">The last packet as received (TNC2 text, Latin-1).</param>
/// <param name="FirstHeardVia">The source its latest report arrived on first (F1.25 behavior 9).</param>
/// <param name="SeenOn">Other sources its latest report was heard on.</param>
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
    string? PreviousOwner,
    double? DistanceKm = null,
    int? BearingDegrees = null,
    string? Locator = null,
    StationDevice? Device = null,
    StationArrival? Arrival = null,
    string? LastPacket = null,
    string? FirstHeardVia = null,
    IReadOnlyList<string>? SeenOn = null
);

/// <summary>The equipment or software that sent a packet (docs/features/F1.6-station-details.md, behavior 2.7).</summary>
/// <param name="Vendor">The maker, for example <c>Yaesu</c>.</param>
/// <param name="Model">The model, for example <c>FTM-500D</c>.</param>
/// <param name="Class">
/// The aprs-deviceid class name, such as <c>Tracker</c>, <c>Rig</c>, <c>HandHeld</c>, <c>Software</c> or
/// <c>Digipeater</c>; the UI words it.
/// </param>
public sealed record StationDevice(string? Vendor, string? Model, string? Class);

/// <summary>How a packet reached the engine (docs/features/F1.6-station-details.md, behavior 2.6).</summary>
/// <param name="Kind">Heard directly on RF, through digipeaters, from the internet, or not said by the path.</param>
/// <param name="Digipeaters">The digipeaters that repeated it and named themselves, in order.</param>
/// <param name="Gateway">The IGate that put it on APRS-IS, or the server the sender connected to; null when unknown.</param>
public sealed record StationArrival(ArrivalKind Kind, IReadOnlyList<string> Digipeaters, string? Gateway);

/// <summary>How a packet reached the engine.</summary>
public enum ArrivalKind
{
    /// <summary>The path doesn't say (a server passing it on, say).</summary>
    Unknown,

    /// <summary>Heard on RF straight from the sender, with no digipeater.</summary>
    Direct,

    /// <summary>Repeated by digipeaters on RF.</summary>
    Digipeated,

    /// <summary>Sent over the internet, never on RF.</summary>
    Internet,
}

/// <summary>
/// One step of <see cref="IEngineService.WatchStationsAsync"/>: the stations that changed since the last batch (latest
/// state only) and the names that left the map.
/// </summary>
/// <param name="Changed">Stations that are new or changed.</param>
/// <param name="Removed">Names to take off the map, such as killed objects; their tracks go with them.</param>
/// <param name="Tracks">
/// Track changes since the last batch, one per station (docs/features/F1.5-tracks.md); the first batches carry every
/// track as a reset. Null when no track changed.
/// </param>
public sealed record StationBatch(
    IReadOnlyList<StationState> Changed,
    IReadOnlyList<string> Removed,
    IReadOnlyList<TrackUpdate>? Tracks = null
);

/// <summary>One point of a station's position history (docs/features/F1.5-tracks.md, behavior 1).</summary>
/// <param name="Latitude">Latitude in degrees.</param>
/// <param name="Longitude">Longitude in degrees.</param>
/// <param name="Time">When the station was last heard at this point (a report within 20 m moves the time on).</param>
public sealed record TrackPoint(double Latitude, double Longitude, DateTimeOffset Time);

/// <summary>
/// How one station's track changed: apply <see cref="Reset"/> first (forget the points held), then append
/// <see cref="Added"/>, then drop <see cref="Dropped"/> points from the oldest end. The result matches the engine's.
/// </summary>
/// <param name="Name">The station, object or item.</param>
/// <param name="Added">New points, oldest first.</param>
/// <param name="Dropped">How many of the oldest points to drop, for the history's length and age limits.</param>
/// <param name="Reset">Forget the points held first: a new viewer's snapshot, or a cleared track.</param>
public sealed record TrackUpdate(string Name, IReadOnlyList<TrackPoint> Added, int Dropped = 0, bool Reset = false);
