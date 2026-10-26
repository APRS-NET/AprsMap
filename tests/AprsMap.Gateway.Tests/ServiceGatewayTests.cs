// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AprsMap.Gateway.Tests;

/// <summary>An operator gateway that is the Linux service, as its unit says (F1.40 behavior 1).</summary>
public sealed class ServiceGatewayFactory : OperatorGatewayFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Engine:Updates:Kind", "Service").UseSetting("Engine:Updates:Check", "false");
    }
}

/// <summary>Restart and Update and restart (docs/features/F1.40-gateway-service-updates.md, acceptance tests 1, 2).</summary>
public sealed class ServiceGatewayTests(ServiceGatewayFactory gateway) : IClassFixture<ServiceGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyTheOperatorMayRestartOrUpdateTheService()
    {
        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);

        var restart = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.RestartGatewayAsync(Ct));
        var update = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.InstallUpdateAsync("9.9.9", Ct));

        Assert.Equal(EngineErrorCodes.OperatorOnly, restart.EngineError.Code);
        Assert.Equal(EngineErrorCodes.OperatorOnly, update.EngineError.Code);
    }

    [Fact]
    public async Task TheOperatorsUpdateMustBeTheOneOffered()
    {
        await using var op = gateway.CreateRemote(await gateway.SignInAsync(Ct));
        await op.StartAsync(Ct);

        Assert.True((await op.GetUpdateStatusAsync(Ct)).CanInstall);
        var refused = await Assert.ThrowsAsync<EngineOperationException>(() => op.InstallUpdateAsync("9.9.9", Ct));
        Assert.Equal(EngineErrorCodes.UpdateNotOffered, refused.EngineError.Code);
    }

    [Fact]
    public async Task RestartStopsTheGatewayCleanlyForSystemdToStartAgain()
    {
        // A gateway of its own: this one stops.
        await using var own = new ServiceGatewayFactory();
        await using var op = own.CreateRemote(await own.SignInAsync(Ct));
        await op.StartAsync(Ct);
        var stopping = own.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        await op.RestartGatewayAsync(Ct);

        for (var i = 0; i < 60 && !stopping.IsCancellationRequested; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
        }
        Assert.True(stopping.IsCancellationRequested);
        Assert.Contains(own.Logs.Messages, m => m.Contains("asked the gateway to restart", StringComparison.Ordinal));
    }
}

/// <summary>A gateway that isn't the service (the container) has neither (F1.40 behavior 1).</summary>
public sealed class ContainerGatewayUpdateTests(OperatorGatewayFactory gateway) : IClassFixture<OperatorGatewayFactory>
{
    [Fact]
    public async Task AContainerGatewayNeitherRestartsNorUpdatesItself()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var op = gateway.CreateRemote(await gateway.SignInAsync(ct));
        await op.StartAsync(ct);

        Assert.False((await op.GetUpdateStatusAsync(ct)).CanInstall);
        var restart = await Assert.ThrowsAsync<EngineAccessException>(() => op.RestartGatewayAsync(ct));
        var update = await Assert.ThrowsAsync<EngineAccessException>(() => op.InstallUpdateAsync("9.9.9", ct));
        Assert.Equal(EngineErrorCodes.GatewayNotService, restart.EngineError.Code);
        Assert.Equal(EngineErrorCodes.GatewayNotService, update.EngineError.Code);
    }
}
