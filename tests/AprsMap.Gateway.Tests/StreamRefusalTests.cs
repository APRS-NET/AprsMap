// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.Gateway.Tests;

/// <summary>
/// A stream the gateway refuses reaches the browser as the engine's error, not the transport's: a viewer's Messages
/// panel says messages are the operator's, rather than the page taking the refusal for a lost connection and fading
/// in and out (F1.14 behavior 7).
/// </summary>
public sealed class StreamRefusalTests(GatewayFactory gateway) : IClassFixture<GatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AViewersMessagesStreamIsRefusedAsTheOperators()
    {
        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);

        var refused = await Assert.ThrowsAsync<EngineAccessException>(async () =>
        {
            await foreach (var _ in viewer.WatchMessagesAsync(Ct)) { }
        });

        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);
    }
}
