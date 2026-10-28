// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Saved filters' text and checks (docs/features/F1.42-saved-filters.md, behaviors 1, 9).</summary>
public sealed class SavedFilterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly StationList[] Lists =
    [
        new("spotters", "Skywarn spotters", "KD0XYZ"),
        new("ms150", "MS150 volunteers", "N0VOL"),
    ];

    private static readonly StationFilter StormSpotting = new(
        Categories: ["Weather"],
        HeardWithinMinutes: 30,
        WithWeather: true,
        Lists: ["spotters"]
    );

    [Fact]
    public void AFilterIsOneReadableLineThatReadsBackTheSame()
    {
        var view = new SavedMapView(38.9, -94.7, 10);

        var text = SavedFilterText.Format(StormSpotting, view, Lists);

        Assert.Equal(
            "lists: Skywarn spotters; categories: Weather; heard: 30 min; with: weather; view: 38.9 -94.7 z10",
            text
        );
        var read = SavedFilterText.Parse(text, Lists);
        Assert.True(SavedFilterText.Same(StormSpotting, read.Filter));
        Assert.Equal((view, 0, 0), (read.View, read.Errors.Count, read.MissingLists.Count));
    }

    [Fact]
    public void EveryConditionGoesThroughTheText()
    {
        var all = new StationFilter(
            StationKinds.ObjectsAndItems,
            ["Vehicles", "Weather"],
            60,
            Reception.Direct,
            MessageCapable: true,
            WithTrack: true,
            DeviceClasses: ["Tracker"],
            MicEStatuses: ["InService"],
            Tags: ["#skywarn"],
            WithTelemetry: true,
            HasProblems: true,
            Lists: ["ms150", "spotters"]
        );

        var read = SavedFilterText.Parse(SavedFilterText.Format(all, null, Lists), Lists);

        Assert.True(SavedFilterText.Same(all, read.Filter));
        Assert.Null(read.View);
    }

    [Fact]
    public void AnotherProfilesListIsFoundByNameAndOneItLacksIsPointedOut()
    {
        StationList[] other = [new("x1", "skywarn SPOTTERS", "KD0XYZ")]; // case doesn't matter

        var read = SavedFilterText.Parse("lists: Skywarn spotters, MS150 volunteers; heard: 2 h; colour: red", other);

        Assert.Equal(["x1"], read.Filter.Lists);
        Assert.Equal(["MS150 volunteers"], read.MissingLists);
        Assert.Equal(["heard: 2 h", "colour: red"], read.Errors);
    }

    [Fact]
    public void TheSameConditionsInAnotherOrderAreTheSame()
    {
        Assert.True(
            SavedFilterText.Same(
                new StationFilter(Categories: ["A", "B"], Lists: ["x", "y"]),
                new StationFilter(Categories: ["B", "A"], Lists: ["y", "x"])
            )
        );
        Assert.False(SavedFilterText.Same(StormSpotting, StormSpotting with { HeardWithinMinutes = 10 }));
        Assert.True(SavedFilterText.Same(null, StationFilter.None));
    }

    [Fact]
    public async Task NamesMustBeTheirOwnAndThereAreAtMostTwenty()
    {
        var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );
        var settings = await engine.GetSettingsAsync(Ct);
        AppSettings With(params SavedFilter[] filters) =>
            settings with
            {
                Profiles = [settings.Profiles[0] with { SavedFilters = filters }],
            };

        await engine.SaveSettingsAsync(With(SavedFilter.Create("Storm spotting", StormSpotting)), Ct);
        Assert.Equal("Storm spotting", (await engine.GetSettingsAsync(Ct)).Profiles[0].SavedFilterSet[0].Name);

        var twice = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(
                With(SavedFilter.Create("Storm", StormSpotting), SavedFilter.Create("storm", StationFilter.None)),
                Ct
            )
        );
        var tooLong = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(With(SavedFilter.Create(new string('x', 41), StormSpotting)), Ct)
        );
        var many = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(
                With([.. Enumerable.Range(0, 21).Select(i => SavedFilter.Create($"F{i}", StationFilter.None))]),
                Ct
            )
        );
        Assert.Equal(
            (
                EngineErrorCodes.SavedFilterNameTaken,
                EngineErrorCodes.SavedFilterNameTaken,
                EngineErrorCodes.SavedFilterTooMany
            ),
            (twice.EngineError.Code, tooLong.EngineError.Code, many.EngineError.Code)
        );
    }
}
