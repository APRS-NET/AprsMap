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

    /// <summary>Streams packets as the engine receives them, starting with the most recent ones it still holds.</summary>
    IAsyncEnumerable<PacketView> WatchPacketsAsync(CancellationToken cancellationToken = default);
}
