// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.Gateway.Tests;

/// <summary>The gateway's components (docs/features/F1.34-about-components.md, acceptance tests 1, 3).</summary>
public sealed class GatewayComponentsTests(OperatorGatewayFactory gateway) : IClassFixture<OperatorGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheGatewayCarriesItsComponentsFromItsBuild()
    {
        var report = ComponentReport.Of(typeof(Program).Assembly);

        Assert.Equal(ComponentHost.Gateway, report.Host);
        Assert.Equal(
            ["Microsoft.NETCore.App", "Microsoft.AspNetCore.App"],
            report.Components.Where(c => c.IsFramework).Select(c => c.Name)
        );
        Assert.Contains(report.Components, c => c is { Name: "AprsNet.Packets", Licence: "MIT", IsFramework: false });
        Assert.DoesNotContain(report.Components, c => c.Name.StartsWith("Avalonia", StringComparison.Ordinal)); // the UI is the browser's
    }

    [Fact]
    public async Task AViewerIsRefusedTheGatewaysComponentsAndTheOperatorGetsThem()
    {
        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);
        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.GetComponentsAsync(Ct));
        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);

        await using var signedIn = gateway.CreateRemote(await gateway.SignInAsync(Ct));
        await signedIn.StartAsync(Ct);
        var report = await signedIn.GetComponentsAsync(Ct);
        Assert.False(string.IsNullOrEmpty(report.Runtime));
    }
}
