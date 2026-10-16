// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The shell's menus and narrow layout (docs/features/F1.0-shell-and-docking.md, behaviors 4, 6, 7).</summary>
[Collection(nameof(LanguageViewTests))]
public sealed class ShellViewTests
{
    private static (Window Window, MainViewModel Vm) Show(double width = 1280)
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = width,
            Height = 800,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    [AvaloniaFact]
    public void EachPanelsMenuOffersMoveToTheOtherDocksAndTheyWork()
    {
        var (window, vm) = Show();
        var chrome = window
            .GetVisualDescendants()
            .OfType<ToolChromeControl>()
            .First(c => (c.DataContext as Dock.Model.Core.IDock)?.Id == ShellFactory.RightDock);
        var menu = Assert.IsType<MenuFlyout>(chrome.ToolFlyout);
        menu.ShowAt(chrome);
        Dispatcher.UIThread.RunJobs();

        var items = menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header?.ToString()).ToList();
        Assert.Contains("Move to _left", items);
        Assert.Contains("Move to _bottom", items);
        Assert.DoesNotContain("Move to _right", items); // already there
        Assert.DoesNotContain("Dock as tabbed document", items);
        var moveLeft = menu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Move to _left");
        moveLeft.Command!.Execute(moveLeft.CommandParameter);
        menu.Hide();

        var details = ShellViewModel
            .Dockables(vm.Shell.Layout)
            .OfType<PanelTool>()
            .Single(p => p.Kind == PanelKinds.Details);
        Assert.Equal(ShellFactory.LeftDock, details.Owner!.Id);
        window.Close();
    }

    [AvaloniaFact]
    public void TheAppMenuShowsClosedPanelsAndResetsTheLayout()
    {
        var (window, vm) = Show();
        var stations = ShellViewModel
            .Dockables(vm.Shell.Layout)
            .OfType<PanelTool>()
            .Single(p => p.Kind == PanelKinds.Stations);
        vm.Shell.Factory.CloseDockable(stations);
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AppMenuButton");
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);

        Dispatcher.UIThread.RunJobs(); // the menu follows the closed panel
        // A real click, as the desktop gets one; the menu is already filled when it opens.
        var centre = button
            .TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!
            .Value;
        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);
        var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();
        Assert.Equal(["Show Stations", "Reset layout"], headers); // no Gather outside the desktop app
        var show = menu.Items.OfType<MenuItem>().First();
        show.Command!.Execute(show.CommandParameter);
        menu.Hide();

        Assert.Empty(vm.Shell.ClosedPanels);
        window.Close();
    }

    [AvaloniaFact]
    public void ANarrowWindowShowsFixedTabsAndWideningRestoresTheLayout()
    {
        var (window, vm) = Show(width: 600);
        var view = Assert.IsType<MainView>(window.Content);

        Assert.True(view.IsNarrow);
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "NarrowShell");
        Assert.Equal(
            ["Map", "Stations", "Details", "Packets"],
            tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString())
        );

        window.Width = 1280;
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.IsNarrow);
        Assert.Contains(ShellViewModel.Dockables(vm.Shell.Layout), d => d.Id == PanelKinds.Packets);
        window.Close();
    }

    [AvaloniaFact]
    public void DocksOwnTextsFollowTheLanguage()
    {
        var resources = new ResourceDictionary();
        var text = new Localizer(System.Globalization.CultureInfo.GetCultureInfo("en-US"));

        DockStrings.Attach(resources, text);
        Assert.Equal("_Auto hide", resources["ToolChromeControlAutoHideString"]);
        text.SetLanguage(Localizer.PseudoLanguage);

        Assert.Equal(PseudoLocalizer.Transform("_Auto hide"), resources["ToolChromeControlAutoHideString"]);
        Assert.All(DockStrings.Keys, key => Assert.True(Localizer.Exists(key), key));
    }

    [AvaloniaFact]
    public void CtrlTabSwitchesBetweenPanels() =>
        Assert.Equal(
            new Avalonia.Input.KeyGesture(Avalonia.Input.Key.Tab, Avalonia.Input.KeyModifiers.Control),
            Dock.Settings.DockSettings.ToolSelectorKeyGesture
        );
}
