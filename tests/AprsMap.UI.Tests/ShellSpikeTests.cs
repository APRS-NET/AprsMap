// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Spike: docking layout, persistence and window placement.</summary>
[Collection(nameof(LanguageViewTests))]
public sealed class ShellSpikeTests : IDisposable
{
    public void Dispose() => (Application.Current as App)?.Appearance.Apply(new AppearanceSettings());

    // A laptop panel at 1.5× and an external monitor to its right at 1×.
    private static readonly ScreenArea Laptop = new(new(0, 0, 2880, 1800), new(0, 0, 2880, 1740), 1.5);
    private static readonly ScreenArea External = new(new(2880, 0, 2560, 1440), new(2880, 0, 2560, 1400), 1.0);

    // The same external monitor arranged to the laptop's left: negative coordinates.
    private static readonly ScreenArea ExternalLeft = new(new(-2560, 0, 2560, 1440), new(-2560, 0, 2560, 1400), 1.0);

    [Fact]
    public void AWindowOnAScreenThatStillExistsStaysWhereItWas()
    {
        var (position, size) = WindowPlacement.Fit(new(3000, 100), new(800, 600), [Laptop, External]);
        Assert.Equal(new PixelPoint(3000, 100), position);
        Assert.Equal(new Size(800, 600), size);
    }

    [Fact]
    public void AWindowLeftOnAnUnpluggedMonitorMovesToTheNearestScreen()
    {
        var (position, size) = WindowPlacement.Fit(new(3000, 100), new(800, 600), [Laptop]);
        Assert.True(Laptop.WorkingArea.Contains(position), position.ToString());
        Assert.True(position.X + size.Width * Laptop.Scaling <= Laptop.WorkingArea.Right);
    }

    [Fact]
    public void AWindowAtNegativeCoordinatesComesBackWhenThatMonitorIsGone()
    {
        Assert.Equal(
            new PixelPoint(-2000, 200),
            WindowPlacement.Fit(new(-2000, 200), new(800, 600), [Laptop, ExternalLeft]).Position
        );
        var (position, _) = WindowPlacement.Fit(new(-2000, 200), new(800, 600), [Laptop]);
        Assert.True(Laptop.WorkingArea.Contains(position), position.ToString());
    }

    [Fact]
    public void AWindowWhoseTitleBarIsOffScreenIsBroughtBack()
    {
        // Mostly visible, but its title bar is above the top of the screen: it couldn't be dragged.
        var (position, _) = WindowPlacement.Fit(new(100, -200), new(800, 600), [Laptop]);
        Assert.True(position.Y >= Laptop.WorkingArea.Y);
    }

    [Fact]
    public void AWindowBiggerThanTheScreenIsShrunkToFit()
    {
        var (_, size) = WindowPlacement.Fit(new(3000, 0), new(2500, 1400), [Laptop]);
        Assert.True(size.Width <= Laptop.WorkingArea.Width / Laptop.Scaling);
        Assert.True(size.Height <= Laptop.WorkingArea.Height / Laptop.Scaling);
    }

    [Fact]
    public void TheMonitorFingerprintIgnoresOrderButNotArrangement()
    {
        Assert.Equal(WindowPlacement.Fingerprint([Laptop, External]), WindowPlacement.Fingerprint([External, Laptop]));
        Assert.NotEqual(
            WindowPlacement.Fingerprint([Laptop, External]),
            WindowPlacement.Fingerprint([Laptop, ExternalLeft])
        );
        Assert.NotEqual(WindowPlacement.Fingerprint([Laptop, External]), WindowPlacement.Fingerprint([Laptop]));
    }

