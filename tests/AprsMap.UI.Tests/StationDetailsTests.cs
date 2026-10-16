// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Stations;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class StationDetailsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static (MainViewModel Vm, FakeEngine Engine) Create(UnitSettings? units = null)
    {
        var engine = new FakeEngine();
        if (units is not null)
        {
            engine.Settings = engine.Settings with { Units = units };
        }
        var vm = new MainViewModel(engine, new FakeTimeProvider(Now));
        vm.Show(engine.Settings);
        return (vm, engine);
    }

    [Fact]
    public async Task EveryKnownLineIsWrittenInTheViewersUnits()
    {
        var (vm, engine) = Create();
        engine.Details["N0CALL-9"] = FakeEngine.Detail("N0CALL-9", Now.AddMinutes(-4));

        await vm.Details.ShowAsync("N0CALL-9");

        var d = vm.Details;
        Assert.Equal("Station", d.KindText);
        Assert.Equal("Heard 4 minutes ago · 12 packets", d.HeardText);
        Assert.Equal("40.0150° N, 105.2705° W", d.PositionText);
        Assert.Equal("DN70kb", d.LocatorText);
        Assert.Equal("100 km NE 045°", d.RangeText);
        Assert.Equal("67 km/h · course 088° · 1,609 m altitude", d.MotionText);
        Assert.Equal("Heading home", d.Comment);
        Assert.Equal("Net tonight", d.Status);
        Assert.Equal("Via digipeater N2DIGI, gated by N1GATE", d.ArrivalText);
        Assert.Equal("Yaesu FTM-500D", d.DeviceText);
        Assert.StartsWith("N0CALL-9>APY500", d.LastPacket, StringComparison.Ordinal);
        Assert.Equal("40.01500, -105.27050", d.PositionCopy());
    }

    [Fact]
    public async Task ImperialUnitsAreUsedWhenChosen()
    {
        var (vm, engine) = Create(UnitSettings.Imperial);
        engine.Details["N0CALL-9"] = FakeEngine.Detail("N0CALL-9", Now);

        await vm.Details.ShowAsync("N0CALL-9");

        Assert.Equal("41 mph · course 088° · 5,280 ft altitude", vm.Details.MotionText);
        Assert.Equal("62 mi NE 045°", vm.Details.RangeText);
    }

    [Fact]
    public async Task UnknownLinesAreHiddenAndAnObjectNamesItsOwners()
    {
        var (vm, engine) = Create();
        engine.Details["LEADER"] = new StationDetail(
            "LEADER",
            StationKind.Object,
            Now,
            1,
            false,
            null,
            null,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "N1NEW",
            "N0OLD"
        );

        await vm.Details.ShowAsync("LEADER");

        var d = vm.Details;
        Assert.Equal("Object from N1NEW, before N0OLD", d.KindText);
        Assert.Null(d.PositionText);
        Assert.Null(d.RangeText);
        Assert.Null(d.MotionText);
        Assert.Null(d.ArrivalText);
        Assert.Null(d.DeviceText);
        Assert.False(d.HasPosition);
        Assert.Null(d.PositionCopy());
    }

    [Fact]
    public void ControlCharactersAreShownAsSymbols() =>
        Assert.Equal("a␇b␍␊␡", StationDetailsViewModel.Visible("a\u0007b\r\n\u007f"));

    [Fact]
    public async Task SelectingShowsAChangeRefreshesAndARemovalSaysNoLongerHeard()
    {
        var (vm, engine) = Create();
        engine.Details["N0CALL-9"] = FakeEngine.Detail("N0CALL-9", Now);

        vm.SelectedStation = "N0CALL-9";
        await WaitUntil(() => vm.Details.LastPacket is not null);
        Assert.True(vm.Details.HasStation);

        engine.Details["N0CALL-9"] = FakeEngine.Detail("N0CALL-9", Now) with { Comment = "Moved on" };
        vm.Apply(new StationBatch([FakeEngine.Station("N0CALL-9")], []));
        await WaitUntil(() => vm.Details.Comment == "Moved on");

        vm.Apply(new StationBatch([], ["N0CALL-9"]));
        Assert.True(vm.Details.IsGone);
        Assert.Equal("Moved on", vm.Details.Comment); // the last details stay until closed

        vm.Details.CloseCommand.Execute(null);
        Assert.Null(vm.SelectedStation);
        Assert.True(vm.Details.IsEmpty);
    }

    [Fact]
    public async Task CentreAndFollowMoveTheMap()
    {
        var (vm, engine) = Create();
        engine.Details["N0CALL-9"] = FakeEngine.Detail("N0CALL-9", Now);
        var centred = new List<(double, double)>();
        vm.Details.CentreRequested += (_, at) => centred.Add(at);
        await vm.Details.ShowAsync("N0CALL-9");

        vm.Details.CentreCommand.Execute(null);
        vm.Details.IsFollowing = true;
        engine.Details["N0CALL-9"] = FakeEngine.Detail("N0CALL-9", Now) with { Latitude = 41 };
        await vm.Details.ShowAsync("N0CALL-9");

        Assert.Equal([(40.015, -105.2705), (40.015, -105.2705), (41, -105.2705)], centred);
    }

    [AvaloniaFact]
    public async Task ThePanelShowsTheStationAndEscapeClosesIt()
    {
        var (vm, engine) = Create();
        engine.Details["N0CALL-9"] = FakeEngine.Detail("N0CALL-9", Now);
        var panel = new DetailsPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 400,
            Height = 700,
        };
        window.Show();
        Assert.True(panel.FindControl<TextBlock>("EmptyNote")!.IsVisible);

        vm.SelectedStation = "N0CALL-9";
        await WaitUntil(() => vm.Details.LastPacket is not null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("N0CALL-9", panel.FindControl<SelectableTextBlock>("StationName")!.Text);
        Assert.Equal("Yaesu FTM-500D", panel.FindControl<SelectableTextBlock>("DeviceValue")!.Text);

        panel.FindControl<Button>("CentreButton")!.Focus();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Null(vm.SelectedStation);
        window.Close();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
