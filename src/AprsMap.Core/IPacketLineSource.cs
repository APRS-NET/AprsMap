// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>One connection's worth of raw TNC2 packet lines. The engine opens a new source after each failure.</summary>
public interface IPacketLineSource : IAsyncDisposable
{
    /// <summary>Connects and logs in. Returns the server name reported at login.</summary>
    Task<string> ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Reads packet lines until the connection ends.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);
}

/// <summary>Creates a fresh <see cref="IPacketLineSource"/> for each connection attempt.</summary>
public interface IPacketLineSourceFactory
{
    /// <summary>Creates a source that logs in with <paramref name="profile"/>.</summary>
    IPacketLineSource Create(StationProfile profile);
}
