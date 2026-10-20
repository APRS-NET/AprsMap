// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Objects in Settings › Objects and from the map (docs/features/F2.7-objects-and-items.md).</summary>
public sealed class ObjectsUiTests
{
    private static readonly SourceSettings AprsIs = FakeEngine.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    private static readonly OwnObject Net = new(
        "net",
        "NETCTL",
        OwnObjectKind.Object,
        49.0583,
        -72.0292,
        Comment: "Net 7pm"
    );

    private static AppSettings Home(params OwnObject[] objects) =>
        new(
            [
                new StationProfile("home", "Home", "N0TEST", 12345, [AprsIs.Id, Tnc.Id], 49.0, -72.0) with
                {
                    Licensed = true,
                    PositionConfirmed = true,
                    Objects = objects.Length > 0 ? objects : null,
                },
            ],
            "home",
            UnitSettings.Metric,
            Sources: [AprsIs, Tnc]
        );

    private static (ObjectsSectionViewModel Section, FakeEngine Engine) Create(AppSettings settings)
    {
        var engine = new FakeEngine { Settings = settings };
        var section = new ObjectsSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(settings);
        return (section, engine);
    }

    [Fact]
    public async Task AddingOnePlacesItWithItsDetailsAndSendViaTicked()
    {
        var (section, engine) = Create(Home());
        Assert.Equal("Objects for Home", section.Heading);
        Assert.NotNull(section.EmptyText);

        section.AddCommand.Execute(null);
        Assert.True(section.IsEditing);
        Assert.Equal(OwnObject.DefaultSymbol, section.SelectedSymbol!.Symbol); // red X
        Assert.Equal("30 minutes", section.SelectedInterval!.Name);
        section.Name = " NETCTL ";
        section.CoordinatesText = "49.0583, -72.0292";
        section.Comment = "Net 7pm";
        section.Via[1].IsChecked = false;
        await section.SaveObjectCommand.ExecuteAsync(null);

        Assert.False(section.IsEditing);
        var placed = Assert.Single(engine.Settings.GetActiveProfile()!.ObjectList);
        Assert.Equal(
            ("NETCTL", OwnObjectKind.Object, 49.0583, -72.0292, "Net 7pm"),
            (placed.Name, placed.Kind, placed.Latitude, placed.Longitude, placed.Comment)
        );
        Assert.Equal([AprsIs.Id], placed.SourceIds);
        var row = Assert.Single(section.Rows);
        Assert.Equal(("NETCTL", "Object", "49.0583, -72.0292 · Net 7pm"), (row.Name, row.State, row.Details));
        Assert.Null(section.EmptyText);
    }

    [Fact]
    public async Task UnreadableCoordinatesAndRefusalsShowWhy()
    {
        var (section, engine) = Create(Home());
        section.AddCommand.Execute(null);
        section.Name = "NET";
        section.CoordinatesText = "somewhere";

        await section.SaveObjectCommand.ExecuteAsync(null);
        Assert.NotNull(section.Error);
        Assert.Empty(engine.ObjectCalls);

        section.CoordinatesText = "49, -72";
        engine.PlaceFailure = new EngineArgumentException(
            new EngineError(EngineErrorCodes.ObjectNameTaken, "Two objects are named 'NET'.")
        );
        await section.SaveObjectCommand.ExecuteAsync(null);

        Assert.Equal("Another object in this profile has that name.", section.Error);
        Assert.True(section.IsEditing);
    }

    [Fact]
    public async Task ChangingKillingAndForgettingGoToTheEngine()
    {
        var (section, engine) = Create(Home(Net, Net with { Id = "aid", Name = "AID-1", Kind = OwnObjectKind.Item }));

        section.EditCommand.Execute(section.Rows[0]);
        Assert.Equal("Change NETCTL", section.FormHeading);
        Assert.Equal("49.0583, -72.0292", section.CoordinatesText);
        section.CoordinatesText = "49.1, -72";
        await section.SaveObjectCommand.ExecuteAsync(null);
        Assert.Equal(49.1, engine.Settings.GetActiveProfile()!.ObjectList.Single(o => o.Id == "net").Latitude);

        await section.KillCommand.ExecuteAsync(section.Rows.Single(r => r.Name == "NETCTL"));
        var killed = section.Rows.Single(r => r.Name == "NETCTL");
        Assert.False(killed.IsLive);
        Assert.StartsWith("Being killed", killed.State, StringComparison.Ordinal);
        await section.ForgetCommand.ExecuteAsync(section.Rows.Single(r => r.Name == "AID-1"));

        Assert.Equal(["place NETCTL", "kill net", "forget aid"], engine.ObjectCalls);
    }

    [Fact]
    public void TheEmergencySymbolIsNeverOffered()
    {
        Assert.DoesNotContain(ObjectsSectionViewModel.Symbols, s => s.Symbol == "\\!");
    }

    [AvaloniaFact]
    public async Task FromTheMapPlaceOpensTheFormThereAndMoveSendsTheNewPlace()
    {
        var engine = new FakeEngine { Settings = Home(Net) };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        Assert.True(vm.CanPlaceObjects);
        Assert.Equal(["NETCTL"], vm.LiveObjects.Select(o => o.Name));

        await vm.PlaceObjectAtAsync(49.5, -72.25);
        Assert.True(vm.Settings!.IsObjectsSection);
        Assert.True(vm.Settings.Objects!.IsEditing);
        Assert.Equal("49.5, -72.25", vm.Settings.Objects.CoordinatesText);

        await vm.MoveObjectAsync("net", 49.2, -72.1);
        var moved = Assert.Single(engine.Settings.GetActiveProfile()!.ObjectList);
        Assert.Equal((49.2, -72.1), (moved.Latitude, moved.Longitude));
    }
}
