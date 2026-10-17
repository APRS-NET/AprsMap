// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using Microsoft.AspNetCore.SignalR.Client;

namespace AprsMap.Gateway.Tests;

public class RemoteEngineServiceTests
{
    [Theory]
    [InlineData("http://host:8080/", "http://host:8080/hubs/engine?contract=2")]
    [InlineData("https://host/aprs/", "https://host/aprs/hubs/engine?contract=2")]
    [InlineData("https://host/aprs", "https://host/aprs/hubs/engine?contract=2")]
    public void TheHubIsFoundUnderTheGatewaysOwnPathWithTheContractVersion(string gateway, string hub) =>
        Assert.Equal(new Uri(hub), RemoteEngineService.HubUri(new Uri(gateway)));

    [Fact]
    public async Task CreateBuildsADisconnectedClientForTheGateway()
    {
        await using var remote = RemoteEngineService.Create(new Uri("http://localhost:8080/"));

        Assert.Equal(HubConnectionState.Disconnected, remote.State);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(3, 10)]
    [InlineData(4, 30)]
    [InlineData(1000, 30)]
    public void ReconnectingNeverGivesUp(long previousRetries, int expectedSeconds)
    {
        var policy = new RemoteEngineService.KeepTryingPolicy();

        var delay = policy.NextRetryDelay(new RetryContext { PreviousRetryCount = previousRetries });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void GuardsAgainstNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RemoteEngineService.Create(null!));
        Assert.Throws<ArgumentNullException>(() => new RemoteEngineService(null!));
    }
}
