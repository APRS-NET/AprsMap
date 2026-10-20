// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Mic-E status in Settings › Beacons and the status bar (docs/features/F2.12-mic-e-status.md).</summary>
public sealed class MicEStatusUiTests
{
    private static readonly SourceSettings AprsIs = FakeEngine.Rotate;

    private static AppSettings Home(MicEStatus? micE = null) =>
        new(
            [
                new StationProfile("home", "Home", "N0TEST", 12345, [AprsIs.Id], 49.0, -72.0) with
                {
                    Licensed = true,
                    PositionConfirmed = true,
                    PositionBeacon = PositionBeaconSettings.Off with { MicE = micE },
                },
            ],
            "home",
            UnitSettings.Metric,
            Sources: [AprsIs]
        );

    private static (BeaconsSectionViewModel Section, FakeEngine Engine) Create(AppSettings settings)
    {
        var engine = new FakeEngine { Settings = settings };
        var section = new BeaconsSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(settings);
        return (section, engine);
    }

    [Fact]
    public async Task MicEIsAFormatWithAStatusAndTakesNoPhg()
    {
        var (section, engine) = Create(Home());
        Assert.True(section.FormatStandard);
        Assert.Equal(
            ["Off duty", "En route", "In service", "Returning", "Committed", "Special", "Priority", "Emergency"],
            section.MicEStatuses.Select(s => s.Name)
        );
        Assert.Equal(MicEStatus.OffDuty, section.SelectedMicEStatus!.Value); // the default (behavior 2)

        section.IncludePhg = true;
        section.FormatStandard = false;
        section.MicE = true;
        Assert.False(section.CanIncludePhg);
        section.SelectedMicEStatus = section.MicEStatuses.Single(s => s.Value == MicEStatus.EnRoute);
        await section.SaveCommand.ExecuteAsync(null);

        var beacon = engine.Settings.GetActiveProfile()!.EffectivePositionBeacon;
        Assert.Equal((MicEStatus.EnRoute, false, null), (beacon.MicE, beacon.Compressed, beacon.Phg));
        Assert.Equal(0, engine.PositionsSent); // only Emergency is sent at once
    }

    [Fact]
    public async Task EmergencyAsksFirstAndCancelSavesNothing()
    {
        var (section, engine) = Create(Home(MicEStatus.EnRoute));
        Assert.True(section.MicE);
        Assert.Equal(MicEStatus.EnRoute, section.SelectedMicEStatus!.Value);

        section.SelectedMicEStatus = section.MicEStatuses.Single(s => s.Value == MicEStatus.Emergency);
        await section.SaveCommand.ExecuteAsync(null);
        Assert.True(section.IsConfirmingEmergency);
        Assert.Empty(engine.Saved);

        section.CancelEmergencyCommand.Execute(null);

        Assert.False(section.IsConfirmingEmergency);
        Assert.Empty(engine.Saved);
        Assert.Equal(0, engine.PositionsSent);
    }

    [Fact]
    public async Task SendEmergencySavesAndSendsThePositionAtOnce()
    {
        var (section, engine) = Create(Home(MicEStatus.EnRoute));
        section.SelectedMicEStatus = section.MicEStatuses.Single(s => s.Value == MicEStatus.Emergency);
        await section.SaveCommand.ExecuteAsync(null);

        await section.ConfirmEmergencyCommand.ExecuteAsync(null);

        Assert.Equal(MicEStatus.Emergency, engine.Settings.GetActiveProfile()!.EffectivePositionBeacon.MicE);
        Assert.Equal([null], engine.PositionSources); // on every source that can send (behavior 8)

        await section.SaveCommand.ExecuteAsync(null); // already Emergency: saved without asking again
        Assert.False(section.IsConfirmingEmergency);
        Assert.Equal(1, engine.PositionsSent);
    }

    [Fact]
    public async Task WhileEmergencyIsSetTheStatusBarSaysSoAndOpensBeacons()
    {
        var engine = new FakeEngine { Settings = Home(MicEStatus.Emergency) };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        Assert.True(vm.IsSendingEmergency);

        await vm.OpenBeaconsCommand.ExecuteAsync(null);
        Assert.True(vm.Settings!.IsBeaconsSection);

        vm.Show(Home(MicEStatus.EnRoute));
        Assert.False(vm.IsSendingEmergency);
    }
}
