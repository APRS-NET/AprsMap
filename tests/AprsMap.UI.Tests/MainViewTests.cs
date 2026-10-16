// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class MainViewTests
{
    [AvaloniaFact]
    public void ShowsConnectionAndPackets()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        vm.Show(new Contracts.EngineStatus(Contracts.ConnectionState.Connected, "T2TEST", 1, "1.0.0"));
        vm.PacketList.Receive(FakeEngine.Packet("N0CALL-9"));
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 800,
            Height = 600,
        };

        window.Show();

        var label = window.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ConnectionLabel");
        Assert.Equal("APRS-IS connected", label.Text);
        // The Packets panel is built by the shell, after the window's first layout pass.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PacketList");
        Assert.Equal(1, list.ItemCount);
        window.Close();
    }

    [AvaloniaFact]
    public void ThePanelsFadeWhileTheEngineIsAway()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider()) { SharedStation = true };
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 800,
            Height = 600,
        };
        window.Show();
        var dock = window.GetVisualDescendants().OfType<Dock.Avalonia.Controls.DockControl>().Single();
        Assert.Equal(1, dock.Opacity);

        vm.MarkEngineAway();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(window.TryFindResource("Opacity.Away", out var faded));
        Assert.Equal(faded, dock.Opacity);
        window.Close();
    }

    [AvaloniaFact]
    public void ADuplicateRowIsMuted()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        vm.PacketList.Receive(FakeEngine.Packet("N0DUP") with { Markers = Contracts.PacketMarkers.Duplicate });
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 800,
            Height = 600,
        };

        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var source = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "N0DUP");
        Assert.True(window.TryFindResource("Brush.TextMuted", window.ActualThemeVariant, out var muted));
        Assert.Contains("duplicate", source.Classes);
        Assert.Equal(muted, source.Foreground);
        window.Close();
    }

    [AvaloniaFact]
    public void TheConnectionOffersReconnectNowAndShowsItsDetails()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(engine.Status with { Verified = true, Reconnects = 1 });
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 800,
            Height = 600,
        };
        window.Show();
        var button = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConnectionButton");

        Assert.Equal("Server T2TEST · login verified · reconnected once", ToolTip.GetTip(button));
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        flyout.ShowAt(button);
        var items = flyout.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["ReconnectItem", "InternetToggle", "AprsIsToggle", "TilesToggle"], items.Select(i => i.Name));
        var item = items[0];
        Assert.Same(vm.ReconnectCommand, item.Command);
        item.Command!.Execute(null);
        Assert.Equal(1, engine.Reconnects);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void ShowsTheProfilePickerOnlyOnceThereAreProfiles()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        var window = new Window { Content = new MainView { DataContext = vm } };
        window.Show();
        var picker = window.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "ProfilePicker");

        Assert.False(picker.IsVisible);
        vm.Show(FakeEngine.TwoProfiles());
        Assert.True(picker.IsVisible);
        Assert.True(picker.IsEnabled);
        Assert.Equal(2, picker.ItemCount);
        Assert.Equal("home", (picker.SelectedItem as Contracts.StationProfile)?.Id);
        window.Close();
    }

    [AvaloniaFact]
    public void ShowsAnEngineErrorOnlyWhileThereIsOne()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        var window = new Window { Content = new MainView { DataContext = vm } };
        window.Show();
        var error = window.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ErrorLabel");

        Assert.False(error.IsVisible);
        vm.ErrorText = "Gateway unreachable";
        Assert.True(error.IsVisible);
        Assert.Equal("Gateway unreachable", error.Text);
        vm.ErrorText = null;
        Assert.False(error.IsVisible);
        window.Close();
    }
}
