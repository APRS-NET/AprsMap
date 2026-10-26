// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>State of the engine's APRS-IS connection.</summary>
public enum ConnectionState
{
    /// <summary>Not connected and not trying to.</summary>
    Disconnected,

    /// <summary>Connecting or logging in.</summary>
    Connecting,

    /// <summary>Logged in and receiving.</summary>
    Connected,

    /// <summary>
    /// The connection ended or couldn't be made, and the next attempt is waiting until
    /// <see cref="EngineStatus.RetryAt"/>; <see cref="EngineStatus.ConnectionError"/> says why.
    /// </summary>
    Reconnecting,

    /// <summary>No station profile yet: first-run setup has to supply a callsign before the engine connects.</summary>
    NotConfigured,

    /// <summary>
    /// The server refused the login (<see cref="EngineStatus.ConnectionError"/> has its reason). The engine doesn't retry
    /// by itself: it waits for a profile change or <see cref="IEngineService.ReconnectAsync"/>.
    /// </summary>
    Refused,
}

/// <summary>A snapshot of the engine.</summary>
/// <param name="Connection">APRS-IS connection state.</param>
/// <param name="Server">
/// When connected, the name the server reported at login; while connecting or reconnecting, the server being tried
/// (<c>host:port</c>).
/// </param>
/// <param name="PacketsReceived">Packets received since the engine started.</param>
/// <param name="Version">The engine's build version.</param>
/// <param name="ProfileId">The active <see cref="StationProfile.Id"/>, so a UI notices when another one switches it.</param>
/// <param name="Callsign">The active profile's callsign, when there is one.</param>
/// <param name="AprsNetVersion">The APRS.NET version the engine runs on, for About.</param>
/// <param name="Verified">Whether the server accepted the login's passcode (when connected).</param>
/// <param name="RetryAt">When the next attempt starts, while <see cref="ConnectionState.Reconnecting"/>.</param>
/// <param name="Reconnects">How many times the connection has been re-established since it first logged in.</param>
/// <param name="ConnectionError">
/// Why the connection is <see cref="ConnectionState.Reconnecting"/> or <see cref="ConnectionState.Refused"/>: one of the
/// <c>aprs-is.*</c> codes in <see cref="EngineErrorCodes"/>.
/// </param>
/// <param name="Viewers">How many browsers are connected to the gateway (F1.14 behavior 11); null on desktop.</param>
/// <param name="Filter">The APRS-IS filter in use (F1.2 behavior 7); null before the first login.</param>
/// <param name="FilterAreas">The filter's range parts as circles, for the map (F1.2 behavior 6).</param>
/// <param name="Sources">
/// Each source the active profile uses, with its own connection state (F1.25 behavior 11). The top-level fields are the
/// APRS-IS source's.
/// </param>
/// <param name="Toggles">The station's quick toggles (F1.15), for the status bar's markers.</param>
/// <param name="Transmit">Whether the station transmits, and what holds it (docs/features/F2.1-own-station.md).</param>
/// <param name="UnreadMessages">Unread messages to the active profile (docs/features/F2.4-messaging.md, behavior 4).</param>
/// <param name="CapturedToday">
/// While packet capture is on, how many packets it recorded today (docs/features/F1.27-packet-capture.md, behavior 6);
/// null while it's off.
/// </param>
/// <param name="Stations">How many stations, objects and items the store holds, for the View filter's chip (F1.9).</param>
/// <param name="PacketHistory">
/// <param name="Title">The gateway's page title (<c>Gateway:Title</c>, F1.26 behavior 10); null on the desktop.</param>
/// <param name="Gps">The active profile's GPS and its fix (docs/features/F3.6-gps.md); null without one.</param>
/// <param name="IGate">The receive-only IGate (docs/features/F3.4-rx-igate.md); null when the profile isn't one.</param>
/// <param name="DeviceList">The device list in use (docs/features/F4.16-device-list.md), so a change shows without asking.</param>
/// Counts up each time the held packets are cleared (F1.19 behavior 3), so every viewer empties its packet list.
/// </param>
public sealed record EngineStatus(
    ConnectionState Connection,
    string? Server,
    long PacketsReceived,
    string Version,
    string? ProfileId = null,
    string? Callsign = null,
    string? AprsNetVersion = null,
    bool Verified = false,
    DateTimeOffset? RetryAt = null,
    int Reconnects = 0,
    EngineError? ConnectionError = null,
    int? Viewers = null,
    string? Filter = null,
    IReadOnlyList<FilterArea>? FilterAreas = null,
    IReadOnlyList<SourceStatus>? Sources = null,
    int PacketHistory = 0,
    int Stations = 0,
    StationToggles? Toggles = null,
    TransmitStatus? Transmit = null,
    int UnreadMessages = 0,
    int? CapturedToday = null,
    string? Title = null,
    GpsStatus? Gps = null,
    IGateStatus? IGate = null,
    DeviceListStatus? DeviceList = null
);

