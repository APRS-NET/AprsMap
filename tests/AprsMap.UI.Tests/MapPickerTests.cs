// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Map;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The wizard's map picker (docs/features/F0.5-first-run-wizard.md, behavior 9.5).</summary>
public sealed class MapPickerTests
{
    [Fact]
    public void ThePickerIsOfferedOnlyWithAMap()
    {
        Assert.False(new SetupWizardViewModel(new FakeEngine()).CanPickOnMap);

        using var map = new MapViewModel(null, null, new FakeTimeProvider());
        Assert.True(new SetupWizardViewModel(new FakeEngine()) { Map = map }.CanPickOnMap);
    }

    [Fact]
    public async Task UsingAPointConfirmsItAsTheLocation()
    {
        using var map = new MapViewModel(null, null, new FakeTimeProvider());
        var wizard = new SetupWizardViewModel(new FakeEngine()) { Map = map };

        Assert.True(await wizard.UseMapPointAsync(40.0149856, -105.2705456));

        Assert.Equal(40.01499, wizard.Latitude);
        Assert.Equal(-105.27055, wizard.Longitude);
        Assert.True(wizard.HasLocation);
    }

    [AvaloniaFact]
    public async Task PickingTheMapShowsAMapUnderACrosshairAndUseConfirmsItsCentre()
    {
        using var map = new MapViewModel(new MapTests.FakeTiles(), null, new FakeTimeProvider());
        await map.LoadAsync(TestContext.Current.CancellationToken);
        var wizard = new SetupWizardViewModel(new FakeEngine()) { Map = map };
        using var view = new SetupWizardView { DataContext = wizard };
        var window = new Window
        {
            Content = view,
            Width = 1000,
            Height = 800,
        };
        window.Show();

        wizard.Step = SetupStep.Location;
        wizard.IsMapMethod = true;
        Dispatcher.UIThread.RunJobs();
        var picker = window.GetLogicalDescendants().OfType<MapView>().Single();
        picker.GoTo(40, -105, 100);
        Dispatcher.UIThread.RunJobs();
        var use = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "UseMapPointButton");
        use.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 20 && !wizard.HasLocation; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(40, wizard.Latitude!.Value, 3);
        Assert.Equal(-105, wizard.Longitude!.Value, 3);
        window.Close();
    }
}
