// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using Microsoft.AspNetCore.Hosting;

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
