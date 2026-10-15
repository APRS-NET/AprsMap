// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class SetupWizardViewTests
{
    private static T Find<T>(Window window, string name)
        where T : Control => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static async Task<(Window Window, MainViewModel ViewModel, FakeEngine Engine)> ShowNotSetUp(
        AppSettings? settings = null
    )
    {
        var engine = new FakeEngine { Settings = settings ?? AppSettings.Empty };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 1000,
            Height = 700,
        };
        window.Show();
        vm.Show(engine.Settings);
        for (var i = 0; i < 50 && vm.Setup is null; i++)
        {
            await Task.Yield();
        }
        Dispatcher.UIThread.RunJobs();
        return (window, vm, engine);
    }

    private static void Type(Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        // Commands run asynchronously; let them and the focus moves they cause finish.
        for (var i = 0; i < 10; i++)
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task WithoutAProfileTheWizardCoversTheMainView()
    {
        var (window, vm, _) = await ShowNotSetUp();

        var wizard = Find<SetupWizardView>(window, "SetupWizard");
        Assert.True(wizard.IsVisible);
        Assert.True(Find<StackPanel>(window, "CallsignStep").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(window, "LocationStep").IsEffectivelyVisible);
        Assert.Equal("Step 1 of 4", Find<TextBlock>(window, "StepLabel").Text);
        Assert.False(Find<RadioButton>(window, "DeviceChoice").IsVisible); // no device location on this platform
        Assert.False(vm.CanOpenSetup);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheWholeWizardWorksFromTheKeyboard()
    {
        var (window, vm, engine) = await ShowNotSetUp();
        Assert.True(Find<TextBox>(window, "CallsignBox").IsFocused);

        Type(window, "ab1cd-9");
        await Press(window, PhysicalKey.Enter);
        Assert.Equal(SetupStep.Location, vm.Setup!.Step);
        Assert.True(Find<TextBox>(window, "LocatorBox").IsFocused);

        Type(window, "fn31pr");
        await Press(window, PhysicalKey.Enter); // reads the locator and moves on
        Assert.Equal(SetupStep.Server, vm.Setup.Step);

        await Press(window, PhysicalKey.Enter);
        Assert.Equal(SetupStep.Passcode, vm.Setup.Step);

        await Press(window, PhysicalKey.Enter); // Finish: receive only
        var profile = Assert.Single(Assert.Single(engine.Saved).Profiles);
        Assert.Equal("AB1CD-9", profile.Callsign);
        Assert.Equal(41.7291667, profile.Latitude);
        Assert.Equal(-1, profile.Passcode);
        Assert.Null(vm.Setup);
        Assert.False(Find<SetupWizardView>(window, "SetupWizard").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheCallsignFieldShowsUpperCaseAsItIsTyped()
    {
        var (window, vm, _) = await ShowNotSetUp();

        Type(window, "ab1 cd-9");

        Assert.Equal("AB1CD-9", Find<TextBox>(window, "CallsignBox").Text);
        Assert.Equal(7, Find<TextBox>(window, "CallsignBox").CaretIndex);
        Assert.Equal("AB1CD-9", vm.Setup!.Callsign);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EscapeAsksBeforeDroppingWhatWasTyped()
    {
        var (window, vm, _) = await ShowNotSetUp();
        Type(window, "AB1CD");

        await Press(window, PhysicalKey.Escape);
        Assert.True(Find<Border>(window, "ConfirmClose").IsVisible);
        Assert.True(Find<Button>(window, "KeepEditingButton").IsFocused);

        await Press(window, PhysicalKey.Escape);
        Assert.Null(vm.Setup);
        Assert.True(vm.CanOpenSetup);
        Assert.True(Find<Button>(window, "OpenSetupButton").IsVisible);

        await vm.OpenSetupCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Setup);
        Assert.False(vm.CanOpenSetup);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AReadOnlyGatewayShowsANoteInsteadOfTheWizard()
    {
        var (window, vm, _) = await ShowNotSetUp(AppSettings.Empty with { ReadOnly = true });

        Assert.Null(vm.Setup);
        Assert.False(vm.CanOpenSetup);
        Assert.True(Find<TextBlock>(window, "GatewaySetupNote").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithAProfileThereIsNoWizard()
    {
        var (window, vm, _) = await ShowNotSetUp(FakeEngine.TwoProfiles());

        Assert.Null(vm.Setup);
        Assert.False(Find<SetupWizardView>(window, "SetupWizard").IsVisible);
        Assert.False(Find<TextBlock>(window, "GatewaySetupNote").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheCalculatorOffersCopyWhereThereIsAClipboard()
    {
        var (window, vm, _) = await ShowNotSetUp();
        var wizard = vm.Setup!;
        wizard.Callsign = "AB1CD";
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.HasPasscode = true;
        wizard.OpenCalculatorCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<Border>(window, "Calculator").IsVisible);
        Assert.True(Find<CheckBox>(window, "LicensedBox").IsFocused);
        Assert.False(Find<Button>(window, "CalculateButton").IsEffectivelyEnabled);
        wizard.IsLicensed = true;
        await wizard.CalculateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("5000", Find<SelectableTextBlock>(window, "CalculatedLabel").Text);
        Assert.Equal(wizard.CopyToClipboard is not null, Find<Button>(window, "CopyButton").IsVisible);
        Assert.Equal(string.Empty, Find<TextBox>(window, "PasscodeBox").Text);
        window.Close();
    }
}