/// <summary>
/// The receive-only IGate (docs/features/F3.4-rx-igate.md, behavior 6): what it has gated to APRS-IS and what it hasn't
/// since the engine started, and why it's held, if it is.
/// </summary>
/// <param name="Gated">Packets heard on RF and sent to APRS-IS.</param>
/// <param name="Refused">Packets heard on RF the IGate rules kept off APRS-IS (from the internet, NOGATE, duplicates…).</param>
/// <param name="Held">Why nothing is gated now (no verified APRS-IS login, Transmit off…); null when it gates.</param>
public sealed record IGateStatus(long Gated, long Refused, EngineError? Held = null);

/// <summary>How much a GPS knows (docs/features/F3.6-gps.md, behavior 4).</summary>
public enum GpsFixKind
{
    /// <summary>No fix yet, or none now.</summary>
    NoFix,

    /// <summary>Position without altitude.</summary>
    Fix2D,

    /// <summary>Position with altitude.</summary>
    Fix3D,
}

/// <summary>The active profile's GPS and its latest fix (docs/features/F3.6-gps.md, behaviors 4, 5, 7).</summary>
/// <param name="SourceId">The GPS source.</param>
/// <param name="Fix">How much it knows.</param>
/// <param name="At">When the fix came; null before the first.</param>
/// <param name="IsFresh">Whether the fix is recent enough for the station to be where it says (60 s).</param>
/// <param name="Latitude">Where it is, with a fix.</param>
/// <param name="Longitude">Where it is, with a fix.</param>
/// <param name="AltitudeMeters">Height above mean sea level, with a 3D fix.</param>
/// <param name="SpeedKnots">Speed over the ground.</param>
/// <param name="Course">Course over the ground, in degrees true.</param>
/// <param name="Satellites">Satellites used in the fix, where the GPS says.</param>
/// <param name="Hdop">Horizontal dilution of precision, where the GPS says.</param>
/// <param name="Accuracy">How good the fix is (docs/features/F3.12-gps-status.md, behavior 3), where the GPS says.</param>
/// <param name="Sky">The satellites in view (F3.12 behaviors 4, 5); null before the GPS has said.</param>
public sealed record GpsStatus(
    string SourceId,
    GpsFixKind Fix,
    DateTimeOffset? At = null,
    bool IsFresh = false,
    double? Latitude = null,
    double? Longitude = null,
    double? AltitudeMeters = null,
    double? SpeedKnots = null,
    double? Course = null,
    int? Satellites = null,
    double? Hdop = null,
    GpsAccuracy? Accuracy = null,
    IReadOnlyList<GpsSkySatellite>? Sky = null
)
{
    /// <summary>Whether the station is where the GPS says: a fresh fix with a position.</summary>
    public bool HasPosition => IsFresh && Fix != GpsFixKind.NoFix && Latitude is not null && Longitude is not null;
}

/// <summary>A satellite system (docs/features/F3.12-gps-status.md, behavior 4).</summary>
public enum GnssConstellation
{
    /// <summary>Not known.</summary>
    Unknown,

    /// <summary>GPS (United States).</summary>
    Gps,

    /// <summary>GLONASS (Russia).</summary>
    Glonass,

    /// <summary>Galileo (Europe).</summary>
    Galileo,

    /// <summary>BeiDou (China).</summary>
    BeiDou,

    /// <summary>QZSS (Japan).</summary>
    Qzss,

    /// <summary>NavIC (India).</summary>
    NavIC,

    /// <summary>An augmentation satellite (WAAS, EGNOS…).</summary>
    Sbas,
}

