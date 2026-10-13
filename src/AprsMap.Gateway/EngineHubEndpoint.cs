// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.CompilerServices;
using AprsMap.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace AprsMap.Gateway;

/// <summary>Exposes the in-process <see cref="IEngineService"/> to remote UIs. Method names come from <see cref="EngineHub"/>.</summary>
internal sealed class EngineHubEndpoint(IEngineService engine) : Hub
{
    [HubMethodName(EngineHub.GetStatus)]
    public Task<EngineStatus> GetStatus() => engine.GetStatusAsync(Context.ConnectionAborted);

    [HubMethodName(EngineHub.WatchPackets)]
    public async IAsyncEnumerable<PacketView> WatchPackets([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var packet in engine.WatchPacketsAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return packet;
        }
    }
}
