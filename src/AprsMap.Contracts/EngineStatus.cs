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
/// <param name="Stations">How many stations, objects and items the store holds, for the View filter's chip (F1.9).</param>
/// <param name="PacketHistory">
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
    int UnreadMessages = 0
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
public sealed record TransmitStatus(
    bool On,
    EngineError? Blocked = null,
    DateTimeOffset? LastSent = null,
    bool ReceiveOnly = false,
    NextBeacon? NextBeacon = null,
    EngineError? BeaconHeld = null
);

/// <summary>One source's connection, for its status pill (F1.25 behavior 11).</summary>
/// <param name="Id">The source's id.</param>
/// <param name="Name">The source's name.</param>
/// <param name="Kind">What it is.</param>
/// <param name="State">Its connection state, as F1.1 defines it.</param>
/// <param name="Server">What it is connected to (the APRS-IS server's name), when connected.</param>
/// <param name="Error">Why it is reconnecting or stopped.</param>
/// <param name="Enabled">Whether it's switched on (F3.1 behavior 6); a source switched off is listed, disconnected.</param>
/// <param name="RetryAt">When the next attempt is, while it is reconnecting.</param>
/// <param name="TransmitEnabled">The source is TX enabled (docs/features/F2.1-own-station.md, behavior 5).</param>
/// <param name="TransmitBlocked">
/// Why the station couldn't send on it now (a <c>transmit.*</c> code), or null when it can; null too when it isn't TX
/// enabled.
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
    bool TransmitEnabled = false,
    EngineError? TransmitBlocked = null
)
{
    /// <summary>Whether the station can send on this source now.</summary>
    public bool CanTransmit => TransmitEnabled && TransmitBlocked is null;
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
