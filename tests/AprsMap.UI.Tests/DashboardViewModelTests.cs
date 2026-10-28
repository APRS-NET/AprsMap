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

        public StationFilter? ViewFilter { get; set; }

        public DashboardViewModel Create() =>
            new(
                Engine,
                Time,
                new Localizer(CultureInfo.GetCultureInfo("en-US")),
                () => Status,
                new SourceNames(() => null),
                source => source.Kind == SourceKind.AprsIs ? Switch : null,
                Selected.Add,
                () => ProblemsShown++,
                viewFilter: () => ViewFilter
            );
    }

    [Fact]
    public void TheConnectionTileSaysWhenTheHistorysSizeLimitCutsItShort()
    {
        // F1.41 behavior 5.
        var vm = new Harness().Create();

        vm.Show(
            Sample() with
            {
                History = new HistoryStatus(
                    true,
                    HistorySettings.Default,
                    Problem: HistoryProblem.SizeLimitShortens,
                    DaysFit: 3
                ),
            }
        );
        Assert.Equal(
            "The size limit keeps about 3 days of the 7 asked for. Raise it to keep them all.",
            vm.HistoryWarning
        );
        vm.Show(Sample());
        Assert.Null(vm.HistoryWarning);
    }

    [Fact]
    public async Task WithTwoSourcesOneCanBeShownAndTheyAreCompared()
    {
        // #454
        var tnc = SourceSettings.KissTcpFor("127.0.0.1", 8001) with
        {
            Id = "tnc",
            Name = "TNC-X",
        };
        var aprsIs = SourceSettings.AprsIsFor("rotate.aprs2.net:14580") with { Id = "rotate", Name = "APRS-IS" };
        var profile = new StationProfile("p", "Home", "N0CALL", -1, [aprsIs.Id, tnc.Id], null, null);
        var settings = new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [aprsIs, tnc]);
        var engine = new FakeEngine
        {
            Dashboard = Sample() with
            {
                Comparison = [new DashboardSourceRow("rotate", 40, 1200, 35), new DashboardSourceRow("tnc", 9, 60, 0)],
            },
        };
        var vm = new DashboardViewModel(
            engine,
            new FakeTimeProvider(T0),
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            () => null,
            new SourceNames(() => settings),
            _ => null,
            _ => { },
            () => { }
        );

        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.HasSourceChoice);
        Assert.Equal(["All sources", "APRS-IS", "TNC-X"], vm.SourceOptions.Select(o => o.Name));
        Assert.Equal(
            [
                new ValueRow("APRS-IS", "40 stations · 1,200 packets · 35 heard only here"),
                new ValueRow("TNC-X", "9 stations · 60 packets · none heard only here"),
            ],
            vm.Comparison
        );

        vm.SelectedSource = vm.SourceOptions[2];
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["tnc"], engine.DashboardSources);
        Assert.False(vm.HasComparison);
        Assert.Equal(
            "Farthest heard directly, weather, heard on RF and the tables still count every source.",
            vm.ScopeText
        );
    }

    [Fact]
    public void SevenDaysPutTheDayInEachPointAndSayWhatTheListsCover()
    {
        // F1.41 behavior 10.
        var vm = new Harness().Create();
        Assert.Contains("7 days", vm.Ranges.Select(r => r.Label));

        vm.Show(
            Sample() with
            {
                Range = DashboardRange.Week,
                Start = T0,
                Step = TimeSpan.FromHours(1),
                ListsCover = TimeSpan.FromDays(2),
            }
        );

        Assert.StartsWith("Fri 12:00", vm.Stations.PointTimes[0], StringComparison.Ordinal);
        Assert.StartsWith("Fri 1:00", vm.Stations.PointTimes[1], StringComparison.Ordinal);
        Assert.Equal(
            "The stations heard, the newest and the busiest cover the last 2 days: that's all the history kept.",
            vm.ScopeText
        );
    }

    [Fact]
    public void MyViewOverSevenDaysSaysItsTheWholeFeed()
    {
        var vm = new Harness { ViewFilter = new StationFilter(Kinds: StationKinds.Stations) }.Create();

        vm.Range = DashboardRange.Week;
        vm.MyView = true;

        Assert.Equal("My view counts up to 24 hours, so over 7 days this is the whole feed.", vm.ScopeText);
        vm.Range = DashboardRange.Day;
        Assert.StartsWith("My view: only the stations", vm.ScopeText, StringComparison.Ordinal);
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
    public async Task ATncRowHasItsPillsSwitchAndSaysOffWhenItsOff()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider(T0));
        vm.Show(engine.Settings);
        EngineStatus Status(bool tncOn) =>
            new(ConnectionState.Connected, "T2TEST", 0, "1", Toggles: StationToggles.AllOn)
            {
                Sources =
                [
                    new("rotate", "rotate.aprs2.net", SourceKind.AprsIs, ConnectionState.Connected, "T2TEST"),
                    new(
                        "tnc",
                        "Direwolf",
                        SourceKind.KissTcp,
                        tncOn ? ConnectionState.Connected : ConnectionState.Disconnected
                    )
                    {
                        Enabled = tncOn,
                    },
                ],
            };
        vm.Show(Status(tncOn: true));

        vm.Dashboard.Show(Sample());

        var tnc = vm.Dashboard.Connections[1];
        Assert.Same(vm.SourcePills[0].Switch, tnc.Switch); // the pill and the row move together
        Assert.Equal(("Connected", "Direwolf on or off"), (tnc.State, tnc.SwitchName));
        Assert.Equal("APRS-IS on or off", vm.Dashboard.Connections[0].SwitchName);

        tnc.Switch!.IsOn = false;
        await WaitUntil(() => engine.SourcesSwitched.Count == 1);
        Assert.Equal(("tnc", false), engine.SourcesSwitched[0]);
        vm.Show(Status(tncOn: false));
        vm.Dashboard.Show(Sample());
        Assert.Equal("Off", vm.Dashboard.Connections[1].State);
        Assert.False(vm.Dashboard.Connections[1].IsQuiet); // off isn't quiet
        Assert.False(vm.SourcePills[0].Switch.IsOn);
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
    public async Task MyViewAsksWithTheViewFilterAndFollowsItsChanges()
    {
        var harness = new Harness();
        var vm = harness.Create();
        vm.Start();
        await WaitUntil(() => harness.Engine.DashboardFilters.Count == 1);
        Assert.Null(harness.Engine.DashboardFilters[^1]); // the whole feed by default
        Assert.True(vm.WholeFeed);
        Assert.Null(vm.ScopeText);

        vm.MyView = true; // no view filter on: the whole feed, and it says so
        await WaitUntil(() => harness.Engine.DashboardFilters.Count == 2);
        Assert.Null(harness.Engine.DashboardFilters[^1]);
        Assert.Equal("Your View filter is off, so My view counts the whole feed.", vm.ScopeText);

        harness.ViewFilter = new StationFilter(Reception: Reception.AnyRf);
        vm.ViewFilterChanged();
        await WaitUntil(() => harness.Engine.DashboardFilters.Count == 3);
        Assert.Equal(harness.ViewFilter, harness.Engine.DashboardFilters[^1]);
        Assert.StartsWith("My view: only the stations", vm.ScopeText, StringComparison.Ordinal);

        vm.WholeFeed = true;
        await WaitUntil(() => harness.Engine.DashboardFilters.Count == 4);
        Assert.Null(harness.Engine.DashboardFilters[^1]);
        Assert.False(vm.MyView);
        vm.ViewFilterChanged(); // the whole feed doesn't ask again for a filter change
        await Task.Yield();
        Assert.Equal(4, harness.Engine.DashboardFilters.Count);
        vm.Stop();
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
            _ => null,
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
