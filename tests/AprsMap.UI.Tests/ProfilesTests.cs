// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class ProfilesTests
{
    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    private sealed record Opened(WizardMode Mode, StationProfile? Profile, AppSettings Settings);

    private static (
        ProfilesSectionViewModel Section,
        FakeEngine Engine,
        FakeTimeProvider Time,
        List<Opened> Opened,
        List<AppSettings> Saved
    ) Create(AppSettings? settings = null)
    {
        var engine = new FakeEngine();
        if (settings is not null)
        {
            engine.Settings = settings;
        }
        var time = new FakeTimeProvider();
        var opened = new List<Opened>();
        var saved = new List<AppSettings>();
        var section = new ProfilesSectionViewModel(
            engine,
            time,
            English(),
            (mode, profile, current) =>
            {
                opened.Add(new Opened(mode, profile, current));
                return Task.CompletedTask;
            },
            saved.Add
        );
        return (section, engine, time, opened, saved);
    }

    /// <summary>The portable profile's source: a server that isn't a preset.</summary>
    private static readonly SourceSettings T2Test = new(
        "t2test",
        "t2test.example",
        SourceKind.AprsIs,
        "t2test.example:10152"
    );

    /// <summary>Rotate with filter text of its own.</summary>
    private static readonly SourceSettings Filtered = FakeEngine.Rotate with
    {
        Id = "filtered",
        Name = "rotate (filtered)",
        Filter = "r/40/-105/50",
    };

    private static AppSettings WithLocation() =>
        FakeEngine.TwoProfiles() with
        {
            Sources = [FakeEngine.Rotate, T2Test, Filtered],
            Profiles =
            [
                FakeEngine.TwoProfiles().Profiles[0] with
                {
                    Latitude = 40.0625,
                    Longitude = -105.125,
                },
                FakeEngine.TwoProfiles().Profiles[1] with
                {
                    SourceIds = [T2Test.Id],
                },
            ],
        };

    [Fact]
    public async Task EachProfileShowsItsCallsignServerAndLocatorAndTheActiveOneSaysSo()
    {
        var (section, engine, _, _, _) = Create(WithLocation());

        await section.ShowAsync(engine.Settings);

        Assert.Equal(["Home", "Portable"], section.Rows.Select(r => r.Name));
        var home = section.Rows[0];
        Assert.Equal(
            ("N0CALL", "Worldwide", "DN70kb", "Active"),
            (home.Callsign, home.ServerText, home.Locator, home.ActiveText)
        );
        Assert.False(home.CanUse);
        Assert.True(home.CanDelete);
        Assert.Equal("Edit Home", home.EditName);
        var portable = section.Rows[1];
        Assert.Equal(
            ("t2test.example:10152", null, null),
            (portable.ServerText, portable.Locator, portable.ActiveText)
        );
        Assert.True(portable.CanUse);
    }

    [Fact]
    public async Task UseSwitchesTheActiveProfile()
    {
        var (section, engine, _, _, saved) = Create();
        await section.ShowAsync(engine.Settings);

        await section.Rows[1].UseCommand.ExecuteAsync(null);

        Assert.Equal("portable", Assert.Single(engine.Saved).ActiveProfileId);
        Assert.Equal("portable", Assert.Single(saved).ActiveProfileId);
        Assert.True(section.Rows[1].IsActive);
    }

    [Fact]
    public async Task CopyAddsAUniquelyNamedCopyAfterTheOriginalAndOpensIt()
    {
        var (section, engine, _, opened, _) = Create();
        await section.ShowAsync(engine.Settings);

        await section.Rows[0].CopyCommand.ExecuteAsync(null);
        await section.Rows[0].CopyCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Home", "Home (copy 2)", "Home (copy)", "Portable"],
            engine.Settings.Profiles.Select(p => p.Name)
        );
        Assert.Equal("home", engine.Settings.ActiveProfileId);
        Assert.Equal(2, opened.Count);
        Assert.All(opened, o => Assert.Equal(WizardMode.Edit, o.Mode));
        Assert.Equal("Home (copy 2)", opened[1].Profile?.Name);
        Assert.NotEqual("home", opened[1].Profile?.Id);
    }

    [Fact]
    public async Task DeleteCanBeUndoneForTenSeconds()
    {
        var (section, engine, time, _, _) = Create();
        await section.ShowAsync(engine.Settings);

        await section.Rows[1].DeleteCommand.ExecuteAsync(null);
        Assert.Equal(["Home"], engine.Settings.Profiles.Select(p => p.Name));
        Assert.Equal("“Portable” deleted.", section.DeletedText);
        Assert.False(section.Rows[0].CanDelete); // now the only one

        await section.UndoDeleteCommand.ExecuteAsync(null);
        Assert.Equal(["Home", "Portable"], engine.Settings.Profiles.Select(p => p.Name));
        Assert.False(section.CanUndoDelete);

        await section.Rows[1].DeleteCommand.ExecuteAsync(null);
        time.Advance(ProfilesSectionViewModel.UndoWindow);
        Assert.False(section.CanUndoDelete);
        await section.UndoDeleteCommand.ExecuteAsync(null); // too late
        Assert.Single(engine.Settings.Profiles);
    }

    [Fact]
    public async Task DeletingTheActiveProfileHandsOverAndTheOnlyOneStays()
    {
        var (section, engine, _, _, _) = Create();
        await section.ShowAsync(engine.Settings);

        await section.Rows[0].DeleteCommand.ExecuteAsync(null);
        Assert.Equal("portable", engine.Settings.ActiveProfileId);

        await section.Rows[0].DeleteCommand.ExecuteAsync(null); // the only one: nothing happens
        Assert.Single(engine.Settings.Profiles);
    }

    [Fact]
    public async Task OnAReadOnlyGatewayTheListOnlyShows()
    {
        var (section, engine, _, _, _) = Create(FakeEngine.TwoProfiles() with { ReadOnly = true });

        await section.ShowAsync(engine.Settings);

        Assert.True(section.IsReadOnly);
        Assert.False(section.AddCommand.CanExecute(null));
        Assert.All(section.Rows, r => Assert.False(r.CanChange || r.CanUse));
    }

    [Fact]
    public async Task ARefusedChangeLeavesTheListAndSaysWhy()
    {
        var (section, engine, _, _, _) = Create();
        await section.ShowAsync(engine.Settings);
        engine.SaveFailure = EngineErrors.Create(EngineErrorCodes.SettingsReadOnly, "English");

        await section.Rows[1].UseCommand.ExecuteAsync(null);

        Assert.Equal(English()["Error.gateway.settings-read-only"], section.Error);
        Assert.Equal("home", engine.Settings.ActiveProfileId);
        Assert.True(section.Rows[0].IsActive);
    }

    [Fact]
    public async Task AddOpensTheWizardInAddMode()
    {
        var (section, engine, _, opened, _) = Create();
        await section.ShowAsync(engine.Settings);

        await section.AddCommand.ExecuteAsync(null);

        var open = Assert.Single(opened);
        Assert.Equal(WizardMode.Add, open.Mode);
        Assert.Null(open.Profile);
        await section.Rows[0].EditCommand.ExecuteAsync(null);
        Assert.Equal((WizardMode.Edit, "home"), (opened[1].Mode, opened[1].Profile?.Id));
    }

    // ── The wizard's add and edit modes ──

    private static async Task<SetupWizardViewModel> Wizard(
        FakeEngine engine,
        WizardMode mode,
        StationProfile? editing = null
    )
    {
        var wizard = new SetupWizardViewModel(engine, English(), null, mode, engine.Settings, editing);
        await wizard.LoadAsync(TestContext.Current.CancellationToken);
        return wizard;
    }

    [Fact]
    public async Task AddingNamesTheProfileAfterItsCallsignUntilANameIsTyped()
    {
        var engine = new FakeEngine();
        var wizard = await Wizard(engine, WizardMode.Add);
        Assert.True(wizard.IsProfileMode && wizard.IsAddMode);
        Assert.Equal("Add a profile", wizard.Title);

        wizard.Callsign = "AB1CD-7";
        Assert.Equal("AB1CD-7", wizard.ProfileName);
        wizard.ProfileName = "Hiking";
        wizard.Callsign = "AB1CD-9";
        Assert.Equal("Hiking", wizard.ProfileName);
        wizard.Filter = " b/AB1CD* ";
        await ToLastStep(wizard);
        await wizard.FinishCommand.ExecuteAsync(null);

        var saved = Assert.Single(engine.Saved);
        Assert.Equal(["Home", "Portable", "Hiking"], saved.Profiles.Select(p => p.Name));
        var added = saved.Profiles[2];
        Assert.Equal("AB1CD-9", added.Callsign);
        // Rotate has no filter text, so the profile gets a source of its own for it (F1.25 behavior 7).
        var source = saved.AprsIsSourceOf(added)!;
        Assert.Equal(("rotate.aprs2.net:14580", "b/AB1CD*"), (source.Server, source.Filter));
        Assert.Equal(2, saved.SourceList.Count);
        Assert.Equal("home", saved.ActiveProfileId); // not switched without the tick
        Assert.Equal(FakeEngine.TwoProfiles().Units, saved.Units);
    }

    [Fact]
    public async Task AddingWithTheTickSwitchesToTheNewProfile()
    {
        var engine = new FakeEngine();
        var wizard = await Wizard(engine, WizardMode.Add);
        wizard.Callsign = "AB1CD-7";
        await ToLastStep(wizard);
        wizard.SwitchToNew = true;

        await wizard.FinishCommand.ExecuteAsync(null);

        var saved = Assert.Single(engine.Saved);
        Assert.Equal(saved.Profiles[2].Id, saved.ActiveProfileId);
        Assert.Equal([FakeEngine.Rotate.Id], saved.Profiles[2].SourceIds); // the same server: the same source
    }

    [Fact]
    public async Task EditingFillsEverythingInAndKeepsTheId()
    {
        var engine = new FakeEngine
        {
            Settings = WithLocation() with
            {
                Profiles =
                [
                    WithLocation().Profiles[0] with
                    {
                        Callsign = "AB1CD", // the wizard refuses the N0CALL placeholder
                        Passcode = 12345,
                        SourceIds = [Filtered.Id],
                        RangeKm = 50,
                    },
                    WithLocation().Profiles[1],
                ],
            },
        };
        var wizard = await Wizard(engine, WizardMode.Edit, engine.Settings.Profiles[0]);

        Assert.Equal("Edit Home", wizard.Title);
        Assert.Equal(("AB1CD", "Home", "r/40/-105/50"), (wizard.Callsign, wizard.ProfileName, wizard.Filter));
        Assert.Equal(("rotate", false), (wizard.SelectedServer?.Key, wizard.UseOwnServer));
        Assert.Equal((false, "12345"), (wizard.ReceiveOnly, wizard.PasscodeText));
        Assert.Equal(40.0625, wizard.Latitude);
        Assert.Equal(50, wizard.SelectedRange?.Km);
        Assert.False(wizard.HasInput); // nothing changed yet

        wizard.ProfileName = "Base";
        wizard.SelectedRange = wizard.Ranges.Single(r => r.Km == 0);
        Assert.Equal("None", wizard.SelectedRange?.Name);
        Assert.True(wizard.HasInput);
        await ToLastStep(wizard);
        await wizard.FinishCommand.ExecuteAsync(null);

        var saved = Assert.Single(engine.Saved);
        Assert.Equal(["Base", "Portable"], saved.Profiles.Select(p => p.Name));
        Assert.Equal(("home", 12345), (saved.Profiles[0].Id, saved.Profiles[0].Passcode));
        Assert.Equal([Filtered.Id], saved.Profiles[0].SourceIds); // its source, unchanged
        Assert.Equal(3, saved.SourceList.Count);
        Assert.Equal("home", saved.ActiveProfileId);
        Assert.Equal(0, saved.Profiles[0].RangeKm);
    }

    [Fact]
    public async Task EditingAProfileOnAnotherServerShowsItAsAnotherServer()
    {
        var engine = new FakeEngine { Settings = WithLocation() };

        var wizard = await Wizard(engine, WizardMode.Edit, engine.Settings.Profiles[1]);

        Assert.True(wizard.UseOwnServer);
        Assert.Equal("t2test.example:10152", wizard.OwnServer);
        Assert.True(wizard.ReceiveOnly);
        Assert.False(wizard.HasLocation);
    }

    [Fact]
    public async Task ADuplicateNameGoesBackToTheNameField()
    {
        var engine = new FakeEngine
        {
            SaveFailure = EngineErrors.Create(
                EngineErrorCodes.ProfileNameDuplicate,
                "English",
                new Dictionary<string, string> { [EngineErrorArgs.Profile] = "Home" }
            ),
        };
        var wizard = await Wizard(engine, WizardMode.Add);
        wizard.Callsign = "AB1CD";
        await ToLastStep(wizard);

        await wizard.FinishCommand.ExecuteAsync(null);

        Assert.Equal(SetupStep.Callsign, wizard.Step);
        Assert.Equal("There is more than one profile named “Home”.", wizard.NameError);
        wizard.ProfileName = "Other";
        Assert.Null(wizard.NameError);
    }

    private static async Task ToLastStep(SetupWizardViewModel wizard)
    {
        for (var i = 0; i < 6 && !wizard.IsLastStep; i++)
        {
            await wizard.NextCommand.ExecuteAsync(null);
        }
        Assert.True(wizard.IsLastStep, $"stuck at {wizard.Step}: {wizard.CallsignError}{wizard.LocationError}");
    }

    [Fact]
    public void AddAndEditNeedTheCurrentSettings()
    {
        var engine = new FakeEngine();
        Assert.Throws<ArgumentNullException>(() => new SetupWizardViewModel(engine, mode: WizardMode.Add));
        Assert.Throws<ArgumentNullException>(() =>
            new SetupWizardViewModel(engine, mode: WizardMode.Edit, existing: engine.Settings)
        );
    }
}
