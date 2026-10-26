// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Station lists in the UI (docs/features/F1.33-station-lists.md, acceptance tests 1, 3, 5).</summary>
public sealed class StationListsUiTests
{
    private static Localizer Text => new(CultureInfo.GetCultureInfo("en-US"));

    private static AppSettings WithList(string text = "KD0XYZ Sweep 2\nWA0RC*") =>
        FakeEngine.TwoProfiles() with
        {
            Profiles =
            [
                FakeEngine.TwoProfiles().Profiles[0] with
                {
                    StationLists = [new StationList("spotters", "Spotters", text)],
                },
                FakeEngine.TwoProfiles().Profiles[1],
            ],
        };

    [Fact]
    public async Task TheEditorPointsAtABadLineSavesWhenFixedAndCopiesOutAndToAnotherProfile()
    {
        var engine = new FakeEngine { Settings = WithList() };
        AppSettings? shown = null;
        var section = new StationListsSectionViewModel(engine, Text, s => shown = s);
        section.Show(engine.Settings);
        section.Selected = section.Lists[0];
        Assert.Equal("Spotters", section.Name);
        Assert.Equal("2 entries", section.Summary);

        section.Text += "\nnot*valid";
        Assert.False(section.CanSave);
        Assert.Equal("Line 3 isn't a callsign, a prefix with * or an object name: not*valid", section.Summary);

        section.Text = "# spotters\nKD0XYZ Sweep 2\nWA0RC*\nAID-3 first aid";
        Assert.True(section.CanSave);
        await section.SaveListCommand.ExecuteAsync(null);
        var saved = engine.Settings.Profiles[0].StationListSet.Single();
        Assert.Equal("# spotters\nKD0XYZ Sweep 2\nWA0RC*\nAID-3 first aid", saved.Text);
        Assert.Same(engine.Settings, shown);

        string? copied = null;
        section.CopyToClipboard = t =>
        {
            copied = t;
            return Task.CompletedTask;
        };
        section.PasteFromClipboard = () => Task.FromResult<string?>("N0NEW\n");
        await section.CopyListCommand.ExecuteAsync(null);
        Assert.Equal(saved.Text, copied); // the same text back out, headings and all
        await section.PasteCommand.ExecuteAsync(null);
        Assert.Equal("N0NEW\n", section.Text);

        section.Show(engine.Settings);
        section.Selected = section.Lists[0];
        await section.CopyToProfileCommand.ExecuteAsync(null);
        Assert.Equal("Spotters", engine.Settings.Profiles[1].StationListSet.Single().Name);
        Assert.Single(engine.Settings.Profiles[0].StationListSet); // each profile keeps its own
        Assert.Equal("Copied to Portable.", section.Message);
    }

    [Fact]
    public async Task TheViewFilterShowsOnlyAListAndTheChipNamesIt()
    {
        var engine = new FakeEngine { Settings = WithList() };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        var spotters = Assert.Single(vm.ViewFilter.Lists);
        Assert.Equal("Spotters", spotters.Label);

        spotters.IsChecked = true;

        Assert.Equal(["spotters"], vm.ViewFilter.Current!.Lists!);
        vm.ViewFilter.ShowCounts(3, 120);
        Assert.StartsWith("Spotters · 3 of 120", vm.ViewFilter.ChipText, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AddToListPutsTheStationOnALineOfItsOwnOnce()
    {
        var engine = new FakeEngine { Settings = WithList() };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        Assert.True(vm.CanAddToList);
        Assert.Equal("Spotters", Assert.Single(vm.AddToListChoices).Name);

        await vm.AddToListAsync("spotters", "n0new-9");
        await vm.AddToListAsync("spotters", "KD0XYZ-7"); // in already, as KD0XYZ

        Assert.Equal("KD0XYZ Sweep 2\nWA0RC*\nN0NEW-9", engine.Settings.Profiles[0].StationListSet.Single().Text);
    }
}
