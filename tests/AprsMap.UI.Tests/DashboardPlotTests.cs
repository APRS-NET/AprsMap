// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Statistics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AprsMap.UI.Tests;

/// <summary>A tile's series as a large plot (docs/features/F1.22-statistics.md, behavior 6.1; acceptance test 11).</summary>
[Collection(nameof(LanguageViewTests))]
public sealed class DashboardPlotTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => (Application.Current as App)?.Appearance.Apply(new AppearanceSettings());

    private static App App => Assert.IsType<App>(Application.Current);

    private static Dashboard Sample() =>
        GpsDashboardTests.Sample() with
        {
            Packets = [.. Enumerable.Range(0, 60).Select(i => i % 10)],
        };

    private static (Window Window, DashboardPanel Panel, DashboardViewModel Model) Show()
    {
        var vm = GpsDashboardTests.Create(engine: new FakeEngine { Dashboard = Sample() });
        var panel = new DashboardPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 1280,
            Height = 900,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, panel, vm);
    }

    private static Button ExpandOf(DashboardPanel panel, Tile tile) =>
        panel
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(b => b.Classes.Contains("expand-tile") && ReferenceEquals(b.DataContext, tile));

    [Theory]
    [InlineData(0, 9, new[] { 0.0, 5, 10 })]
    [InlineData(0, 0.9, new[] { 0.0, 0.5, 1.0 })]
    [InlineData(0, 2.5, new[] { 0.0, 1, 2, 3 })]
    [InlineData(0, 0, new[] { 0.0, 0.5, 1.0 })]
    public void TheValueAxisHasRoundTicks(double low, double high, double[] expected)
    {
        Assert.Equal(expected, TimePlot.Ticks(low, high).Select(t => Math.Round(t, 6)));
    }

    [AvaloniaFact]
    public void ExpandOpensThePlotAndTheCursorReadsEachPointByKeyboardAndPointer()
    {
        var (window, panel, vm) = Show();
        var expand = ExpandOf(panel, vm.Packets);

        expand.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(vm.Packets, vm.Expanded);
        var plot = panel.FindControl<TimePlot>("Plot")!;
        Assert.True(plot.IsEffectivelyVisible);
        Assert.True(plot.IsFocused);
        Assert.Equal(vm.Packets.PointLabels[^1], plot.Readout); // the newest first
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal(vm.Packets.PointLabels[^2], plot.Readout);
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        Assert.Equal(vm.Packets.PointLabels[0], plot.Readout);
        Assert.Equal(plot.Readout, panel.FindControl<TextBlock>("PlotReadout")!.Text);

        window.CaptureRenderedFrame()?.Dispose(); // lays the axes out
        var right = plot.TranslatePoint(new Point(plot.Bounds.Width - 10, plot.Bounds.Height / 2), window)!.Value;
        window.MouseMove(right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(59, plot.Point);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Expanded);
        Assert.True(expand.IsFocused);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(AccessibilityRenderTests.Combinations), MemberType = typeof(AccessibilityRenderTests))]
    public void ThePlotFitsAtDoubleTextSize(ThemeMode theme, Palette palette)
    {
        App.Appearance.Apply(
            new AppearanceSettings
            {
                Theme = theme,
                Palette = palette,
                TextScale = 2.0,
            }
        );
        var (window, panel, vm) = Show();
        vm.Expanded = vm.GpsDilution;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();

        var plot = panel.FindControl<TimePlot>("Plot")!;
        Assert.True(plot.Bounds.Height >= 200);
        var failures = panel
            .FindControl<Border>("PlotOverlay")!
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Bounds.Width > 0)
            .Where(t =>
                t.TextTrimming == TextTrimming.None
                && t.TextLayout.Width > t.Bounds.Width - t.Padding.Left - t.Padding.Right + 1
            )
            .Select(t => $"“{t.Text}” is cut short")
            .ToList();
        window.Close();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
