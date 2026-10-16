// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Statistics;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The dashboard tab's numbers (docs/features/F1.22-statistics.md, behaviors 2–5, 9).</summary>
public sealed class DashboardViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static int[] Points(params int[] last) => [.. Enumerable.Repeat(0, 60 - last.Length), .. last];

    private static Dashboard Sample() =>
        FakeEngine.EmptyDashboard() with
        {
            At = T0,
            Start = T0.AddMinutes(-59),
            HistoryStart = T0.AddMinutes(-30), // counting began within the hour shown
            Packets = Points(40, 60),
            PacketsPerMinute = 12.5,
            StationsHeard = Points(10, 12),
            StationsHeardTotal = 15,
            NewStations = Points(2, 1),
            NewStationsTotal = 3,
            Newest = [new("N0NEW-9", T0.AddMinutes(-1)), new("N0NEW-7", T0.AddMinutes(-2))],
            Arrivals =
            [
                new(nameof(ArrivalKind.Direct), Points(10, 20)),
                new(nameof(ArrivalKind.Digipeated), Points(10, 10)),
                new(nameof(ArrivalKind.Internet), Points(20, 30)),
                new(nameof(ArrivalKind.Unknown), Points(0, 0)),
            ],
            Kinds = [new(nameof(PacketKind.Position), 75), new(nameof(PacketKind.Weather), 25)],
            Problems = Points(0, 2),
            ProblemsTotal = 2,
            BusiestStations = [new("N0CAR-9", 20), new("N0HOUSE", 5)],
            BusiestRelays = [new("N0GATE", 30)],
            Sources = [new("aprs-is", T0.AddMinutes(-3))],
        };

    private sealed class Harness
    {
        public FakeEngine Engine { get; } = new();

        public FakeTimeProvider Time { get; } = new(T0);

        public EngineStatus? Status { get; set; }

        public List<string> Selected { get; } = [];

        public int ProblemsShown { get; private set; }

        public SourceSwitch Switch { get; } = new(_ => Task.CompletedTask);

        public DashboardViewModel Create() =>
            new(
                Engine,
                Time,
                new Localizer(CultureInfo.GetCultureInfo("en-US")),
                () => Status,
                new SourceNames(() => null),
                Switch,
                Selected.Add,
                () => ProblemsShown++
            );
    }

    [Fact]
    public void TilesWriteEveryNumberOutAndReadAsASentence()
    {
        var harness = new Harness
        {
            Status = new EngineStatus(ConnectionState.Connected, null, 0, "1") { Stations = 42 },
        };
        var vm = harness.Create();

        vm.Show(Sample());

        Assert.False(vm.IsWaiting);
        Assert.Equal("Stations heard", vm.Stations.Title);
        Assert.Equal("15", vm.Stations.Number);
        Assert.Equal("42 stations on the map now", vm.Stations.Detail);
        Assert.Equal(60, vm.Stations.Series.Count);
        Assert.Equal("100", vm.Packets.Number);
        Assert.Equal("12.5 a minute now", vm.Packets.Detail);
        Assert.Equal("Packets received, Last hour: 100. 12.5 a minute now", vm.Packets.Spoken);
        Assert.Equal("3", vm.NewStations.Number);
        Assert.Equal(["N0NEW-9", "N0NEW-7"], vm.Newest.Select(n => n.Name));
        Assert.StartsWith("Counting since", vm.HistoryText, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrivalsAndTypesAreSharesOfThePackets()
    {
        var vm = new Harness().Create();

        vm.Show(Sample());

        Assert.Equal("50 %", vm.Arrivals.Number); // 50 of 100 heard on RF
        Assert.Equal(
            ["Heard directly", "Through digipeaters", "Internet only"], // Unknown left out when none
            vm.ArrivalRows.Select(r => r.Name)
        );
        Assert.Equal("30 · 30 %", vm.ArrivalRows[0].Value);
        Assert.Equal([50.0, 50.0], vm.Arrivals.Series.TakeLast(2));
        Assert.Equal(["Positions", "Weather"], vm.Kinds.Select(k => k.Name));
        Assert.Equal(0.75, vm.Kinds[0].Fraction);
    }

    [Fact]
    public void BusyRowsSelectTheStationAndProblemsOpenThePacketList()
    {
        var harness = new Harness();
        var vm = harness.Create();
        vm.Show(Sample());

        vm.BusiestStations[1].Open!.Execute(null);
        vm.BusiestRelays[0].Open!.Execute(null);
        vm.Newest[0].Open!.Execute(null);
        vm.ShowProblemsCommand.Execute(null);

        Assert.Equal(["N0HOUSE", "N0GATE", "N0NEW-9"], harness.Selected);
        Assert.Equal(1, harness.ProblemsShown);
        Assert.Equal("20 packets", vm.BusiestStations[0].Value);
        Assert.Equal(0.25, vm.BusiestStations[1].Fraction); // against the busiest
    }

    [Fact]
    public void AConnectedSourceSilentForOverTwoMinutesIsQuietInWords()
    {
        var harness = new Harness
        {
            Status = new EngineStatus(ConnectionState.Connected, null, 0, "1")
            {
                Reconnects = 2,
                Sources = [new SourceStatus("aprs-is", "noam.aprs2.net", SourceKind.AprsIs, ConnectionState.Connected)],
            },
        };
        var vm = harness.Create();

        vm.Show(Sample()); // last packet 3 minutes ago

        var row = Assert.Single(vm.Connections);
        Assert.Equal("noam.aprs2.net", row.Name);
        Assert.Same(harness.Switch, row.Switch); // APRS-IS has the pill's switch
        Assert.Equal("Connected", row.State);
        Assert.True(row.IsQuiet);
        Assert.StartsWith("Quiet: no packet since", row.Last, StringComparison.Ordinal);
        Assert.Equal("Reconnected 2 times since the app started", vm.ReconnectsText);

        vm.Show(Sample() with { Sources = [new("aprs-is", T0.AddSeconds(-30))] });
        Assert.False(vm.Connections[0].IsQuiet);
        Assert.StartsWith("Last packet at", vm.Connections[0].Last, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingYetSaysItsWaiting()
    {
        var vm = new Harness().Create();

        vm.Show(FakeEngine.EmptyDashboard());

        Assert.True(vm.IsWaiting);
        Assert.Equal("0", vm.Packets.Number);
        Assert.DoesNotContain(vm.ArrivalRows, r => r.Fraction > 0);
    }

    [Fact]
    public async Task ItAsksEveryTenSecondsOnlyWhileShownAndAgainWhenTheRangeChanges()
    {
        var harness = new Harness();
        var vm = harness.Create();

        vm.Start();
        await WaitUntil(() => harness.Engine.DashboardRanges.Count == 1);
        harness.Time.Advance(DashboardViewModel.RefreshInterval);
        await WaitUntil(() => harness.Engine.DashboardRanges.Count == 2);

        vm.Range = DashboardRange.Day;
        await WaitUntil(() => harness.Engine.DashboardRanges.Count == 3);
        Assert.Equal(DashboardRange.Day, harness.Engine.DashboardRanges[^1]);
        Assert.True(vm.Ranges[2].IsSelected);

        vm.Stop();
        harness.Time.Advance(DashboardViewModel.RefreshInterval * 3);
        await Task.Yield();
        Assert.Equal(3, harness.Engine.DashboardRanges.Count);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void TablesSortByCountOrNameCopyAsTabsAndRowsApplyTheirFilter()
    {
        var used = new List<StationFilter>();
        var vm = new DashboardViewModel(
            new FakeEngine(),
            new FakeTimeProvider(T0),
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            () => null,
            new SourceNames(() => null),
            new SourceSwitch(_ => Task.CompletedTask),
            _ => { },
            () => { },
            () => UnitSettings.Metric with { Distance = DistanceUnit.Miles, Temperature = TemperatureUnit.Fahrenheit },
            used.Add
        );

        vm.Show(
            Sample() with
            {
                FarthestDirect = [new("BASHOR", "KC0QBU", 38.2, T0)],
                Weather = new DashboardWeather(2, new("N0COLD", -10), new("N0WARM", 30), null),
                Tables = new DashboardTables(
                    20,
                    [new("Tracker", "Byonics", "TinyTrack", 5), new(null, null, null, 15)],
                    [new("/>", "Car", "Vehicles", 4), new("/_", "Weather station", "Weather", 16)],
                    [new(nameof(StationKind.Station), 19), new(nameof(StationKind.Object), 1)],
                    [new(nameof(ArrivalKind.Internet), 12), new(nameof(ArrivalKind.Direct), 7)]
                ),
            }
        );

        Assert.Equal("24 mi to KC0QBU", vm.FarthestDirect[0].Value);
        Assert.Equal(["N0COLD", "N0WARM"], vm.WeatherExtremes.Select(r => r.Name));
        Assert.Equal("Coldest 14 °F", vm.WeatherExtremes[0].Value);
        Assert.Equal(["Unknown device", "Byonics TinyTrack"], vm.Platforms.Rows.Select(r => r.Name)); // most first
        Assert.Equal("5 stations", vm.Platforms.Rows[1].CountText);
        Assert.Equal("25 %", vm.Platforms.Rows[1].ShareText);
        Assert.Null(vm.Platforms.Rows[0].Apply); // no class to filter by

        vm.Symbols.SortByNameCommand.Execute(null);
        Assert.Equal(["Car", "Weather station"], vm.Symbols.Rows.Select(r => r.Name));
        Assert.Equal(
            "Symbols\tClass or category\tStations\tShare (%)\nCar\tVehicles\t4\t20\nWeather station\tWeather\t16\t80\n",
            vm.Symbols.CopyText()
        );

        vm.Platforms.Rows[1].Apply!.Execute(null);
        vm.Symbols.Rows[0].Apply!.Execute(null);
        vm.StationKindsTable.Rows[1].Apply!.Execute(null);
        vm.Reception.Rows[1].Apply!.Execute(null);
        Assert.Equal(["Tracker"], used[0].DeviceClasses!);
        Assert.Equal(["Vehicles"], used[1].Categories!);
        Assert.Equal(StationKinds.ObjectsAndItems, used[2].Kinds);
        Assert.Equal(Reception.Direct, used[3].Reception);
    }

    [Fact]
    public void ARowsFilterTurnsTheViewFilterOnAndShowsTheMap()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider(T0));
        vm.Shell.Show(AprsMap.UI.Shell.PanelKinds.Dashboard);

        vm.Dashboard.Show(
            Sample() with
            {
                Tables = new DashboardTables(5, [new("Tracker", "Byonics", "TinyTrack", 5)], [], [], []),
            }
        );
        vm.Dashboard.Platforms.Rows[0].Apply!.Execute(null);

        Assert.True(vm.ViewFilter.IsFiltered);
        Assert.Equal(["Tracker"], vm.ViewFilter.Current!.DeviceClasses!);
        var map = AprsMap
            .UI.Shell.ShellViewModel.Dockables(vm.Shell.Layout)
            .OfType<AprsMap.UI.Shell.MapDocument>()
            .Single();
        Assert.Same(map, ((Dock.Model.Core.IDock)map.Owner!).ActiveDockable);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Yield();
        }
        Assert.True(condition());
    }
}
