// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class MainViewTests
{
    [AvaloniaFact]
    public void ShowsConnectionAndPackets()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        vm.Show(new Contracts.EngineStatus(Contracts.ConnectionState.Connected, "T2TEST", 1, "1.0.0"));
        vm.Add(FakeEngine.Packet("N0CALL-9"));
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 800,
            Height = 600,
        };

        window.Show();

        var label = window.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "ConnectionLabel");
        Assert.Equal("APRS-IS connected · T2TEST", label.Text);
        var list = window.GetLogicalDescendants().OfType<ListBox>().Single(l => l.Name == "PacketList");
        Assert.Equal(1, list.ItemCount);
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
