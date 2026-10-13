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
}

/// <summary>A snapshot of the engine.</summary>
/// <param name="Connection">APRS-IS connection state.</param>
/// <param name="Server">The server name reported at login, when connected.</param>
/// <param name="PacketsReceived">Packets received since the engine started.</param>
/// <param name="Version">The engine's build version.</param>
public sealed record EngineStatus(ConnectionState Connection, string? Server, long PacketsReceived, string Version);
