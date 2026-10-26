// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>The two-way IGate in the UI (docs/features/F3.4-two-way-igate.md, behaviors 1 and 7).</summary>
public sealed class IGateToRfUiTests
{
    private static Localizer Text => new(CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public async Task TheSecondSwitchSavesAndTheCardCountsWhatWentToRf()
    {
        var settings = FakeEngine.TwoProfiles() with
        {
            Profiles =
            [
                FakeEngine.TwoProfiles().Profiles[0] with
                {
                    IGate = true,
                },
                FakeEngine.TwoProfiles().Profiles[1],
            ],
        };
        var engine = new FakeEngine { Settings = settings };
        var section = new SourcesSectionViewModel(engine, Text, _ => { });
        section.Show(engine.Settings);
        Assert.False(section.IGateToRfOn);

        section.IGateToRfOn = true;
        await Task.Yield();

        Assert.True(engine.Settings.Profiles[0].EffectiveIGateToRf);
        section.ShowIGate(new IGateStatus(12, 3, null, ToRf: true, GatedToRf: 2, RefusedToRf: 1));
        Assert.Equal("12 packets gated, 3 not · 2 sent to RF, 1 not", section.IGateCountText);
    }

    [Fact]
    public void TheDashboardListsWhoIsHeardOnRf()
    {
        var vm = GpsDashboardTests.Create();
        vm.Show(
            FakeEngine.EmptyDashboard() with
            {
                HeardOnRf = [new("W1AW-9", 0, DateTimeOffset.UnixEpoch), new("N0DIGI", 1, DateTimeOffset.UnixEpoch)],
                HeardOnRfTotal = 2,
            }
        );

        Assert.Equal("2 stations heard on the TNCs", vm.HeardOnRfText);
        Assert.Equal(["W1AW-9", "N0DIGI"], vm.HeardOnRf.Select(r => r.Name));
        Assert.StartsWith("direct · ", vm.HeardOnRf[0].Value, StringComparison.Ordinal);
        Assert.StartsWith("1 hop · ", vm.HeardOnRf[1].Value, StringComparison.Ordinal);
        Assert.True(vm.HeardOnRf[0].CanOpen);
    }
}
