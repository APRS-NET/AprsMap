// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AprsMap.Gateway.Tests;

/// <summary>A gateway with small limits: 3 viewers in all, 2 from one address, 1 call a second with bursts of 3.</summary>
public sealed class LimitedGatewayFactory : GatewayFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder
            .UseSetting("Gateway:MaxViewers", "3")
            .UseSetting("Gateway:MaxViewersPerAddress", "2")
            .UseSetting("Gateway:CallsPerSecond", "1")
            .UseSetting("Gateway:CallBurst", "3");
    }
}

/// <summary>A gateway whose filter follows its viewers' maps (F1.2 behavior 9).</summary>
public sealed class FollowingGatewayFactory : GatewayFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Gateway:FollowViewers", "true");
    }
}

public sealed class FollowViewersTests(FollowingGatewayFactory factory) : IClassFixture<FollowingGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachViewersMapAddsARangeCappedAt500Km()
    {
        var engine = factory.Services.GetRequiredService<Core.Engine>();
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);
        await remote.GetSettingsAsync(Ct);

        await remote.SetMapViewAsync(new MapViewReport(35, -90, 900), Ct);

        Assert.Contains("r/35/-90/500", engine.PreviewFilter(), StringComparison.Ordinal);
    }
}

public sealed class ViewerLimitTests(LimitedGatewayFactory factory) : IClassFixture<LimitedGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ViewersOverTheLimitFromOneAddressAreRefusedAsBusyAndCountedWhenIn()
    {
        // The test server has no client address, so every viewer here comes from the same one.
        await using var first = factory.CreateRemote();
        await using var second = factory.CreateRemote();
        await using var third = factory.CreateRemote();
        await first.StartAsync(Ct);
        await second.StartAsync(Ct);

        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => third.StartAsync(Ct));
        Assert.Equal(EngineErrorCodes.GatewayBusy, refused.EngineError.Code);
        Assert.Equal(2, (await first.GetStatusAsync(Ct)).Viewers);

        await second.DisposeAsync();
        await WaitUntilAsync(async () => (await first.GetStatusAsync(Ct)).Viewers == 1);
        await using var again = factory.CreateRemote();
        await again.StartAsync(Ct); // a place came free
    }

    [Fact]
    public async Task CallsOverTheRateFailAsRateLimited()
    {
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);

        var limited = 0;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                await remote.GetStationAsync("N0NOBODY", Ct);
            }
            catch (EngineAccessException ex) when (ex.EngineError.Code == EngineErrorCodes.GatewayRateLimited)
            {
                limited++;
            }
        }

        Assert.InRange(limited, 5, 7); // a burst of 3, then about 1 a second
    }

    [Theory]
    [InlineData("")]
    [InlineData("?contract=0")]
    public async Task AnAppOnAnotherContractVersionIsToldToReload(string query)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            EngineHub.Path + "/negotiate" + query + (query.Length == 0 ? "?" : "&") + "negotiateVersion=1",
            null,
            Ct
        );

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(EngineHub.TryDecodeError(await response.Content.ReadAsStringAsync(Ct), out var error));
        Assert.Equal(EngineErrorCodes.GatewayOutdated, error.Code);
    }

    [Fact]
    public async Task OnceTheGatewayRefusesTheVersionEveryCallSaysSo()
    {
        var contract = new ContractCheck();
        using var invoker = new HttpMessageInvoker(contract.Wrap(new RefuseNegotiate()));
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://gw/hubs/engine/negotiate"), Ct);
        Assert.True(contract.IsOutdated);

        // A client whose connection never started fails its calls; with the check tripped they say why.
        var connection = new HubConnectionBuilder().WithUrl("http://gw/hubs/engine").Build();
        await using var remote = new RemoteEngineService(connection, contract);
        var outdated = await Assert.ThrowsAsync<EngineAccessException>(() => remote.GetStatusAsync(Ct));
        Assert.Equal(EngineErrorCodes.GatewayOutdated, outdated.EngineError.Code);
    }

    [Fact]
    public async Task WithoutFollowViewersABrowsersMapViewChangesNothingButItsFollowedStationCounts()
    {
        var engine = factory.Services.GetRequiredService<Core.Engine>();
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);
        await remote.GetSettingsAsync(Ct); // loads the settings
        var before = engine.PreviewFilter();

        await remote.SetMapViewAsync(new MapViewReport(35, -90, 80, "W1AW-9"), Ct);

        var after = engine.PreviewFilter()!;
        Assert.DoesNotContain("r/35/-90", after, StringComparison.Ordinal);
        Assert.EndsWith("f/W1AW-9/50", after, StringComparison.Ordinal);
        await remote.DisposeAsync();
        await WaitUntilAsync(() => Task.FromResult(engine.PreviewFilter() == before)); // it left: no longer counts
    }

    [Fact]
    public async Task ABrowsersWatchedStationsCountUpToThree()
    {
        var engine = factory.Services.GetRequiredService<Core.Engine>();
        await using var remote = factory.CreateRemote();
        await remote.StartAsync(Ct);
        await remote.GetSettingsAsync(Ct);

        await remote.SetMapViewAsync(
            new MapViewReport(null, null, 0, Watched: ["N0AAA", "N0BBB", "N0CCC", "N0DDD", "N0EEE"]),
            Ct
        );

        var filter = engine.PreviewFilter()!;
        Assert.EndsWith("f/N0AAA/50 f/N0BBB/50 f/N0CCC/50", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("N0DDD", filter, StringComparison.Ordinal);
    }

    private sealed class RefuseNegotiate : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Conflict));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (EngineAccessException)
            {
                // rate-limited while polling: wait and ask again
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
        }
        Assert.Fail("Condition was not met in time.");
    }
}
