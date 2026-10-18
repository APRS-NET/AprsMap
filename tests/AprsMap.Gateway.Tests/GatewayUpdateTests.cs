// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.Gateway.Tests;

/// <summary>Only a gateway's operator hears about updates (docs/features/F4.15-updates.md, acceptance test 6).</summary>
public sealed class GatewayUpdateTests(OperatorGatewayFactory gateway) : IClassFixture<OperatorGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AViewerIsRefusedAndTheOperatorIsAnsweredAsAGateway()
    {
        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);
        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.GetUpdateStatusAsync(Ct));
        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);
        await Assert.ThrowsAsync<EngineAccessException>(() => viewer.CheckForUpdateAsync(Ct));

        await using var signedIn = gateway.CreateRemote(await gateway.SignInAsync(Ct));
        await signedIn.StartAsync(Ct);
        var status = await signedIn.GetUpdateStatusAsync(Ct);
        Assert.Null(status.Update); // nothing checked: the test gateway's engine doesn't run
    }
}
