// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.Gateway.Tests;

/// <summary>The device list on a gateway (docs/features/F4.16-device-list.md, acceptance test 7, behavior 12).</summary>
public sealed class GatewayDeviceListTests(OperatorGatewayFactory gateway) : IClassFixture<OperatorGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AViewerSeesTheListInUseButOnlyTheOperatorUpdatesIt()
    {
        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);
        var status = await viewer.GetDeviceListAsync(Ct);
        Assert.Equal(DeviceListOrigin.BuiltIn, status.Origin);
        Assert.False(status.UrlIsSetting); // a gateway's address is its environment's
        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.UpdateDeviceListAsync(Ct));
        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);
    }

    [Fact]
    public async Task TheOperatorCantPointTheServerElsewhereFromABrowser()
    {
        await using var signedIn = gateway.CreateRemote(await gateway.SignInAsync(Ct));
        await signedIn.StartAsync(Ct);
        var settings = await signedIn.GetSettingsAsync(Ct);

        await signedIn.SaveSettingsAsync(
            settings with
            {
                DeviceList = new DeviceListSettings(DeviceListSchedule.Daily, Url: "https://evil.example/list.json"),
            },
            Ct
        );

        var saved = (await signedIn.GetSettingsAsync(Ct)).EffectiveDeviceList;
        Assert.Equal(DeviceListSchedule.Daily, saved.Schedule); // the schedule is the operator's
        Assert.Null(saved.Url);
        Assert.Equal(
            AprsNet.DeviceId.AprsDeviceListSource.DefaultUrl.ToString(),
            (await signedIn.GetDeviceListAsync(Ct)).Url
        );
    }
}
