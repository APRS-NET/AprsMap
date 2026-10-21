// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Settings › Data › Device list (docs/features/F4.16-device-list.md, acceptance tests 1, 5, 6).</summary>
public sealed class DeviceListUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private static (DeviceListViewModel List, FakeEngine Engine) Create(bool canChange = true)
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var list = new DeviceListViewModel(
            engine,
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            time,
            canChange,
            _ => { }
        );
        list.Show(engine.Settings);
        list.Show(engine.DeviceList);
        return (list, engine);
    }

    [Fact]
    public void ItSaysWhichListIsInUseAndWhenItWasChecked()
    {
        var (list, engine) = Create();
        Assert.Equal("In use: built in, from 18 October 2026", list.InUseText);
        Assert.Equal("Not checked yet", list.CheckedText);
        Assert.True(list.ShowsChecks);

        list.Show(
            engine.DeviceList with
            {
                Origin = DeviceListOrigin.Downloaded,
                GeneratedAt = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero),
                LastChecked = Now.AddHours(-2),
            }
        );
        Assert.Equal("In use: downloaded, from 20 October 2026", list.InUseText);
        Assert.Equal("Checked today: up to date", list.CheckedText);
        Assert.False(list.CheckFailed);

        list.Show(
            engine.DeviceList with
            {
                LastChecked = Now.AddDays(-3),
                LastError = new EngineError(EngineErrorCodes.DeviceListUnreachable, "No route to host"),
            }
        );
        Assert.Equal("Checked 18 October 2026: couldn't reach the server", list.CheckedText);
        Assert.True(list.CheckFailed);
    }

    [Fact]
    public async Task UpdateNowChecksAndTheScheduleIsSavedAsItsChosen()
    {
        var (list, engine) = Create();

        await list.UpdateNowCommand.ExecuteAsync(null);
        Assert.Equal(1, engine.DeviceListUpdates);

        list.Schedule = list.ScheduleOptions.Single(o => o.Value == DeviceListSchedule.Off);
        await Task.Yield();
        Assert.Equal(DeviceListSchedule.Off, engine.Settings.EffectiveDeviceList.Schedule);
    }

    [Fact]
    public void AViewerSeesTheListButNotTheChecksAndWithoutAnAddressThereAreNone()
    {
        var (viewer, engine) = Create(canChange: false);
        Assert.False(viewer.ShowsChecks);
        Assert.False(viewer.ShowsUrl);
        Assert.NotNull(viewer.InUseText);

        var (list, _) = Create();
        list.Show(engine.DeviceList with { CanDownload = false, Url = null });
        Assert.False(list.ShowsChecks);
        Assert.Equal("Downloading is turned off: the built-in list is used.", list.CheckedText);
    }

    [Fact]
    public async Task OnTheDesktopTheAddressCanBeChangedEmptiedOrReset()
    {
        var (list, engine) = Create();
        list.Show(
            engine.DeviceList with
            {
                UrlIsSetting = true,
                Url = "https://aprs-deviceid.aprsfoundation.org/tocalls.pretty.json",
            }
        );
        Assert.True(list.ShowsUrl);
        Assert.Equal("https://aprs-deviceid.aprsfoundation.org/tocalls.pretty.json", list.Url);

        list.Url = "not an address";
        await list.SaveUrlCommand.ExecuteAsync(null);
        Assert.Equal("That isn't a web address (https://…).", list.UrlError);

        list.Url = "https://mirror.example/tocalls.json";
        await list.SaveUrlCommand.ExecuteAsync(null);
        Assert.Null(list.UrlError);
        Assert.Equal("https://mirror.example/tocalls.json", engine.Settings.EffectiveDeviceList.Url);

        list.Url = "";
        await list.SaveUrlCommand.ExecuteAsync(null);
        Assert.Equal("", engine.Settings.EffectiveDeviceList.Url); // never download

        await list.ResetUrlCommand.ExecuteAsync(null);
        Assert.Null(engine.Settings.EffectiveDeviceList.Url);
    }
}
