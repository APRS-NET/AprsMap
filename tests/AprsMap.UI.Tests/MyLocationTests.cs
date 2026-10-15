// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Map;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class MyLocationTests
{
    private static AppSettings Located(AppSettings settings, double? lat, double? lon) =>
        settings with
        {
            Profiles =
            [
                .. settings.Profiles.Select(p =>
                    p.Id == settings.ActiveProfileId ? p with { Latitude = lat, Longitude = lon } : p
                ),
            ],
        };

    [Fact]
    public async Task SettingMyLocationSavesTheActiveProfileAndUndoPutsItBack()
    {
        var engine = new FakeEngine();
        engine.Settings = Located(engine.Settings, 40, -105);
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        await vm.SetMyLocationAsync(41.123456789, -104.5);

        var active = engine.Saved[^1].GetActiveProfile()!;
        Assert.Equal((41.12346, -104.5), (active.Latitude, active.Longitude));
        Assert.All(
            engine.Saved[^1].Profiles.Where(p => p.Id != active.Id),
            p => Assert.Contains(p, engine.Settings.Profiles)
        );
        Assert.Equal("My location is set here.", vm.MyLocationNotice);
        Assert.True(vm.CanUndoMyLocation);

        await vm.UndoMyLocationCommand.ExecuteAsync(null);

        active = engine.Saved[^1].GetActiveProfile()!;
        Assert.Equal((40d, -105d), (active.Latitude, active.Longitude));
        Assert.False(vm.CanUndoMyLocation);
    }

    [Fact]
    public async Task UndoIsOfferedForTenSeconds()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time);
        vm.Show(engine.Settings);

        await vm.SetMyLocationAsync(40, -105);
        time.Advance(MainViewModel.LocationUndoWindow - TimeSpan.FromSeconds(1));
        Assert.True(vm.CanUndoMyLocation);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(vm.CanUndoMyLocation);
    }

    [Fact]
    public async Task AReadOnlyGatewayDoesNotOfferIt()
    {
        var engine = new FakeEngine();
        engine.Settings = engine.Settings with { ReadOnly = true };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        Assert.False(vm.CanSetMyLocation);
        await vm.SetMyLocationAsync(40, -105);
        Assert.Empty(engine.Saved);
    }

    [Fact]
    public void WithoutAProfileThereIsNothingToSet()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        vm.Show(AppSettings.Empty);

        Assert.False(vm.CanSetMyLocation);
    }

    [AvaloniaFact]
    public async Task TheHouseIsDrawnAtMyLocationWithTheCallsignAndSwitchingProfileMovesIt()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(null, null, new FakeTimeProvider()),
        };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 800,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.Show(Located(engine.Settings, null, null));
        Assert.Null(panel.View.Me);

        var located = Located(engine.Settings, 40, -105);
        vm.Show(located);
        var me = panel.View.Me!;
        Assert.Equal(located.GetActiveProfile()!.Callsign, me.Name);
        Assert.Equal("/-", me.Symbol.ToString());
        Assert.False(me.IsStale);

        var other = located.Profiles.First(p => p.Id != located.ActiveProfileId);
        vm.Show(located with { ActiveProfileId = other.Id });
        Assert.Equal(other.Latitude is null ? null : other.Callsign, panel.View.Me?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RightClickOffersSetMyLocationHereForThePlaceClicked()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(null, null, new FakeTimeProvider()),
        };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        vm.Show(Located(engine.Settings, 40, -105));
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 800,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var at = panel.View.ScreenPoint(40.5, -104.5);
        var inWindow = panel.View.TranslatePoint(at, window)!.Value;

        window.MouseDown(inWindow, MouseButton.Right);
        window.MouseUp(inWindow, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        var menu = (ContextMenu)panel.Resources["MapMenu"]!;
        Assert.True(menu.IsOpen);
        var item = menu.GetLogicalDescendants().OfType<MenuItem>().Single(i => i.Name == "SetMyLocationItem");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => engine.Saved.Count > 0);
        var active = engine.Saved[^1].GetActiveProfile()!;
        Assert.Equal(40.5, active.Latitude!.Value, 2);
        Assert.Equal(-104.5, active.Longitude!.Value, 2);
        window.Close();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }

    [AvaloniaFact]
    public void APlaceOnScreenIsReadBackAsTheSameCoordinates()
    {
        using var map = new MapView();
        var window = new Window
        {
            Content = map,
            Width = 800,
            Height = 600,
        };
        window.Show();
        map.ShowAround(40, -105, 150);
        Dispatcher.UIThread.RunJobs();

        var (lat, lon) = map.PlaceAt(map.ScreenPoint(40.5, -104.25));

        Assert.Equal(40.5, lat, 3);
        Assert.Equal(-104.25, lon, 3);
        window.Close();
    }
}
