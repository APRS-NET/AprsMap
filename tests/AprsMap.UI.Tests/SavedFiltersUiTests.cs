// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Settings;
using AprsMap.UI.Stations;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Saved filters in the UI (docs/features/F1.42-saved-filters.md, acceptance tests 1–6).</summary>
public sealed class SavedFiltersUiTests
{
    private static readonly StationFilter Storm = new(HeardWithinMinutes: 30, WithWeather: true, Lists: ["spotters"]);

    private static readonly SavedFilter StormSpotting = new(
        "storm",
        "Storm spotting",
        Storm,
        new SavedMapView(38.9, -94.7, 10)
    );

    private static readonly SavedFilter LocalRf = new("rf", "Local RF", new StationFilter(Reception: Reception.Direct));

    private static AppSettings With(bool readOnly = false, params SavedFilter[] saved)
    {
        var two = FakeEngine.TwoProfiles();
        return two with
        {
            ReadOnly = readOnly,
            Profiles =
            [
                two.Profiles[0] with
                {
                    StationLists = [new StationList("spotters", "Skywarn spotters", "KD0XYZ")],
                    SavedFilters = saved,
                },
                two.Profiles[1],
            ],
        };
    }

    private static MainViewModel App(FakeEngine engine)
    {
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        return vm;
    }

    private sealed class MemoryStore : IPreferenceStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetAsideAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void ChoosingASavedFilterShowsItsConditionsNamesItAndMovesTheMap()
    {
        var engine = new FakeEngine { Settings = With(false, StormSpotting, LocalRf) };
        var vm = App(engine);
        var filter = vm.ViewFilter;
        SavedMapView? moved = null;
        filter.MapViewRequested += (_, view) => moved = view;
        Assert.Equal(["All", "My filter", "Storm spotting", "Local RF"], filter.Choices.Select(c => c.Label));
        Assert.True(filter.ShowsChip); // saved filters to switch to, even with nothing filtered

        filter.Choices[2].ChooseCommand.Execute(null);

        Assert.True(SavedFilterText.Same(Storm, filter.Current));
        Assert.Equal(StormSpotting.View, moved);
        Assert.True(filter.Choices[2].IsChecked);
        filter.ShowCounts(4, 120);
        Assert.StartsWith("Storm spotting · 4 of 120", filter.ChipText, StringComparison.Ordinal);
        moved = null;
        filter.Choices[3].ChooseCommand.Execute(null); // no view: the map stays
        Assert.Null(moved);
        Assert.Equal(Reception.Direct, filter.Current!.Reception);
    }

    [Fact]
    public void ChangingOneMarksItChangedBackToSavedRestoresAndMyFilterIsKept()
    {
        var engine = new FakeEngine { Settings = With(false, StormSpotting) };
        var filter = App(engine).ViewFilter;
        filter.WithTrack = true; // My filter, before any saved filter
        filter.Choose(FilterChoiceKind.Saved, "storm");

        filter.MessageCapable = true;
        Assert.True(filter.IsChanged);
        filter.ShowCounts(2, 120);
        Assert.StartsWith("Storm spotting (changed)", filter.ChipText, StringComparison.Ordinal);
        filter.BackToSavedCommand.Execute(null);
        Assert.False(filter.IsChanged);
        Assert.False(filter.MessageCapable);

        filter.Choose(FilterChoiceKind.Mine, null);
        Assert.Equal(new StationFilter(WithTrack: true), filter.Current); // My filter as it was
    }

    [Fact]
    public async Task SaveAsKeepsTheMapViewOnlyWhenTickedAndSaveWritesOverItForEveryone()
    {
        var engine = new FakeEngine { Settings = With() };
        var vm = App(engine);
        var filter = vm.ViewFilter;
        filter.WithWeather = true;
        filter.SaveAsName = " Weather ";

        await filter.SaveAsCommand.ExecuteAsync(null);

        var saved = Assert.Single(engine.Settings.Profiles[0].SavedFilterSet);
        Assert.Equal(("Weather", (SavedMapView?)null), (saved.Name, saved.View)); // unticked by default (decision 3)
        Assert.Equal("Weather", filter.InUse!.Name);
        filter.Heard = filter.HeardOptions.Single(o => Equals(o.Value, 10));
        Assert.True(filter.IsChanged);
        await filter.SaveOverCommand.ExecuteAsync(null);
        Assert.Equal(10, engine.Settings.Profiles[0].SavedFilterSet[0].Filter.HeardWithinMinutes);
        Assert.False(filter.IsChanged);

        filter.SaveAsName = "weather"; // taken, whatever the case
        await filter.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal("Each saved filter needs a name of its own, up to 40 characters.", filter.SaveError);
    }

