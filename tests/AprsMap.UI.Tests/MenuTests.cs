// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Packets.Export;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Menus and commands (docs/features/F1.29-menus.md, acceptance tests 1, 3–5, 7).</summary>
[Collection(nameof(LanguageViewTests))]
public sealed class MenuTests
{
    private sealed class OfferingSignIn : AprsMap.UI.Platform.IOperatorSession
    {
        public Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthInfo(false, SignInOffered: true));

        public Task SignInAsync(SignInRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Actions : IMenuActions
    {
        public bool CanQuit => true;

        public bool CanFullScreen => true;

        public bool IsFullScreen => false;

        public List<string> Done { get; } = [];

        public void Quit() => Done.Add("quit");

        public void ToggleFullScreen() => Done.Add("full-screen");

        public Task SavePacketsAsync(PacketExporter exporter)
        {
            Done.Add("save " + exporter.Extension);
            return Task.CompletedTask;
        }

        public Task OpenUriAsync(Uri uri)
        {
            Done.Add(uri.ToString());
            return Task.CompletedTask;
        }
    }

    private static readonly Localizer Text = new(System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    private static IEnumerable<CommandEntry> Commands(IEnumerable<MenuEntry> entries) =>
        entries.SelectMany(e =>
            e switch
            {
                CommandEntry c => [c],
                SubmenuEntry s => Commands(s.Items),
                _ => [],
            }
        );

    private static Dictionary<string, CommandEntry> All(AppMenus menus) =>
        menus.Menus.SelectMany(m => Commands(m.Items())).ToDictionary(c => c.Id);

    private static MainViewModel Desktop(FakeEngine? engine = null)
    {
        engine ??= new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider()) { MainScreen = () => null };
        vm.Show(engine.Settings);
        vm.Show(
            new EngineStatus(
                ConnectionState.Connected,
                "T2TEST",
                0,
                "1.0",
                Verified: true,
                Toggles: StationToggles.AllOn,
                Transmit: new TransmitStatus(false)
            )
        );
        return vm;
    }

    [Fact]
    public void FiveMenusHoldWhatTheAppMenuThePillsAndSettingsOffered()
    {
        var menus = new AppMenus(Desktop(), Text, new Actions());

        Assert.Equal(["_File", "_View", "_Station", "_Tools", "_Help"], menus.Menus.Select(m => m.Header));
        var ids = All(menus).Keys;
        string[] expected =
        [
            "file.settings",
            "file.export-capture",
            "file.quit",
            "file.save-packets.csv",
            "view.panel.packets",
            "view.show-all",
            "view.reset-layout",
            "view.save-layout",
            "view.gather",
            "view.full-screen",
            "view.always-show-menu",
            "station.internet",
            "station.aprs-is",
            "station.tiles",
            "station.transmit",
            "station.reconnect-all",
            "station.send-position",
            "station.destination.set",
            "station.sources",
            "station.beacons",
            "station.console.aprs-is",
            "tools.find",
            "tools.messages",
            "tools.bulletins",
            "tools.clear-data",
            "tools.setup",
            "tools.start-over",
            "help.docs",
            "help.report",
            "help.source",
            "help.about",
        ];
        Assert.All(expected, id => Assert.Contains(id, ids));
        Assert.Equal(AppMenus.SettingsGesture, All(menus)["file.settings"].Gesture);
        Assert.Equal(AppMenus.FindGesture, All(menus)["tools.find"].Gesture);
    }

    [Fact]
    public async Task CommandsThatCantRunNowAreGreyedOutWithTheReason()
    {
        var vm = Desktop();
        var commands = All(new AppMenus(vm, Text, new Actions()));

        Assert.Null(commands["station.transmit"].WhyNot);
        Assert.False(commands["station.transmit"].Checked);
        Assert.Equal("Transmit is off. Turn it on in the status bar.", commands["station.send-position"].WhyNot);
        Assert.Equal("No destination is set.", commands["station.destination.clear"].WhyNot);
        Assert.Equal("The map can't be closed.", commands["view.panel.map"].WhyNot);
        Assert.Equal("Every panel is shown.", commands["view.show-all"].WhyNot);
        Assert.True(commands["station.internet"].Checked);

        // A viewer who could sign in (the operator port): greyed, with why (a pure viewer's are left out, F1.26).
        var viewer = new MainViewModel(new FakeEngine(), new FakeTimeProvider())
        {
            SharedStation = true,
            OperatorSession = new OfferingSignIn(),
        };
        viewer.Show(new FakeEngine().Settings with { ReadOnly = true });
        await viewer.LoadAuthAsync();
        var shared = All(new AppMenus(viewer, Text, new Actions()));
        Assert.Equal("Sign in as this station's operator first.", shared["station.transmit"].WhyNot);
        Assert.Equal("Only this station's operator can change it.", shared["station.internet"].WhyNot);
        Assert.Equal("Only this station's operator can change it.", shared["tools.clear-data"].WhyNot);
    }

    [Fact]
    public void ViewPanelsHidesAndShowsAPanelWhereItWas()
    {
        var vm = Desktop();
        var packets = ShellViewModel
            .Dockables(vm.Shell.Layout)
            .OfType<PanelTool>()
            .Single(p => p.Kind == PanelKinds.Packets);
        vm.Shell.MoveTo(packets, ShellFactory.RightDock); // moved from the bottom

        vm.TogglePanel(PanelKinds.Packets);
        Assert.Contains(PanelKinds.Packets, vm.Shell.ClosedPanels);
        Assert.False(All(new AppMenus(vm, Text, new Actions()))["view.panel.packets"].Checked);

        vm.TogglePanel(PanelKinds.Packets);
        var shown = ShellViewModel
            .Dockables(vm.Shell.Layout)
            .OfType<PanelTool>()
            .Single(p => p.Kind == PanelKinds.Packets);
        Assert.Equal(ShellFactory.RightDock, shown.Owner?.Id); // back where it was, not its default

        vm.TogglePanel(PanelKinds.Stations);
        vm.TogglePanel(PanelKinds.Dashboard);
        vm.ShowAllPanels();
        Assert.Equal([PanelKinds.Gps], vm.Shell.ClosedPanels); // on demand: opened from the GPS pill or by name
    }

    [Fact]
    public void HelpAndFileRunWhatOnlyTheWindowCanDo()
    {
        var actions = new Actions();
        var commands = All(new AppMenus(Desktop(), Text, actions));

        commands["file.quit"].Command.Execute(null);
        commands["view.full-screen"].Command.Execute(null);
        commands["file.save-packets.csv"].Command.Execute(null);
        commands["help.report"].Command.Execute(null);

        Assert.Equal(["quit", "full-screen", "save csv", Brand.SourceUrl.TrimEnd('/') + "/issues/new"], actions.Done);
    }

    [Fact]
    public void ANameWithAnUnderscoreIsShownAsTyped()
    {
        Assert.Equal("Field__day", AppMenus.Literal("Field_day"));
        var items = MenuBuilder.Items([
            SeparatorEntry.Instance,
            new CommandEntry("a", "A", new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }), WhyNot: "Not now."),
            SeparatorEntry.Instance,
            SeparatorEntry.Instance,
            new CommandEntry("b", "B", new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }), Checked: true),
            SeparatorEntry.Instance,
        ]);
        Assert.Equal(3, items.Count); // no separator first, last or twice
        var greyed = Assert.IsType<MenuItem>(items[0]);
        Assert.False(greyed.IsEnabled);
        Assert.Equal("Not now.", ToolTip.GetTip(greyed));
        Assert.True(Assert.IsType<MenuItem>(items[2]).IsChecked);
    }

    private static (Window Window, MainViewModel Vm, Menu Bar) Window(MenuStyle style, double width = 1280)
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider()) { MenuStyle = style };
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = width,
            Height = 800,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, window.GetVisualDescendants().OfType<Menu>().Single(m => m.Name == "MenuBar"));
    }

    [AvaloniaFact]
    public void OnTheDesktopTheMenuBarIsHiddenUntilAltOrF10()
    {
        var (window, vm, bar) = Window(MenuStyle.InWindow);
        Assert.Equal(0, bar.MaxHeight); // hidden, though in the window for Alt
        Assert.Equal(
            ["_File", "_View", "_Station", "_Tools", "_Help"],
            bar.Items.OfType<MenuItem>().Select(i => i.Header)
        );

        window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.MenuBarRevealed);
        Assert.True(double.IsPositiveInfinity(bar.MaxHeight));
        Assert.True(((MenuItem)bar.Items[0]!).IsSubMenuOpen); // File, open

        bar.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.MenuBarRevealed);
        Assert.Equal(0, bar.MaxHeight);

        window.KeyPress(Key.F10, RawInputModifiers.None, PhysicalKey.F10, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.MenuBarRevealed);
        bar.Close();

        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        window.KeyPress(Key.V, RawInputModifiers.Alt, PhysicalKey.V, "v");
        window.KeyRelease(Key.V, RawInputModifiers.Alt, PhysicalKey.V, "v");
        window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.Items.OfType<MenuItem>().Single(i => i.Name == "Menu_view").IsSubMenuOpen); // Alt+V: View
        Assert.True(double.IsPositiveInfinity(bar.MaxHeight)); // with the bar shown
        bar.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, bar.MaxHeight);

        vm.AlwaysShowMenuBar = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(double.IsPositiveInfinity(bar.MaxHeight));
        vm.AlwaysShowMenuBar = false;
        window.Close();
    }

    [AvaloniaFact]
    public void InTheWebAppAltIsTheBrowsersAndTheBarShowsOnlyWhenAskedOnAWideScreen()
    {
        var (window, vm, bar) = Window(MenuStyle.Hamburger);
        Assert.False(bar.IsVisible);

        window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.MenuBarRevealed);
        Assert.False(bar.IsVisible);

        vm.AlwaysShowMenuBar = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsVisible);
        vm.AlwaysShowMenuBar = false;
        window.Close();
    }
}
