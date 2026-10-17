// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.Gateway.Tests;

/// <summary>
/// No browser transmits until operator sign-in exists (docs/features/F2.1-own-station.md, behavior 9, acceptance test
/// 5), on a gateway that lets browsers change settings and on one that doesn't.
/// </summary>
public sealed class GatewayTransmitTests(TrustedGatewayFactory trusted, GatewayFactory locked)
    : IClassFixture<TrustedGatewayFactory>,
        IClassFixture<GatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrowsersCannotTurnTransmitOnSendOrTestSend(bool allowSettingsChanges)
    {
        await using var remote = (allowSettingsChanges ? trusted : locked).CreateRemote();
        await remote.StartAsync(Ct);

        var turn = await Assert.ThrowsAsync<EngineAccessException>(() =>
            remote.SetToggleAsync(Toggle.Transmit, true, Ct)
        );
        var send = await Assert.ThrowsAsync<EngineAccessException>(() => remote.SendPositionAsync(null, Ct));
        var test = await Assert.ThrowsAsync<EngineAccessException>(() => remote.TestSendAsync("rotate", Ct));

        Assert.Equal(EngineErrorCodes.OperatorOnly, turn.EngineError.Code);
        Assert.Equal(EngineErrorCodes.OperatorOnly, send.EngineError.Code);
        Assert.Equal(EngineErrorCodes.OperatorOnly, test.EngineError.Code);
        Assert.False((await remote.GetSettingsAsync(Ct)).EffectiveToggles.Transmit);
    }

    [Fact]
    public async Task ATrustedBrowserCantTurnTransmitOnBySavingSettings()
    {
        await using var remote = trusted.CreateRemote();
        await remote.StartAsync(Ct);
        var settings = await remote.GetSettingsAsync(Ct);

        await remote.SaveSettingsAsync(
            settings with
            {
                Toggles = settings.EffectiveToggles with { Transmit = true },
            },
            Ct
        );

        Assert.False((await remote.GetSettingsAsync(Ct)).EffectiveToggles.Transmit);
    }
}
