// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// One profile's APRS-IS session. Only the first login can fail; after it the source reconnects by itself until it is
/// disposed, and reports each drop and new login through the callback given to <see cref="StartAsync"/>.
/// </summary>
public interface IPacketSource : IAsyncDisposable
{
    /// <summary>Connects and logs in for the first time.</summary>
    /// <param name="stateChanged">
    /// Called on the source's own task after the first login: <see cref="ConnectionState.Reconnecting"/> when the
    /// session drops or a retry fails, <see cref="ConnectionState.Connecting"/> while it retries,
    /// <see cref="ConnectionState.Connected"/> after each new login, and <see cref="ConnectionState.Refused"/> when the
    /// server refuses a new login (the engine then ends the session). Keep it short.
    /// </param>
    /// <param name="cancellationToken">Gives up the first login.</param>
    /// <exception cref="AprsNet.AprsIsTcpClient.LoginRejectedException">The server refused the login.</exception>
    /// <exception cref="Exception">The server couldn't be reached or didn't answer the login.</exception>
    Task<PacketSourceLogin> StartAsync(Action<PacketSourceEvent> stateChanged, CancellationToken cancellationToken);

    /// <summary>Reads parsed packets from every connection in turn, until the source is disposed.</summary>
    IAsyncEnumerable<AprsPacket> ReadPacketsAsync(CancellationToken cancellationToken);

    /// <summary>Sends <paramref name="profile"/>'s filter now and on every later login, without logging in again.</summary>
    ValueTask SetFilterAsync(StationProfile profile, CancellationToken cancellationToken);
}

/// <summary>A server's answer to a login.</summary>
/// <param name="Server">The name it reported, or empty.</param>
/// <param name="Verified">Whether it accepted the passcode.</param>
public sealed record PacketSourceLogin(string Server, bool Verified);

/// <summary>A change in a <see cref="IPacketSource"/>'s session after its first login.</summary>
/// <param name="State">The new state.</param>
/// <param name="Login">The new login, when <paramref name="State"/> is <see cref="ConnectionState.Connected"/>.</param>
/// <param name="RetryIn">The wait before the next attempt, when it is <see cref="ConnectionState.Reconnecting"/>.</param>
/// <param name="Error">Why it is <see cref="ConnectionState.Reconnecting"/> or <see cref="ConnectionState.Refused"/>.</param>
public sealed record PacketSourceEvent(
    ConnectionState State,
    PacketSourceLogin? Login = null,
    TimeSpan? RetryIn = null,
    EngineError? Error = null
);

/// <summary>Creates a <see cref="IPacketSource"/> for each profile the engine logs in with.</summary>
public interface IPacketSourceFactory
{
    /// <summary>Creates a source that logs in with <paramref name="profile"/>.</summary>
    IPacketSource Create(StationProfile profile);
}
