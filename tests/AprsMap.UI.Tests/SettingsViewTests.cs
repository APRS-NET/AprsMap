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

public class SettingsViewTests
{
    private static T Find<T>(Window window, string name)
        where T : Control => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static (Window Window, MainViewModel ViewModel) Show(double width = 1100, AppSettings? settings = null)
    {
        var engine = new FakeEngine();
        if (settings is not null)
        {
            engine.Settings = settings;
        }
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = width,
            Height = 700,
        };
        window.Show();
        return (window, vm);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task TheGearOpensSettingsAndBackClosesIt()
    {
        var (window, vm) = Show();

        Find<Button>(window, "SettingsButton").Command!.Execute(null);
        await Settle();
        Assert.True(Find<SettingsView>(window, "SettingsPage").IsVisible);
        Assert.True(Find<StackPanel>(window, "ProfilesSection").IsEffectivelyVisible); // first in the list
        Assert.True(window.FocusManager?.GetFocusedElement() is Button); // the first profile's first action

        Find<Button>(window, "SettingsBackButton").Command!.Execute(null);
        await Settle();
        Assert.Null(vm.Settings);
        Assert.False(Find<SettingsView>(window, "SettingsPage").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlCommaOpensSettingsAndEscapeClosesIt()
    {
        var (window, vm) = Show();
        window.Focus();

        window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
        await Settle();
        Assert.NotNull(vm.Settings);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Settle();
        Assert.Null(vm.Settings);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AboutShowsTheEnginesVersions()
    {
        var (window, vm) = Show();
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 1, "1.2.3", AprsNetVersion: "0.1.0-alpha.17"));

        await vm.OpenSettingsCommand.ExecuteAsync(null);
        vm.Settings!.SelectedSection = vm.Settings.Sections.Single(s => s.Id == "about");
        await Settle();

        Assert.True(Find<StackPanel>(window, "AboutSection").IsEffectivelyVisible);
        Assert.Equal("Engine 1.2.3 · APRS.NET 0.1.0-alpha.17", Find<TextBlock>(window, "EngineLabel").Text);
        Assert.True(Find<HyperlinkButton>(window, "LookupCredit").IsVisible); // the fake engine has lookup on
        window.Close();
    }

    [AvaloniaFact]
    public async Task OnANarrowWindowTheSectionsBecomeTabs()
    {
        var (window, vm) = Show(width: 600);

        await vm.OpenSettingsCommand.ExecuteAsync(null);
        await Settle();

        Assert.Contains("narrow", Find<ListBox>(window, "SectionList").Classes);
        window.Width = 1100;
        await Settle();
        Assert.DoesNotContain("narrow", Find<ListBox>(window, "SectionList").Classes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AProfileAddedFromSettingsShowsInTheListAndTheStatusBar()
    {
        var (window, vm) = Show();
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        await Settle();
        Assert.Equal(2, Find<ItemsControl>(window, "ProfileList").ItemCount);

        Find<Button>(window, "AddProfileButton").Command!.Execute(null);
        for (var i = 0; i < 50 && vm.Setup is null; i++)
        {
            await Task.Yield();
        }
        await Settle();
        var wizard = vm.Setup!;
        Assert.Equal(WizardMode.Add, wizard.Mode);
        Assert.True(Find<SetupWizardView>(window, "SetupWizard").IsVisible); // over Settings
        Assert.True(Find<SettingsView>(window, "SettingsPage").IsVisible);
        Assert.True(Find<TextBox>(window, "ProfileNameBox").IsEffectivelyVisible);

        wizard.Callsign = "AB1CD-7";
        wizard.ProfileName = "Hiking";
        for (var i = 0; i < 6 && !wizard.IsLastStep; i++)
        {
            await wizard.NextCommand.ExecuteAsync(null);
        }
        await wizard.FinishCommand.ExecuteAsync(null);
        for (var i = 0; i < 50 && Find<ItemsControl>(window, "ProfileList").ItemCount < 3; i++)
        {
            await Settle();
        }

        Assert.Null(vm.Setup);
        Assert.Equal(3, Find<ItemsControl>(window, "ProfileList").ItemCount);
        Assert.Equal(["Home", "Portable", "Hiking"], vm.Profiles.Select(p => p.Name)); // the status bar picker
        window.Close();
    }

    [AvaloniaFact]
    public async Task AfterADeleteUndoHasFocus()
    {
        var (window, vm) = Show();
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        await Settle();

        await vm.Settings!.Profiles!.Rows[1].DeleteCommand.ExecuteAsync(null);
        await Settle();

        Assert.Equal("UndoDeleteButton", (window.FocusManager?.GetFocusedElement() as Button)?.Name);
        await vm.Settings.Profiles.UndoDeleteCommand.ExecuteAsync(null);
        await Settle();
        Assert.Equal("AddProfileButton", (window.FocusManager?.GetFocusedElement() as Button)?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsWaitsWhileTheSetupWizardIsOpen()
    {
        var (window, vm) = Show(settings: AppSettings.Empty);
        for (var i = 0; i < 50 && vm.Setup is null; i++)
        {
            await Task.Yield();
        }

        Assert.NotNull(vm.Setup);
        Assert.False(vm.OpenSettingsCommand.CanExecute(null));
        Assert.False(Find<Button>(window, "SettingsButton").IsEffectivelyEnabled);
        window.Close();
    }
}
