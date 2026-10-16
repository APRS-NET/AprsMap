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
    public void EachSourceSaysWhoUsesItAndAprsIsOrKissOverTcpCanBeAdded()
    {
        var (section, _) = Create(BothOnRotate());

        Assert.Equal(["rotate.aprs2.net", "euro"], section.Rows.Select(r => r.Name));
        Assert.Equal("Used by: Home, Portable", section.Rows[0].UsedByText);
        Assert.Equal("Not used by any profile", section.Rows[1].UsedByText);
        Assert.Equal(
            ["APRS-IS", "KISS over TCP", "KISS over serial · coming", "GPS · coming"],
            section.Kinds.Select(k => k.Name)
        );
        Assert.Equal([true, true, false, false], section.Kinds.Select(k => k.IsAvailable));
    }

    [Fact]
    public async Task AddingAKissSourceTicksItForTheActiveProfileByDefault()
    {
        var (section, engine) = Create(BothOnRotate());
        section.AddCommand.Execute(null);
        section.Kinds[1].IsSelected = true; // KISS over TCP

        Assert.True(section.IsKiss);
        Assert.False(section.IsAprsIs);
        Assert.Equal([true, false], section.EditProfiles.Select(p => p.IsChecked)); // the active profile
        section.EditHost = " 10.10.20.144 ";
        await section.SaveCommand.ExecuteAsync(null);

        var tnc = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.KissTcp);
        Assert.Equal(("10.10.20.144", "10.10.20.144", null, null), (tnc.Name, tnc.Host, tnc.Port, tnc.TncPort));
        Assert.Equal("10.10.20.144:8001", section.Rows[^1].Server);
        Assert.Contains(tnc.Id, engine.Settings.Profiles[0].SourceIds);
        Assert.DoesNotContain(tnc.Id, engine.Settings.Profiles[1].SourceIds);
        Assert.False(section.IsEditing);
    }

    [Fact]
    public async Task EditingAKissSourceKeepsItsKindAndMovesItBetweenProfiles()
    {
        var tnc = new SourceSettings("tnc", "Direwolf", SourceKind.KissTcp, Host: "10.10.20.144");
        var settings = BothOnRotate() with { Sources = [FakeEngine.Rotate, Euro, tnc] };
        settings = settings with
        {
            Profiles =
            [
                settings.Profiles[0] with
                {
                    SourceIds = [.. settings.Profiles[0].SourceIds, "tnc"],
                },
                settings.Profiles[1],
            ],
        };
        var (section, engine) = Create(settings);

        section.Rows[2].EditCommand.Execute(null);
        Assert.False(section.CanPickKind);
        Assert.Equal([true, false], section.EditProfiles.Select(p => p.IsChecked));
        section.EditPort = "8002";
        section.EditTncPort = "1";
        section.EditProfiles[0].IsChecked = false;
        section.EditProfiles[1].IsChecked = true;
        await section.SaveCommand.ExecuteAsync(null);

        var saved = engine.Settings.SourceList.Single(s => s.Id == "tnc");
        Assert.Equal((8002, 1), (saved.Port, saved.TncPort));
        Assert.DoesNotContain("tnc", engine.Settings.Profiles[0].SourceIds);
        Assert.Contains("tnc", engine.Settings.Profiles[1].SourceIds);
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
