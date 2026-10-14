// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace AprsMap.Gateway;

/// <summary>Exposes the in-process <see cref="IEngineService"/> to remote UIs. Method names come from <see cref="EngineHub"/>.</summary>
internal sealed class EngineHubEndpoint(IEngineService engine) : Hub
{
    [HubMethodName(EngineHub.GetStatus)]
    public Task<EngineStatus> GetStatus() => engine.GetStatusAsync(Context.ConnectionAborted);

    [HubMethodName(EngineHub.WatchPackets)]
    public IAsyncEnumerable<PacketView> WatchPackets(CancellationToken cancellationToken) =>
        engine.WatchPacketsAsync(cancellationToken);

    [HubMethodName(EngineHub.WatchStations)]
    public IAsyncEnumerable<StationBatch> WatchStations(CancellationToken cancellationToken) =>
        engine.WatchStationsAsync(cancellationToken);
}