/// <summary>How good a GPS's fix is (docs/features/F3.12-gps-status.md, behavior 3); each value null when not given.</summary>
/// <param name="SatellitesUsed">Satellites the fix uses.</param>
/// <param name="SatellitesInView">Satellites the GPS can see, used or not.</param>
/// <param name="Pdop">Position (3D) dilution of precision: lower is better.</param>
/// <param name="Hdop">Horizontal dilution of precision.</param>
/// <param name="Vdop">Vertical dilution of precision.</param>
/// <param name="HorizontalErrorMeters">The GPS's estimate of its horizontal error.</param>
/// <param name="VerticalErrorMeters">The GPS's estimate of its altitude error.</param>
/// <param name="SpeedErrorKnots">The GPS's estimate of its speed error (gpsd only).</param>
/// <param name="Tdop">Time dilution of precision (gpsd only).</param>
/// <param name="Gdop">Geometric dilution of precision, position and time together (gpsd only).</param>
/// <param name="LatitudeErrorMeters">The GPS's estimate of its error in latitude.</param>
/// <param name="LongitudeErrorMeters">The GPS's estimate of its error in longitude.</param>
public sealed record GpsAccuracy(
    int? SatellitesUsed = null,
    int? SatellitesInView = null,
    double? Pdop = null,
    double? Hdop = null,
    double? Vdop = null,
    double? HorizontalErrorMeters = null,
    double? VerticalErrorMeters = null,
    double? SpeedErrorKnots = null,
    double? Tdop = null,
    double? Gdop = null,
    double? LatitudeErrorMeters = null,
    double? LongitudeErrorMeters = null
);

/// <summary>A satellite a GPS can see (docs/features/F3.12-gps-status.md, behaviors 4, 5).</summary>
/// <param name="System">Its system.</param>
/// <param name="Id">Its number, as the GPS gives it.</param>
/// <param name="SignalId">Which of its signals, when the GPS says.</param>
/// <param name="ElevationDegrees">Above the horizon, 0–90; null when not known.</param>
/// <param name="AzimuthDegrees">From true north, 0–359; null when not known.</param>
/// <param name="SnrDbHz">Signal-to-noise ratio; null when it's seen but not tracked.</param>
/// <param name="Used">Whether the fix uses it.</param>
public sealed record GpsSkySatellite(
    GnssConstellation System,
    int Id,
    int? SignalId,
    double? ElevationDegrees,
    double? AzimuthDegrees,
    double? SnrDbHz,
    bool Used
);

/// <summary>Transmitting, for the status bar (docs/features/F2.1-own-station.md, behaviors 4–13).</summary>
/// <param name="On">Whether the Transmit switch is on.</param>
/// <param name="Blocked">
/// Why the station couldn't send now: one of the <c>transmit.*</c> codes in <see cref="EngineErrorCodes"/> (with the
/// switch off, why it couldn't be turned on), or null when it can.
/// </param>
/// <param name="LastSent">When the station last sent a packet.</param>
/// <param name="ReceiveOnly">The engine was started receive-only (<c>Engine:ReceiveOnly</c>), which nothing here can change.</param>
/// <param name="NextBeacon">The next beacon due (docs/features/F2.2-beacons.md, behavior 10), or null when none is on.</param>
/// <param name="BeaconHeld">
/// Why beacons are waiting (behaviors 4, 5): a <c>transmit.*</c> code, <c>transmit.position-unconfirmed</c> included;
/// null when they go out as due.
/// </param>
/// <param name="LastPosition">
/// How the last position sent got out (docs/features/F3.3-rf-paths.md, behavior 4), or null before one was sent.
/// </param>
/// <param name="SeenAt">Where others see the station (docs/features/F2.3-seen-as.md); null without a fresh GPS fix.</param>
public sealed record TransmitStatus(
    bool On,
    EngineError? Blocked = null,
    DateTimeOffset? LastSent = null,
    bool ReceiveOnly = false,
    NextBeacon? NextBeacon = null,
    EngineError? BeaconHeld = null,
    LastPositionHeard? LastPosition = null,
    SeenPosition? SeenAt = null
);

