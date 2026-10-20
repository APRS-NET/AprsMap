// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>Saved status texts and Send status now in Settings › Beacons (docs/features/F2.11-status-reports.md).</summary>
public sealed class StatusReportUiTests
{
    private static readonly SourceSettings AprsIs = FakeEngine.Rotate;

    private static AppSettings Home(StatusBeaconSettings? status = null) =>
        new(
            [
                new StationProfile("home", "Home", "N0TEST", 12345, [AprsIs.Id], 49.0, -72.0) with
                {
                    Licensed = true,
                    PositionConfirmed = true,
                    StatusBeacon = status,
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
    public async Task ASavedTextFillsTheBoxAndTheListIsSavedWithTheSection()
    {
        var (section, engine) = Create(
            Home(new StatusBeaconSettings(Text: "Net control", SavedTexts: ["QRV on 146.52", "Net control"]))
        );
        Assert.Equal(["QRV on 146.52", "Net control"], section.SavedTexts);
        Assert.False(section.SaveTextCommand.CanExecute(null)); // the text shown is already saved
        Assert.False(section.RemoveSavedTextCommand.CanExecute(null));

        section.SelectedSavedText = "Net control";
        Assert.Equal("Net control", section.StatusText);
        Assert.False(section.SaveTextCommand.CanExecute(null)); // already saved

        section.RemoveSavedTextCommand.Execute(null);
        section.StatusText = "Mobile, back at 6";
        section.SaveTextCommand.Execute(null);
        section.SelectedAddition = section.Additions.Single(a => a.Value == StatusAddition.GridSquare);
        await section.SaveCommand.ExecuteAsync(null);

        var status = engine.Settings.GetActiveProfile()!.EffectiveStatusBeacon;
        Assert.Equal(["QRV on 146.52", "Mobile, back at 6"], status.SavedTextList);
        Assert.Equal(("Mobile, back at 6", StatusAddition.GridSquare), (status.Text, status.Add));
        Assert.Equal(["Nothing", "My grid square", "The time (UTC)"], section.Additions.Select(a => a.Name));
    }

    [Fact]
    public void NoMoreThanTwentyTextsCanBeSaved()
    {
        var twenty = Enumerable.Range(1, StatusBeaconSettings.MaxSavedTexts).Select(i => $"Status {i}").ToList();
        var (section, _) = Create(Home(new StatusBeaconSettings(SavedTexts: twenty)));

        section.StatusText = "One more";

        Assert.False(section.SaveTextCommand.CanExecute(null));
    }

    [Fact]
    public async Task SendStatusNowSavesThenSendsAndSaysWhere()
    {
        var (section, engine) = Create(Home());
        section.StatusText = "QRV on 146.52";

        await section.SendStatusNowCommand.ExecuteAsync(null);

        Assert.Equal("QRV on 146.52", engine.Settings.GetActiveProfile()!.EffectiveStatusBeacon.Text);
        Assert.Equal([null], engine.StatusSources);
        Assert.Equal("Status sent on 1 source", section.StatusSentText);
    }
}
