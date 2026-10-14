// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// Everything a UI can ask of the engine. The desktop app calls it in-process; the browser app calls the same
/// interface over SignalR through the gateway, so both UIs stay identical (docs/architecture.md).
/// </summary>
public interface IEngineService
{
    /// <summary>A snapshot of the engine's state.</summary>
    Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams packets as the engine receives them, starting with the most recent ones it still holds. For the raw
    /// packet view; the map uses <see cref="WatchStationsAsync"/>, which is far smaller.
    /// </summary>
    IAsyncEnumerable<PacketView> WatchPacketsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the map's stations. The first batches hold every station the engine knows (a late joiner catches up
    /// without a separate snapshot call); after that, each batch holds what changed since the previous one, at most
    /// every few hundred milliseconds, with only the latest state per station.
    /// </summary>
    IAsyncEnumerable<StationBatch> WatchStationsAsync(CancellationToken cancellationToken = default);
}
