// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Reset the app (docs/features/F0.10-start-over.md).</summary>
public sealed class StartOverTests
{
    [AvaloniaFact]
    public async Task OnTheDesktopResetDeletesEverythingAndOpensSetup()
    {
        var engine = new FakeEngine();
        var appearance = new AppearanceService();
        appearance.Apply(new AppearanceSettings { Theme = ThemeMode.Dark });
        var cleared = 0;
        var vm = new MainViewModel(engine, new FakeTimeProvider(), appearance: appearance)
        {
            ClearLocalData = () =>
            {
                cleared++;
                return Task.CompletedTask;
            },
        };
        vm.Show(engine.Settings);
        vm.PacketList.Receive(FakeEngine.Packet("N0CALL"));
        await vm.OpenSettingsAsync();
        var data = Assert.IsType<DataSectionViewModel>(vm.Settings!.Data);
        Assert.False(data.ShowStationChoice);

        data.StartCommand.Execute(null);
        Assert.True(data.IsConfirming);
        await data.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.Resets);
        Assert.Equal(1, cleared);
        Assert.Null(vm.Settings);
        Assert.Empty(vm.Packets);
        Assert.NotNull(vm.Setup); // first-run setup opens again
    }

    [AvaloniaFact]
    public async Task InTheBrowserTheStationIsKeptUnlessAskedAndThePageReloads()
    {
        var engine = new FakeEngine();
        var restarts = 0;
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            SharedStation = true,
            ClearLocalData = () => Task.CompletedTask,
            Restart = () => restarts++,
        };
        vm.Show(engine.Settings);
        await vm.OpenSettingsAsync();
        var data = vm.Settings!.Data!;
        Assert.True(data.ShowStationChoice); // this browser may change settings

        data.StartCommand.Execute(null);
        await data.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(0, engine.Resets);
        Assert.Equal(1, restarts);

        await vm.OpenSettingsAsync();
        vm.Settings!.Data!.StartCommand.Execute(null);
        vm.Settings.Data.IncludeStation = true;
        await vm.Settings.Data.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, engine.Resets);
    }

    [AvaloniaFact]
    public async Task OnAReadOnlyGatewayOnlyThisBrowsersDataCanGo()
    {
        var engine = new FakeEngine { Settings = FakeEngine.TwoProfiles() with { ReadOnly = true } };
        var vm = new MainViewModel(engine, new FakeTimeProvider()) { SharedStation = true };
        vm.Show(engine.Settings);
        await vm.OpenSettingsAsync();

        Assert.False(vm.Settings!.Data!.ShowStationChoice);
    }

    [Fact]
    public async Task CancelDeletesNothingAndAFailureIsShown()
    {
        var text = new Localizer(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var calls = 0;
        var data = new DataSectionViewModel(
            text,
            sharedStation: false,
            canResetStation: true,
            _ =>
            {
                calls++;
                throw EngineErrors.Create(EngineErrorCodes.SettingsReadOnly, "read only");
            }
        );

        data.StartCommand.Execute(null);
        data.CancelCommand.Execute(null);
        Assert.False(data.IsConfirming);
        Assert.Equal(0, calls);

        data.StartCommand.Execute(null);
        await data.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
        Assert.True(data.IsConfirming);
        Assert.NotNull(data.ErrorText);
    }
}
