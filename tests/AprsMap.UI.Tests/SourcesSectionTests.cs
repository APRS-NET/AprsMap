// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

public class SourcesSectionTests
{
    private static readonly SourceSettings Euro = new("euro", "euro", SourceKind.AprsIs, "euro.aprs2.net:14580");

    private static (SourcesSectionViewModel Section, FakeEngine Engine) Create(AppSettings settings)
    {
        var engine = new FakeEngine { Settings = settings };
        var section = new SourcesSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(settings);
        return (section, engine);
    }

    private static AppSettings BothOnRotate() => FakeEngine.TwoProfiles() with { Sources = [FakeEngine.Rotate, Euro] };

    [Fact]
    public void EachSourceSaysWhoUsesItAndOnlyAprsIsCanBeAddedForNow()
    {
        var (section, _) = Create(BothOnRotate());

        Assert.Equal(["rotate.aprs2.net", "euro"], section.Rows.Select(r => r.Name));
        Assert.Equal("Used by: Home, Portable", section.Rows[0].UsedByText);
        Assert.Equal("Not used by any profile", section.Rows[1].UsedByText);
        Assert.Equal(
            ["APRS-IS", "KISS over TCP · coming", "KISS over serial · coming", "GPS · coming"],
            section.Kinds.Select(k => k.Name)
        );
        Assert.Equal([true, false, false, false], section.Kinds.Select(k => k.IsAvailable));
    }

    [Fact]
    public async Task AddingCreatesAnAprsIsSourceNamedAfterItsServer()
    {
        var (section, engine) = Create(BothOnRotate());

        section.AddCommand.Execute(null);
        section.EditServer = " noam.aprs2.net:14580 ";
        section.EditFilter = "t/m";
        await section.SaveCommand.ExecuteAsync(null);

        var added = engine.Saved[^1].SourceList[^1];
        Assert.Equal(
            (SourceKind.AprsIs, "noam.aprs2.net", "noam.aprs2.net:14580", "t/m"),
            (added.Kind, added.Name, added.Server, added.Filter)
        );
        Assert.False(section.IsEditing);
    }

    [Fact]
    public async Task EditingShowsWhoUsesItAndKeepsItsId()
    {
        var (section, engine) = Create(BothOnRotate());

        section.Rows[0].EditCommand.Execute(null);
        Assert.Equal("Used by: Home, Portable", section.EditUsedBy);
        section.EditName = "Rotate";
        await section.SaveCommand.ExecuteAsync(null);

        var saved = engine.Saved[^1].SourceList[0];
        Assert.Equal(("rotate", "Rotate"), (saved.Id, saved.Name));
    }

    [Fact]
    public async Task DeletingASourceInUseAsksFirstAndUnticksIt()
    {
        var (section, engine) = Create(BothOnRotate());

        await section.Rows[1].DeleteCommand.ExecuteAsync(null); // unused: at once
        Assert.Equal(["rotate"], engine.Saved[^1].SourceList.Select(s => s.Id));

        await section.Rows[0].DeleteCommand.ExecuteAsync(null);
        Assert.True(section.IsConfirmingDelete);
        Assert.Equal(
            "rotate.aprs2.net is used by Home, Portable. Delete it and untick it there?",
            section.PendingDeleteText
        );
        Assert.Single(engine.Saved);

        await section.ConfirmDeleteCommand.ExecuteAsync(null);
        var saved = engine.Saved[^1];
        Assert.Empty(saved.SourceList);
        Assert.All(saved.Profiles, p => Assert.Empty(p.SourceIds));
    }
}
