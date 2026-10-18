// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>What left Settings for the menus, and the panels' safety nets (docs/features/F1.29-menus.md, behaviors 9–17).</summary>
public sealed class MenuDialogTests
{
    private static readonly Localizer Text = new(System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    private static MainViewModel Create()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        return vm;
    }

    private static string? DockOf(MainViewModel vm, string kind) =>
        ShellViewModel.Dockables(vm.Shell.Layout).OfType<PanelTool>().SingleOrDefault(p => p.Kind == kind)?.Owner?.Id;

    [Fact]
    public void ResetLayoutAsksFirstAndCanBeUndoneOnce()
    {
        var vm = Create();
        var packets = ShellViewModel
            .Dockables(vm.Shell.Layout)
            .OfType<PanelTool>()
            .Single(p => p.Kind == PanelKinds.Packets);
        vm.Shell.MoveTo(packets, ShellFactory.RightDock);

        vm.AskResetLayout();
        var question = Assert.IsType<ConfirmViewModel>(vm.Dialog!.Content);
        Assert.StartsWith("Put every panel back where it started?", question.Text, StringComparison.Ordinal);
        question.CancelCommand.Execute(null);
        Assert.Null(vm.Dialog);
        Assert.Equal(ShellFactory.RightDock, DockOf(vm, PanelKinds.Packets)); // Cancel changed nothing

        vm.AskResetLayout();
        ((ConfirmViewModel)vm.Dialog!.Content).ConfirmCommand.Execute(null);
        Assert.Null(vm.Dialog);
        Assert.Equal(ShellFactory.BottomDock, DockOf(vm, PanelKinds.Packets));
        Assert.True(vm.CanUndoResetLayout);

        vm.UndoResetLayout();
        Assert.Equal(ShellFactory.RightDock, DockOf(vm, PanelKinds.Packets));
        Assert.False(vm.CanUndoResetLayout); // once
    }

    [Fact]
    public void ALayoutChangeAfterAResetEndsItsUndo()
    {
        var vm = Create();
        vm.AskResetLayout();
        ((ConfirmViewModel)vm.Dialog!.Content).ConfirmCommand.Execute(null);
        Assert.True(vm.CanUndoResetLayout);

        vm.TogglePanel(PanelKinds.Messages);

        Assert.False(vm.CanUndoResetLayout);
    }

    [Fact]
    public async Task TheFirstPanelClosedFromItsCrossSaysWhereToGetItBackOnce()
    {
        var vm = Create();
        await vm.LoadCloseNoteAsync();

        vm.TogglePanel(PanelKinds.Bulletins); // from the menu: it knows
        Assert.Null(vm.LayoutNotice);

        vm.Shell.Close(PanelKinds.Packets); // as the panel's own × does
        Assert.Equal("Closed Packets. Show it again from View › Panels.", vm.LayoutNotice);

        vm.LayoutNotice = null;
        vm.Shell.Close(PanelKinds.Stations);
        Assert.Null(vm.LayoutNotice); // not again
    }

    [Fact]
    public async Task StartOverIsADialogAskingWhatGoes()
    {
        var restarts = 0;
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider()) { Restart = () => restarts++ };
        vm.Show(engine.Settings);

        vm.OpenStartOver();
        var data = Assert.IsType<DataSectionViewModel>(vm.Dialog!.Content);
        Assert.Equal("Start over", vm.Dialog.Title);
        Assert.True(data.IsConfirming);
        await data.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task SaveLayoutAsIsADialogThatClosesOnceSaved()
    {
        var vm = Create();

        vm.OpenSaveLayoutAs();
        var layouts = Assert.IsType<SavedLayoutsViewModel>(vm.Dialog!.Content);
        await layouts.SaveAsCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Dialog); // no name: it says so and stays
        Assert.NotNull(layouts.Error);

        layouts.NewName = "Field day";
        await layouts.SaveAsCommand.ExecuteAsync(null);

        Assert.Null(vm.Dialog);
        Assert.Equal(["Field day"], vm.Layouts.Layouts.Select(l => l.Name));
    }

    [Fact]
    public void KeyboardShortcutsListEveryShortcutMenuByMenu()
    {
        var vm = Create();
        var menus = new AppMenus(vm, Text, new NoActions());

        var rows = menus.Shortcuts();

        Assert.Contains(rows, r => r is { Menu: "File", Command: "Settings" });
        Assert.Contains(rows, r => r is { Menu: "Tools", Command: "Find a station" });
        Assert.Contains(rows, r => r is { Menu: "View", Command: "Full screen", Keys: "F11" });
        Assert.Contains(rows, r => r is { Menu: "Anywhere", Command: "Show the menu bar", Keys: "Alt, F10" });

        Assert.Contains(
            menus.Menus.Single(m => m.Id == "help").Items(),
            e => e is CommandEntry { Id: "help.shortcuts" }
        );
        vm.ShowShortcuts(rows);
        Assert.Equal("Keyboard shortcuts", vm.Dialog!.Title);
    }

    [Fact]
    public async Task SettingsKeepsOnlyChoices()
    {
        var vm = Create();

        await vm.OpenSettingsAsync();

        Assert.DoesNotContain(vm.Settings!.Sections, s => s.Id == "about");
        Assert.Contains(vm.Settings.Sections, s => s.Id == "data"); // packet capture stays, for the operator
    }

    private sealed class NoActions : IMenuActions
    {
        public bool CanQuit => true;

        public bool CanFullScreen => true;

        public bool IsFullScreen => false;

        public void Quit() { }

        public void ToggleFullScreen() { }

        public Task SavePacketsAsync(AprsMap.UI.Packets.Export.PacketExporter exporter) => Task.CompletedTask;

        public Task OpenUriAsync(Uri uri) => Task.CompletedTask;
    }
}
