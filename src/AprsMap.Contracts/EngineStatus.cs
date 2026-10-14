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

    /// <summary>The last attempt failed; the engine will retry.</summary>
    Faulted,

    /// <summary>No station profile yet: first-run setup has to supply a callsign before the engine connects.</summary>
    NotConfigured,
}

/// <summary>A snapshot of the engine.</summary>
/// <param name="Connection">APRS-IS connection state.</param>
/// <param name="Server">The server name reported at login, when connected.</param>
/// <param name="PacketsReceived">Packets received since the engine started.</param>
/// <param name="Version">The engine's build version.</param>
/// <param name="ProfileId">The active <see cref="StationProfile.Id"/>, so a UI notices when another one switches it.</param>
/// <param name="Callsign">The active profile's callsign, when there is one.</param>
public sealed record EngineStatus(
    ConnectionState Connection,
    string? Server,
    long PacketsReceived,
    string Version,
    string? ProfileId = null,
    string? Callsign = null
);
