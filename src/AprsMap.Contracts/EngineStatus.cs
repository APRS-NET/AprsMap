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
    string? Filter = null
);
