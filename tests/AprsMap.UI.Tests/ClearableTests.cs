// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The clear button in search and filter boxes (#420).</summary>
public sealed class ClearableTests
{
    private static (Window Window, TextBox Box) Show()
    {
        var box = new TextBox();
        Clearable.SetIsOn(box, true);
        var window = new Window
        {
            Content = box,
            Width = 300,
            Height = 80,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, box);
    }

    private static Button ClearButton(TextBox box) => Assert.IsType<Button>(box.InnerRightContent);

    [AvaloniaFact]
    public void TheCrossShowsWhileThereIsTextAndEmptiesTheBox()
    {
        var (window, box) = Show();
        var clear = ClearButton(box);
        Assert.False(clear.IsVisible);
        Assert.Equal("Clear search", AutomationProperties.GetName(clear));

        box.Text = "w1aw";
        Assert.True(clear.IsVisible);
        clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(string.Empty, box.Text);
        Assert.False(clear.IsVisible);
        Assert.True(box.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void EscapeClearsABoxWithTextAndOtherwiseGoesOn()
    {
        var (window, box) = Show();
        box.Text = "w1aw";
        box.Focus();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(string.Empty, box.Text);

        var escapes = 0;
        window.KeyDown += (_, e) => escapes += e.Key == Key.Escape ? 1 : 0;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(1, escapes); // an empty box doesn't swallow it
        window.Close();
    }

    [AvaloniaFact]
    public void TheStationsSearchClearsItsFilter()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 1000,
            Height = 700,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var search = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "SearchBox");
        search.Text = "w1a";
        Assert.Equal("w1a", vm.StationList.Search);

        ClearButton(search).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(string.Empty, vm.StationList.Search);
        window.Close();
    }
}
