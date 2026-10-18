// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>A panel as a tab beside the map (docs/features/F1.0-shell-and-docking.md, behavior 16).</summary>
[Collection(nameof(LanguageViewTests))]
public sealed class BesideMapTests
{
    private static string? DockOf(ShellViewModel shell, string kind) =>
        ShellViewModel.Dockables(shell.Layout).OfType<PanelTool>().SingleOrDefault(p => p.Kind == kind)?.Owner?.Id;

    [Fact]
    public void APanelMovesBesideTheMapAndBackToItsSide()
    {
        var shell = new ShellViewModel(new object(), AprsMap.UI.Localization.Localizer.Instance, managedWindows: true);
        var messages = ShellViewModel
            .Dockables(shell.Layout)
            .OfType<PanelTool>()
            .Single(p => p.Kind == PanelKinds.Messages);
        shell.MoveTo(messages, ShellFactory.LeftDock);

        shell.Factory.MoveBesideMap.Execute(messages);
        Assert.Equal(ShellFactory.CentreDock, DockOf(shell, PanelKinds.Messages));
        Assert.Equal([PanelKinds.Messages], shell.BesideMap);
        Assert.Contains(
            ShellViewModel.Dockables(shell.Layout).OfType<MapDocument>(),
            m => m.Owner?.Id == ShellFactory.CentreDock
        );

        shell.SetBesideMap(PanelKinds.Messages, false);
        Assert.Equal(ShellFactory.LeftDock, DockOf(shell, PanelKinds.Messages)); // the side it came from
    }

    [Fact]
    public void BesideTheMapIsKeptWithTheLayoutAndAClosedOneComesBackThere()
    {
        var shell = new ShellViewModel(new object(), AprsMap.UI.Localization.Localizer.Instance, managedWindows: true);
        shell.SetBesideMap(PanelKinds.Packets, true);
        var saved = shell.Save();

        var again = new ShellViewModel(new object(), AprsMap.UI.Localization.Localizer.Instance, managedWindows: true);
        Assert.Equal(LayoutRestore.Restored, again.Restore(saved, []));
        Assert.Equal(ShellFactory.CentreDock, DockOf(again, PanelKinds.Packets));

        again.Close(PanelKinds.Packets);
        again.Show(PanelKinds.Packets);
        Assert.Equal(ShellFactory.CentreDock, DockOf(again, PanelKinds.Packets));
    }

    [Fact]
    public async Task MovingAPanelIsntClosingIt()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        await vm.LoadCloseNoteAsync();

        vm.Shell.SetBesideMap(PanelKinds.Messages, true);
        vm.Shell.SetBesideMap(PanelKinds.Messages, false);
        var packets = ShellViewModel
            .Dockables(vm.Shell.Layout)
            .OfType<PanelTool>()
            .Single(p => p.Kind == PanelKinds.Packets);
        vm.Shell.MoveTo(packets, ShellFactory.LeftDock);

        Assert.Null(vm.LayoutNotice); // no "Closed …" note for a move
    }

    [Fact]
    public void ViewBesideTheMapTicksThePanelsThere()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        vm.Shell.SetBesideMap(PanelKinds.Bulletins, true);
        var menus = new AppMenus(vm, AprsMap.UI.Localization.Localizer.Instance, new NoActions());

        var view = menus.Menus.Single(m => m.Id == "view").Items();
        var beside = view.OfType<SubmenuEntry>()
            .Single(s => s.Header == "Beside the map")
            .Items.OfType<CommandEntry>()
            .ToList();

        Assert.DoesNotContain(beside, c => c.Id is "view.beside-map.map" or "view.beside-map.dashboard");
        Assert.True(beside.Single(c => c.Id == "view.beside-map.bulletins").Checked);
        Assert.False(beside.Single(c => c.Id == "view.beside-map.packets").Checked);
        beside.Single(c => c.Id == "view.beside-map.bulletins").Command.Execute(null);
        Assert.Empty(vm.Shell.BesideMap);
    }

    [AvaloniaFact]
    public void APanelBesideTheMapShowsAsATabWithItsContent()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        var window = new Window
        {
            Width = 1200,
            Height = 760,
            Content = new MainView { DataContext = vm },
        };
        window.Show();
        vm.Shell.SetBesideMap(PanelKinds.Messages, true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.GetVisualDescendants().OfType<MessagesPanel>().Single().IsEffectivelyVisible);
        window.Close();
    }

    private sealed class NoActions : IMenuActions
    {
        public bool CanQuit => false;

        public bool CanFullScreen => false;

        public bool IsFullScreen => false;

        public void Quit() { }

        public void ToggleFullScreen() { }

        public Task SavePacketsAsync(AprsMap.UI.Packets.Export.PacketExporter exporter) => Task.CompletedTask;

        public Task OpenUriAsync(Uri uri) => Task.CompletedTask;
    }
}
