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
    public void ResumingThePacketListScrollsBackToTheNewest()
    {
        var (window, vm) = Show();
        for (var i = 0; i < 200; i++)
        {
            vm.PacketList.Receive(FakeEngine.Packet($"N{i}"));
        }
        vm.PacketList.Flush();
        Dispatcher.UIThread.RunJobs();
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PacketList");
        var scroller = Assert.IsType<ScrollViewer>(list.Scroll);

        Wheel(window, list, -10);
        Assert.True(vm.PacketList.IsPaused); // scrolled away from the newest
        Assert.True(scroller.Offset.Y > 1);

        vm.PacketList.TogglePause();
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.PacketList.IsPaused);
        Assert.Equal(0, scroller.Offset.Y);
        window.Close();
    }

    [AvaloniaFact]
    public void ThePacketListMovingByItselfStaysLiveAtTheTop()
    {
        var (window, vm) = ShowPackets();
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PacketList");
        var scroller = Assert.IsType<ScrollViewer>(list.Scroll);

        // Not the viewer: rows going in above a focused row, or the window losing focus, move the list.
        scroller.Offset = new Vector(0, 400);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.PacketList.IsPaused);
        Assert.Equal(0, scroller.Offset.Y);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingAPacketPausesTheListAndScrollingBackToTheTopResumesIt()
    {
        var (window, vm) = ShowPackets();
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PacketList");
        var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        var centre = row.TranslatePoint(new Avalonia.Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;

        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.PacketList.IsPaused);

        Wheel(window, list, -10);
        Wheel(window, list, 50);
        Assert.False(vm.PacketList.IsPaused);
        window.Close();
    }

    private static (Window Window, MainViewModel Vm) ShowPackets()
    {
        var (window, vm) = Show();
        for (var i = 0; i < 200; i++)
        {
            vm.PacketList.Receive(FakeEngine.Packet($"N{i}"));
        }
        vm.PacketList.Flush();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static void Wheel(Window window, Control over, double notches)
    {
        var centre = over.TranslatePoint(
            new Avalonia.Point(over.Bounds.Width / 2, over.Bounds.Height / 2),
            window
        )!.Value;
        window.MouseWheel(centre, new Vector(0, notches));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheAppMenuHoldsTheMenusAndViewPanelsShowsAClosedPanelAgain()
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
        Assert.Equal(["_File", "_View", "_Station", "_Tools", "_Help"], headers); // F1.29: the menus, under ☰
        var view = menu.Items.OfType<MenuItem>().Single(i => i.Name == "Menu_view");
        var panels = view.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Panels");
        var show = panels.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Stations");
        Assert.False(show.IsChecked); // closed: unticked
        Assert.Contains(view.Items.OfType<MenuItem>(), i => i.Header?.ToString() == "Reset layout…"); // it asks first (F1.29)
        Assert.DoesNotContain(view.Items.OfType<MenuItem>(), i => i.Header?.ToString() == "Gather windows"); // desktop only
        show.Command!.Execute(show.CommandParameter);
        menu.Hide();

        Assert.Empty(vm.Shell.ClosedPanels);
        window.Close();
    }

    [AvaloniaFact]
    public void SaveOpensAMenuOfFileFormats()
    {
        var (window, _) = Show();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveButton");
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);

        var centre = button
            .TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!
            .Value;
        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(menu.IsOpen);
        Assert.Equal(
            [
                "Text log (.txt)",
                "CSV for spreadsheets (.csv)",
                "JSON (.json)",
                "JSON, indented (.json)",
                "YAML (.yaml)",
            ],
            menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString())
        );
        menu.Hide();
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
            [
                "Map",
                "Dashboard",
                "Stations",
                "Details",
                "Messages",
                "Bulletins",
                "Packets",
                "Console",
                "Captured packets",
            ],
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
