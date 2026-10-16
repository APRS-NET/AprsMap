// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Quick toggles in the UI (docs/features/F1.15-quick-toggles.md, behaviors 1–6).</summary>
public sealed class ToggleTests
{
    [Fact]
    public async Task ATogglesMenuItemFlipsItAndThePillSaysItIsOff()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0", Toggles: StationToggles.AllOn));
        Assert.True(vm.CanToggle);
        Assert.Empty(vm.ToggleMarkers);

        await vm.ToggleCommand.ExecuteAsync(Toggle.AprsIs);

        Assert.Equal([(Toggle.AprsIs, false)], engine.Toggled);
        Assert.False(vm.AprsIsOn);
        Assert.Equal("APRS-IS off", vm.ConnectionText); // its own pill says so, at once
        Assert.Empty(vm.ToggleMarkers); // no second pill for it
        Assert.Equal("APRS-IS off", vm.ToggleAnnouncement);
    }

    [Fact]
    public async Task TheAprsIsSwitchFlipsTheSameToggleAsTheMenu()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0", Toggles: StationToggles.AllOn));
        Assert.True(vm.AprsIsSwitch.IsOn);
        Assert.True(vm.AprsIsSwitch.CanChange);

        vm.AprsIsSwitch.IsOn = false;
        await WaitUntil(() => !vm.AprsIsOn);

        Assert.Equal([(Toggle.AprsIs, false)], engine.Toggled);
        Assert.Equal("APRS-IS off", vm.ConnectionText);
        Assert.Empty(vm.ToggleMarkers);
        await vm.ToggleCommand.ExecuteAsync(Toggle.AprsIs); // the menu turns it back on: the switch follows
        Assert.True(vm.AprsIsSwitch.IsOn);
    }

    [Fact]
    public async Task ARefusedSwitchSpringsBack()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0", Toggles: StationToggles.AllOn));
        engine.ToggleFailure = new UnauthorizedAccessException("Operator sign-in needed.");

        vm.AprsIsSwitch.IsOn = false;
        await WaitUntil(() => vm.AprsIsSwitch.IsOn);

        Assert.True(vm.AprsIsOn);
        Assert.Empty(engine.Toggled);
    }

    [Fact]
    public void WithTheInternetOffTheAprsIsSwitchIsHeldOff()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        vm.Show(
            new EngineStatus(
                ConnectionState.Disconnected,
                null,
                0,
                "1.0",
                Toggles: new StationToggles(Internet: false, AprsIs: true)
            )
        );

        Assert.False(vm.AprsIsSwitch.CanChange);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Yield();
        }
        Assert.True(condition());
    }

    [Fact]
    public void WithTheInternetOffItsDependentsCantBeChanged()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        vm.Show(
            new EngineStatus(ConnectionState.Disconnected, null, 0, "1.0", Toggles: new StationToggles(Internet: false))
        );

        Assert.False(vm.CanToggleDependents);
        Assert.Equal(["Internet off"], vm.ToggleMarkers);
    }

    [Fact]
    public void AReadOnlyViewerSeesTheTogglesButCantChangeThem()
    {
        var engine = new FakeEngine { Settings = FakeEngine.TwoProfiles() with { ReadOnly = true } };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        vm.Show(
            new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0", Toggles: new StationToggles(Tiles: false))
        );

        Assert.False(vm.CanToggle);
        Assert.Equal(["Map tiles off"], vm.ToggleMarkers);
    }
}