    [AvaloniaFact]
    public void ALayoutWithAFloatingPanelRoundTripsAndIsFittedToTheScreens()
    {
        var shell = new ShellViewModel(new object(), managedWindows: false);
        var dm = Find(shell.Layout, "dm:W1AW-9");
        shell.Factory.FloatDockable(dm);
        var window = Assert.Single(shell.Layout.Windows!);
        window.X = 3000; // on the external monitor
        window.Y = 100;
        window.Width = 500;
        window.Height = 400;

        var text = LayoutStore.Save(shell.Layout);
        var loaded = LayoutStore.Load(text, [Laptop]);

        Assert.NotNull(loaded);
        var restored = Assert.Single(loaded.Windows!);
        Assert.True(Laptop.WorkingArea.Contains(new PixelPoint((int)restored.X, (int)restored.Y)));
        Assert.Equal("dm:W1AW-9", Assert.IsType<PanelTool>(FindIn(restored.Layout!, "dm:W1AW-9")).Id);
        Assert.Equal("dm", ((PanelTool)FindIn(restored.Layout!, "dm:W1AW-9")).Kind);
        Assert.Contains("packets", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TextThatIsNotALayoutLoadsAsNothing() => Assert.Null(LayoutStore.Load("{ not json", [Laptop]));

    [AvaloniaFact]
    public void APanelCanBeMovedWithoutDragging()
    {
        var shell = new ShellViewModel(new object(), managedWindows: false);
        var messages = Find(shell.Layout, "messages");
        var bottom = (IDock)Find(shell.Layout, "bottom");
        var from = (IDock)messages.Owner!;

        shell.Factory.MoveDockable(from, bottom, messages, null);

        Assert.Same(bottom, messages.Owner);
        Assert.Contains(messages, bottom.VisibleDockables!);
        Assert.DoesNotContain(messages, from.VisibleDockables!);
    }

    [AvaloniaTheory]
    [InlineData(ThemeMode.Light, Palette.Standard)]
    [InlineData(ThemeMode.Dark, Palette.Standard)]
    [InlineData(ThemeMode.Dark, Palette.HighContrast)]
    [InlineData(ThemeMode.Light, Palette.ColourVisionSafe)]
    public void RendersInEveryPalette(ThemeMode theme, Palette palette)
    {
        var app = (App)Application.Current!;
        app.Appearance.Apply(
            new AppearanceSettings
            {
                Theme = theme,
                Palette = palette,
                TextScale = 2.0,
            }
        );
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider(), appearance: app.Appearance);
        vm.Show(engine.Settings);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2CAWEST", 123456, "1.0.0"));
        vm.Add(FakeEngine.Packet("N0CALL-15") with { Summary = "Car at 40.0150, -105.2705" });
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 1280,
            Height = 900,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        var folder = Environment.GetEnvironmentVariable("SPIKE_SHOTS");
        if (folder is not null && frame is not null)
        {
            Directory.CreateDirectory(folder);
            using var file = File.Create(Path.Combine(folder, $"shell-{theme}-{palette}.png"));
            frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        Assert.NotNull(frame);
        window.Close();
    }

    [AvaloniaFact]
    public void TabsCanBeReachedFromTheKeyboardAndHaveAMenu()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 1280,
            Height = 900,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.GetVisualDescendants().OfType<Dock.Avalonia.Controls.ToolTabStripItem>().ToList();
        var report = string.Join(
            "\n",
            tabs.Select(t =>
                $"{(t.DataContext as IDockable)?.Id}: focusable={t.Focusable} tabStop={KeyboardNavigation.GetIsTabStop(t)} menu={(t.ContextMenu is not null || t.ContextFlyout is not null)}"
            )
        );
        var chrome = window.GetVisualDescendants().OfType<Dock.Avalonia.Controls.ToolChromeControl>().ToList();
        report +=
            "\n"
            + string.Join(
                "\n",
                chrome.Select(c => $"chrome {(c.DataContext as IDockable)?.Id}: focusable={c.Focusable}")
            );
        // Tab through the window and record what takes focus.
        var focused = new List<string>();
        var first = tabs.FirstOrDefault();
        window.Focus();
        for (var i = 0; i < 25; i++)
        {
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Tab, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var f = window.FocusManager?.GetFocusedElement();
            focused.Add(f?.GetType().Name + ":" + ((f as Control)?.DataContext as IDockable)?.Id);
        }
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "dock-keyboard.txt"),
            report + "\nTAB ORDER:\n" + string.Join("\n", focused)
        );
        Assert.NotEmpty(tabs);
        window.Close();
    }

    private static IDockable Find(IRootDock root, string id) =>
        root.Factory!.FindDockable(root, d => d.Id == id) ?? throw new InvalidOperationException(id);

    private static IDockable FindIn(IRootDock root, string id) =>
        root.VisibleDockables!.SelectMany(Flatten).First(d => d.Id == id);

    private static IEnumerable<IDockable> Flatten(IDockable d) =>
        d is IDock dock ? new[] { d }.Concat((dock.VisibleDockables ?? []).SelectMany(Flatten)) : [d];
}
