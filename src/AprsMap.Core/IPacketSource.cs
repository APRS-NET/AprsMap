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
    /// <summary>Connects and logs in for the first time. Returns the server name reported at login.</summary>
    /// <param name="stateChanged">
    /// Called on the source's own task with the new state: <see cref="ConnectionState.Connected"/> with the server name
    /// after each login, <see cref="ConnectionState.Faulted"/> when the session drops and a retry is waiting, and
    /// <see cref="ConnectionState.Connecting"/> while it retries. Keep it short.
    /// </param>
    /// <param name="cancellationToken">Gives up the first login.</param>
    Task<string> StartAsync(Action<ConnectionState, string?> stateChanged, CancellationToken cancellationToken);

    /// <summary>Reads parsed packets from every connection in turn, until the source is disposed.</summary>
    IAsyncEnumerable<AprsPacket> ReadPacketsAsync(CancellationToken cancellationToken);
}

/// <summary>Creates a <see cref="IPacketSource"/> for each profile the engine logs in with.</summary>
public interface IPacketSourceFactory
{
    /// <summary>Creates a source that logs in with <paramref name="profile"/>.</summary>
    IPacketSource Create(StationProfile profile);
}
