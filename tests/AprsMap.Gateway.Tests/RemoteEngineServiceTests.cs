// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using Microsoft.AspNetCore.SignalR.Client;

namespace AprsMap.Gateway.Tests;

public class RemoteEngineServiceTests
{
    [Fact]
    public async Task CreateBuildsADisconnectedClientForTheGateway()
    {
        await using var remote = RemoteEngineService.Create(new Uri("http://localhost:8080/"));

        Assert.Equal(HubConnectionState.Disconnected, remote.State);
    }

    [Fact]
    public void GuardsAgainstNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RemoteEngineService.Create(null!));
        Assert.Throws<ArgumentNullException>(() => new RemoteEngineService(null!));
    }
}
