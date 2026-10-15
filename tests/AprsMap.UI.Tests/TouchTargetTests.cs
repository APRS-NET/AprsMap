// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Appearance;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Changes the app's density, so these run alone, after the parallel tests.</summary>
[Collection(nameof(LanguageViewTests))]
public sealed class TouchTargetTests : IDisposable
{
    public void Dispose() => Assert.IsType<App>(Application.Current).Appearance.Apply(new AppearanceSettings());

    private static async Task<Window> ShowSettings(bool touch)
    {
        var app = Assert.IsType<App>(Application.Current);
        app.Appearance.Apply(new AppearanceSettings { Density = Density.Compact });
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider(), appearance: app.Appearance);
        vm.Show(engine.Settings);
        var view = new MainView { DataContext = vm };
        if (touch)
        {
            view.Classes.Add(App.TouchClass);
        }
        var window = new Window
        {
            Content = view,
            Width = 1100,
            Height = 900,
        };
        window.Show();
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static List<Control> Targets(Window window) =>
        [
            .. window
                .GetLogicalDescendants()
                .OfType<Control>()
                .Where(c =>
                    c.IsEffectivelyVisible && c.Bounds.Width > 0 && c is Button or ComboBox or TextBox or ListBoxItem
                ),
        ];

    [AvaloniaFact]
    public async Task OnATouchScreenEveryTargetIsAtLeast44PxEvenWhenCompact()
    {
        var window = await ShowSettings(touch: true);

        var targets = Targets(window);
        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.True(t.Bounds.Height >= 44, $"{t.GetType().Name} {t.Name}: {t.Bounds.Height}"));
        Assert.All(
            targets.OfType<Button>().Where(b => b is not ToggleButton),
            b => Assert.True(b.Bounds.Width >= 44, $"{b.Name}: {b.Bounds.Width}")
        );
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithAMouseCompactStaysCompact()
    {
        var window = await ShowSettings(touch: false);

        Assert.Contains(Targets(window), t => t.Bounds.Height < 44);
        window.Close();
    }
}
