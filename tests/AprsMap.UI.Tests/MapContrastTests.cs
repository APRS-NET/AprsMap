// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Map;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The map's contrast button (#345, docs/features/F1.4-map.md behavior 13).</summary>
public sealed class MapContrastTests
{
    private static (MainViewModel Vm, AppearanceService Appearance) Create()
    {
        var appearance = new AppearanceService();
        return (new MainViewModel(new FakeEngine(), new FakeTimeProvider(), appearance: appearance), appearance);
    }

    [AvaloniaFact]
    public void TheSliderFadesTheMapAndResetFollowsTheThemeAgain()
    {
        var (vm, appearance) = Create();
        Assert.Equal(0, vm.MapFadePercent); // the light theme's own: none
        Assert.Equal("Map fade: 0 % (follows the theme)", vm.MapFadeText);

        vm.MapFadePercent = 50;
        Assert.Equal(0.5, appearance.Current.MapFade);
        Assert.Equal("Map fade: 50 %", vm.MapFadeText);

        vm.MapFadePercent = 95; // past the most the map can take
        Assert.Equal(AppearanceSettings.MapFadeMax, appearance.Current.MapFade);

        vm.MapGreyscale = true;
        vm.SymbolBacking = true;
        Assert.True(appearance.Current is { MapGreyscale: true, SymbolBacking: true });

        vm.ResetMapContrastCommand.Execute(null);
        Assert.Equal(new AppearanceSettings(), appearance.Current);
    }

    [AvaloniaFact]
    public void SettingsMapChangesTheSameThingsTheButtonShows()
    {
        var (vm, appearance) = Create();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        appearance.Apply(appearance.Current with { MapGreyscale = true, MapFade = 0.35 });

        Assert.True(vm.MapGreyscale);
        Assert.Equal(35, vm.MapFadePercent);
        Assert.Contains(nameof(MainViewModel.MapFadePercent), changed);
        Assert.Contains(nameof(MainViewModel.MapGreyscale), changed);
    }

    [Fact]
    public void TheOldBrightnessStepsBecomeFades()
    {
        Assert.Equal(0.6, AppearancePreferences.Read("""{ "version": 1, "mapBrightness": "Dark" }""")!.MapFade);
        Assert.Equal(0.35, AppearancePreferences.Read("""{ "version": 1, "mapBrightness": "Dimmed" }""")!.MapFade);
        Assert.Equal(0d, AppearancePreferences.Read("""{ "version": 1, "mapBrightness": "Normal" }""")!.MapFade);
        Assert.Null(AppearancePreferences.Read("""{ "version": 1, "mapBrightness": "Auto" }""")!.MapFade);
        Assert.Equal(0.8, AppearancePreferences.Read("""{ "version": 1, "mapFade": 3 }""")!.MapFade); // clamped
    }

    [AvaloniaFact]
    public void TheBackingIsDrawnAroundTheSymbolOnlyWhenOn()
    {
        using var view = new MapView();
        var window = new Window
        {
            Content = view,
            Width = 400,
            Height = 300,
        };
        window.Show();
        view.SetStations([new StationState("N0CALL-9", DateTimeOffset.UtcNow, 40, -105, "/>")]);
        view.ShowAround(40, -105, 20);
        Dispatcher.UIThread.RunJobs();
        var at = view.ScreenPoint(40, -105);
        var beside = new PixelPoint((int)(at.X + 13), (int)at.Y); // inside the backing, outside a 24 px symbol

        var without = PixelAt(window, beside);
        view.SymbolBacking = true;
        view.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        var with = PixelAt(window, beside);

        Assert.NotEqual(without, with);
        window.Close();
    }

    private static uint PixelAt(Window window, PixelPoint at)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame.");
        var pixel = Marshal.AllocHGlobal(4);
        try
        {
            frame.CopyPixels(new PixelRect(at, new PixelSize(1, 1)), pixel, 4, 4);
            return (uint)Marshal.ReadInt32(pixel);
        }
        finally
        {
            Marshal.FreeHGlobal(pixel);
        }
    }
}
