// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The receive-only IGate in Settings › Sources (docs/features/F3.4-rx-igate.md, acceptance test 5).</summary>
public sealed class IGateUiTests
{
    private static (SourcesSectionViewModel Section, FakeEngine Engine) Create()
    {
        var engine = new FakeEngine();
        var section = new SourcesSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(engine.Settings);
        return (section, engine);
    }

    [Fact]
    public async Task TheSwitchIsOffByDefaultAndSavesTheActiveProfileAsItsSwitched()
    {
        var (section, engine) = Create();
        Assert.False(section.IGateOn);
        Assert.Equal("IGate for " + engine.Settings.GetActiveProfile()!.Callsign, section.IGateHeading);

        section.IGateOn = true;
        await WaitUntil(() => engine.Settings.GetActiveProfile()!.EffectiveIGate);

        section.IGateOn = false;
        await WaitUntil(() => engine.Settings.GetActiveProfile()!.IGate is null);
    }

    [Fact]
    public void ShowingTheSettingsDoesNotSaveThem()
    {
        var (section, engine) = Create();
        var saved = engine.Settings;
        var active = saved.GetActiveProfile()!;
        section.Show(
            saved with
            {
                Profiles = [.. saved.Profiles.Select(p => p.Id == active.Id ? p with { IGate = true } : p)],
            }
        );

        Assert.True(section.IGateOn);
        Assert.Same(saved, engine.Settings);
    }

    [Fact]
    public void ItSaysWhatItGatedAndWhyItIsHeld()
    {
        var (section, _) = Create();

        section.ShowIGate(new IGateStatus(12, 3));
        Assert.Equal("12 packets gated, 3 not", section.IGateCountText);
        Assert.Null(section.IGateHeldText);

        section.ShowIGate(new IGateStatus(1, 0, new EngineError(EngineErrorCodes.TransmitOff, "Transmit is off.")));
        Assert.Equal("1 packet gated, 0 not", section.IGateCountText);
        Assert.StartsWith("Gating nothing now: ", section.IGateHeldText, StringComparison.Ordinal);

        section.ShowIGate(null); // not an IGate
        Assert.Null(section.IGateCountText);
        Assert.Null(section.IGateHeldText);
    }

    [AvaloniaFact]
    public async Task TheCardShowsInSettingsSourcesAndItsCountsFollowTheStatus()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 1100,
            Height = 900,
        };
        window.Show();
        await vm.OpenSettingsSectionAsync("sources");
        await Settle();
        vm.Settings!.Sources!.IGateOn = true;
        vm.Show(
            engine.Status with
            {
                IGate = new IGateStatus(42, 7, new EngineError(EngineErrorCodes.TransmitOff, "Transmit is off.")),
            }
        );
        await Settle();

        var card = window.GetLogicalDescendants().OfType<Border>().Single(c => c.Name == "IGateCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal("42 packets gated, 7 not", vm.Settings.Sources.IGateCountText);
        window.Close();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
}