    [Fact]
    public void AViewerWhoMayNotSaveGetsTheirChangesAsMyFilter()
    {
        var engine = new FakeEngine { Settings = With(readOnly: true, StormSpotting) };
        var filter = App(engine).ViewFilter;
        Assert.False(filter.CanSave);
        Assert.False(filter.SaveAsCommand.CanExecute(null));
        filter.Choose(FilterChoiceKind.Saved, "storm");

        filter.MessageCapable = true;

        Assert.Null(filter.InUse);
        Assert.True(filter.Choices[1].IsChecked); // My filter, with the change
        Assert.True(filter.Current!.MessageCapable);
    }

    [Fact]
    public void NextFilterGoesRoundAllMyFilterAndEachSavedFilter()
    {
        var engine = new FakeEngine { Settings = With(false, StormSpotting, LocalRf) };
        var filter = App(engine).ViewFilter;
        var seen = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            filter.NextFilterCommand.Execute(null);
            seen.Add(filter.Choices.Single(c => c.IsChecked).Label);
        }

        Assert.Equal(["My filter", "Storm spotting", "Local RF", "All", "My filter"], seen);
    }

    [Fact]
    public async Task TheSavedFilterInUseComesBackNextTime()
    {
        var store = new MemoryStore();
        var engine = new FakeEngine { Settings = With(false, StormSpotting) };
        var first = App(engine);
        await first.ViewFilter.LoadAsync(store);
        first.ViewFilter.Choose(FilterChoiceKind.Saved, "storm");

        var vm = new MainViewModel(engine, new FakeTimeProvider());
        await vm.ViewFilter.LoadAsync(store);
        vm.Show(engine.Settings);

        Assert.Equal("Storm spotting", vm.ViewFilter.InUse?.Name);
        Assert.True(SavedFilterText.Same(Storm, vm.ViewFilter.Current));
    }

    [Fact]
    public async Task SettingsRenamesDeletesWithUndoMovesCopiesAndPastes()
    {
        var engine = new FakeEngine { Settings = With(false, StormSpotting, LocalRf) };
        var section = new StationListsSectionViewModel(
            engine,
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            _ => { }
        );
        var copied = new List<string>();
        section.CopyToClipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        section.Show(engine.Settings);
        Assert.Equal(
            "lists: Skywarn spotters; heard: 30 min; with: weather · moves the map",
            section.SavedFilters[0].Summary
        );

        section.SavedFilters[0].RenameCommand.Execute(null);
        section.NewName = "Storm";
        await section.RenameCommand.ExecuteAsync(null);
        section.Show(engine.Settings);
        await section.SavedFilters[1].MoveUpCommand.ExecuteAsync(null);
        section.Show(engine.Settings);
        Assert.Equal(["Local RF", "Storm"], section.SavedFilters.Select(r => r.Name));

        await section.SavedFilters[1].CopyCommand.ExecuteAsync(null);
        Assert.Equal("lists: Skywarn spotters; heard: 30 min; with: weather; view: 38.9 -94.7 z10", copied.Single());
        await section.SavedFilters[1].DeleteCommand.ExecuteAsync(null);
        Assert.Equal("Storm deleted.", section.DeletedText);
        await section.UndoDeleteCommand.ExecuteAsync(null);
        Assert.Equal(["Local RF", "Storm"], engine.Settings.Profiles[0].SavedFilterSet.Select(f => f.Name));

        section.PastedName = "Volunteers";
        section.PastedText = "lists: MS150 volunteers, Skywarn spotters; kinds: Stations";
        await section.AddPastedFilterCommand.ExecuteAsync(null);
        var pasted = engine.Settings.Profiles[0].SavedFilterSet[^1];
        Assert.Equal(["spotters"], pasted.Filter.Lists);
        Assert.Equal(StationKinds.Stations, pasted.Filter.Kinds);
        Assert.Equal("Added without lists this profile doesn't have: MS150 volunteers.", section.Message);
    }

    [Fact]
    public async Task DeletingAListTakesItOutOfTheSavedFiltersThatShowedIt()
    {
        var engine = new FakeEngine { Settings = With(false, StormSpotting) };
        var section = new StationListsSectionViewModel(
            engine,
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            _ => { }
        );
        section.Show(engine.Settings);
        section.Selected = section.Lists[0];

        await section.DeleteCommand.ExecuteAsync(null);

        var storm = Assert.Single(engine.Settings.Profiles[0].SavedFilterSet);
        Assert.Null(storm.Filter.Lists);
        Assert.Equal(30, storm.Filter.HeardWithinMinutes);
    }
}
