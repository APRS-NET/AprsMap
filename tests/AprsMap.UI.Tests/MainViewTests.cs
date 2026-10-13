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
        vm.Show(new(Contracts.ConnectionState.Connected, "T2TEST", 1, "1.0.0"));
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
}
