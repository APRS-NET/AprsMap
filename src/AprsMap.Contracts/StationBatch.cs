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
/// <param name="Weather">For a station that reports weather: what its map label and wind barb show (F1.10).</param>
/// <param name="Area">For an area object: the shape it draws (F1.12).</param>
/// <param name="Signpost">For a signpost: the 1–3 characters it shows (F1.12).</param>
public sealed record StationState(
    string Name,
    DateTimeOffset LastHeard,
    double Latitude,
    double Longitude,
    string Symbol,
    StationKind Kind = StationKind.Station,
    bool IsStale = false,
    double? DistanceKm = null,
    int? BearingDegrees = null,
    WeatherSummary? Weather = null,
    AreaInfo? Area = null,
    string? Signpost = null
);

/// <summary>
/// The shape an area object draws (docs/features/F1.12-area-objects.md, behaviors 1–3; APRS101 ch. 11 with APRS 1.1's
/// corrected sizes), anchored at the object's position.
/// </summary>
/// <param name="Kind">The shape.</param>
/// <param name="Filled">Drawn with a translucent fill rather than an outline.</param>
/// <param name="Color">APRS's colour, 0–15.</param>
/// <param name="LatitudeDegrees">The latitude extent in degrees.</param>
/// <param name="LongitudeDegrees">The longitude extent in degrees.</param>
/// <param name="CorridorKm">For a line: the corridor's width in km, when sent.</param>
public sealed record AreaInfo(
    AreaKind Kind,
    bool Filled,
    int Color,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double? CorridorKm = null
);

/// <summary>The shapes an area object draws, with where each is anchored.</summary>
public enum AreaKind
{
    /// <summary>A circle centred on the position, the extents its radii.</summary>
    Circle,

    /// <summary>A line from the position down and to the right.</summary>
    LineRight,

    /// <summary>A line from the position down and to the left.</summary>
    LineLeft,

    /// <summary>An ellipse centred on the position, the extents its radii.</summary>
    Ellipse,

    /// <summary>A triangle with its apex at the position, its base the latitude extent below and the longitude extent each side.</summary>
    Triangle,

    /// <summary>A box from the position (its top-left corner) down and to the right.</summary>
    Box,
}

/// <summary>A weather station's latest temperature and wind, for the map (docs/features/F1.10-weather.md, behaviors 4, 5).</summary>
/// <param name="TemperatureC">Temperature in °C.</param>
/// <param name="WindDirection">Where the wind blows from, in degrees.</param>
/// <param name="WindSpeedMs">Wind speed in m/s.</param>
public sealed record WeatherSummary(double? TemperatureC, int? WindDirection, double? WindSpeedMs);

/// <summary>
/// A station's latest weather readings (docs/features/F1.10-weather.md, behaviors 1, 2), each with the time it was
/// heard, in the engine's fixed units; the UI converts them. A reading never heard is null.
/// </summary>
/// <param name="WindDirection">Where the wind blows from, degrees.</param>
/// <param name="WindSpeed">Sustained wind, m/s.</param>
/// <param name="WindGust">Peak gust, m/s.</param>
/// <param name="Temperature">°C.</param>
/// <param name="RainLastHour">mm.</param>
/// <param name="RainLast24Hours">mm.</param>
/// <param name="RainSinceMidnight">mm.</param>
/// <param name="Humidity">Percent.</param>
/// <param name="Pressure">hPa.</param>
/// <param name="Luminosity">W/m².</param>
/// <param name="Snow">Snow in the last 24 hours, mm.</param>
public sealed record StationWeather(
    WeatherReading? WindDirection = null,
    WeatherReading? WindSpeed = null,
    WeatherReading? WindGust = null,
    WeatherReading? Temperature = null,
    WeatherReading? RainLastHour = null,
    WeatherReading? RainLast24Hours = null,
    WeatherReading? RainSinceMidnight = null,
    WeatherReading? Humidity = null,
    WeatherReading? Pressure = null,
    WeatherReading? Luminosity = null,
    WeatherReading? Snow = null
);

/// <summary>One weather reading and when it was heard.</summary>
/// <param name="Value">The value, in the unit its <see cref="StationWeather"/> field names.</param>
/// <param name="At">When the engine received it.</param>
public sealed record WeatherReading(double Value, DateTimeOffset At);

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
/// <param name="Weather">Its latest weather readings (F1.10), when it has sent any.</param>
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
    IReadOnlyList<string>? SeenOn = null,
    StationWeather? Weather = null
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

/// <summary>
/// Which packet's route to work out (docs/features/F1.21-rf-paths.md, behavior 2): a station's last packet, the packet
/// behind one of its track points, or a packet the caller has (from the packet list).
/// </summary>
/// <param name="Name">The station, object or item.</param>
/// <param name="Path">The packet's path as sent (<see cref="PacketView.Path"/>); null for the station's own.</param>
/// <param name="Latitude">Where the packet was sent from; null for the station's position.</param>
/// <param name="Longitude">Where the packet was sent from; null for the station's position.</param>
/// <param name="TrackPoint">The time of the track point whose packet to use (<see cref="TrackPoint.Time"/>).</param>
public sealed record RouteQuery(
    string Name,
    string? Path = null,
    double? Latitude = null,
    double? Longitude = null,
    DateTimeOffset? TrackPoint = null
);

/// <summary>How one packet travelled, for drawing and words (docs/features/F1.21-rf-paths.md, behavior 2).</summary>
/// <param name="Name">The station that sent it.</param>
/// <param name="Kind">Heard directly, digipeated, from the internet, or not said by the path.</param>
/// <param name="Latitude">Where it was sent from, when known.</param>
/// <param name="Longitude">Where it was sent from, when known.</param>
/// <param name="Hops">
/// The stations it went through in order: each digipeater that named itself, then the IGate that put it on APRS-IS.
/// Empty for a packet that never went over RF.
/// </param>
public sealed record PacketRoute(
    string Name,
    ArrivalKind Kind,
    double? Latitude,
    double? Longitude,
    IReadOnlyList<RouteHop> Hops
);

/// <summary>One station a packet went through.</summary>
/// <param name="Name">Its callsign.</param>
/// <param name="IsGateway">The IGate that heard it last and put it on APRS-IS; otherwise a digipeater.</param>
/// <param name="Latitude">Its last known position; null when it hasn't sent one.</param>
/// <param name="Longitude">Its last known position; null when it hasn't sent one.</param>
/// <param name="DistanceKm">How far it is from the last known point before it; null when either is unknown.</param>
public sealed record RouteHop(
    string Name,
    bool IsGateway,
    double? Latitude = null,
    double? Longitude = null,
    double? DistanceKm = null
);

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
