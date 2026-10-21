// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Map;
using AprsMap.UI.ViewModels;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The map's Clear stations button (docs/features/F1.19-housekeeping.md, behavior 1).</summary>
public sealed class ClearStationsButtonTests
{
    private static MainViewModel Create(FakeEngine engine)
    {
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0") { Stations = 82 });
        return vm;
    }

    [Fact]
    public async Task ItAsksWithTheCountThenClearsEveryStation()
    {
        var engine = new FakeEngine();
        var vm = Create(engine);

        vm.AskClearStations();
        Assert.Equal(
            "Clear all 82 stations from the map, with their tracks? They come back as they're heard again.",
            vm.ClearStationsQuestion
        );
        await vm.ClearStationsCommand.ExecuteAsync(null);

        Assert.Equal([ClearScope.Stations], engine.Cleared);
    }

    [Fact]
    public async Task ARefusedClearSaysWhy()
    {
        var engine = new FakeEngine { ClearFailure = new UnauthorizedAccessException("Operator sign-in needed.") };
        var vm = Create(engine);

        await vm.ClearStationsCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorText);
        Assert.Empty(engine.Cleared);
    }

    [AvaloniaFact]
    public void TheButtonOpensTheQuestionAndIsHiddenFromReadOnlyViewers()
    {
        var engine = new FakeEngine();
        var vm = Create(engine);
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 800,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ClearStationsButton");
        Assert.True(button.IsVisible);

        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("Clear all 82 stations", vm.ClearStationsQuestion, StringComparison.Ordinal);
        button.Flyout.Hide();

        vm.Show(engine.Settings with { ReadOnly = true });
        Dispatcher.UIThread.RunJobs();
        Assert.False(button.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ConfirmingInTheQuestionClearsAndClosesIt()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(null, null, new FakeTimeProvider()),
        };
        vm.Show(engine.Settings);
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 800,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var open = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ClearStationsButton");
        var question = Assert.IsType<Flyout>(open.Flyout);
        question.ShowAt(open);
        Dispatcher.UIThread.RunJobs();
        var confirm = ((Control)question.Content!)
            .GetLogicalDescendants()
            .OfType<Button>()
            .First(b => b.Name == "ConfirmClearStationsButton");

        // As a click does: the Click handlers, then the command.
        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(confirm)).Invoke();
        for (var i = 0; i < 10; i++)
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal([ClearScope.Stations], engine.Cleared); // closing the question first used to lose the command
        Assert.False(question.IsOpen);
        window.Close();
    }
}