/// <summary>
/// Where other stations see this one now (docs/features/F2.3-seen-as.md): its last position sent, moved on along its
/// course and speed for the time since, as their maps would.
/// </summary>
/// <param name="Latitude">Where they see it.</param>
/// <param name="Longitude">Where they see it.</param>
/// <param name="DistanceKm">How far that is from where the GPS says the station is.</param>
/// <param name="SentAt">When the position they have was sent.</param>
public sealed record SeenPosition(double Latitude, double Longitude, double DistanceKm, DateTimeOffset SentAt);

/// <summary>One source's connection, for its status pill (F1.25 behavior 11).</summary>
/// <param name="Id">The source's id.</param>
/// <param name="Name">The source's name.</param>
/// <param name="Kind">What it is.</param>
/// <param name="State">Its connection state, as F1.1 defines it.</param>
/// <param name="Server">What it is connected to (the APRS-IS server's name), when connected.</param>
/// <param name="Error">Why it is reconnecting or stopped.</param>
/// <param name="Enabled">Whether it's switched on (F3.1 behavior 6); a source switched off is listed, disconnected.</param>
/// <param name="RetryAt">When the next attempt is, while it is reconnecting.</param>
/// <param name="MaySend">
/// The station may send on it: it isn't RX only (docs/features/F2.1-own-station.md, behavior 5).
/// </param>
/// <param name="TransmitBlocked">
/// Why the station couldn't send on it now (a <c>transmit.*</c> code naming the source), or null when it can; null too
/// when it's RX only.
/// </param>
/// <param name="Manual">
/// Whether a serial TNC is in manual mode, taking typed commands (docs/features/F3.2-built-in-tncs.md, behavior 9).
/// </param>
/// <param name="Warning">
/// Something wrong while it is connected, such as a TNC that isn't in KISS mode (a <c>kiss.*</c> code;
/// docs/features/F3.1-kiss-sources.md, behavior 13), or null.
/// </param>
public sealed record SourceStatus(
    string Id,
    string Name,
    SourceKind Kind,
    ConnectionState State,
    string? Server = null,
    EngineError? Error = null,
    bool Enabled = true,
    DateTimeOffset? RetryAt = null,
    bool MaySend = false,
    EngineError? TransmitBlocked = null,
    EngineError? Warning = null,
    bool Manual = false
)
{
    /// <summary>Whether the station can send on this source now.</summary>
    public bool CanTransmit => MaySend && TransmitBlocked is null;
}

/// <summary>What a Test send did (docs/features/F2.1-own-station.md, behavior 9).</summary>
/// <param name="Sent">The packet sent.</param>
/// <param name="HeardThrough">
/// Who it was heard back through within 30 seconds: the digipeater that repeated it on RF, or the IGate that gated it to
/// APRS-IS; null when it wasn't heard back.
/// </param>
/// <param name="ByIgate">Whether <paramref name="HeardThrough"/> is an IGate rather than a digipeater.</param>
public sealed record TestSendResult(PacketView Sent, string? HeardThrough = null, bool ByIgate = false);

/// <summary>One range part of the APRS-IS filter, drawn on the map as a circle (F1.2 behavior 6).</summary>
/// <param name="Latitude">The centre.</param>
/// <param name="Longitude">The centre.</param>
/// <param name="RadiusKm">The radius in km.</param>
public sealed record FilterArea(double Latitude, double Longitude, double RadiusKm);

/// <summary>What Clear empties (docs/features/F1.19-housekeeping.md, behavior 1).</summary>
public enum ClearScope
{
    /// <summary>Every station, object and item, with their tracks.</summary>
    Stations,

    /// <summary>Only the stations past the stale time.</summary>
    StaleStations,

    /// <summary>Every track, keeping each station's latest position.</summary>
    Tracks,

    /// <summary>The held packets.</summary>
    Packets,

    /// <summary>Every station's telemetry reports (F1.11).</summary>
    Telemetry,
}

/// <summary>How much each <see cref="ClearScope"/> would clear now, for the counts beside each choice.</summary>
/// <param name="Stations">Stations, objects and items held.</param>
/// <param name="StaleStations">Those past the stale time.</param>
/// <param name="Tracks">Stations with a track of two or more positions.</param>
/// <param name="Packets">Packets held for the packet list.</param>
/// <param name="Telemetry">Stations with telemetry reports held.</param>
public sealed record ClearCounts(int Stations, int StaleStations, int Tracks, int Packets, int Telemetry = 0);
