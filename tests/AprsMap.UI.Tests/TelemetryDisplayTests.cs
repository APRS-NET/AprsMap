// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Telemetry in the details (docs/features/F1.11-telemetry.md, behaviors 3, 4).</summary>
public sealed class TelemetryDisplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheDetailsShowTheLatestValuesAChartPerChannelAndTheBits()
    {
        var time = new FakeTimeProvider(T0.AddMinutes(2));
        var engine = new FakeEngine();
        engine.Details["N0TLM"] = new StationDetail(
            "N0TLM",
            StationKind.Station,
            T0,
            2,
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
            null,
            null
        );
        engine.Telemetry["N0TLM"] = new TelemetryView([
            new TelemetrySample(T0.AddMinutes(-10), "001", [1, 2, null, null, null], [true, false]),
            new TelemetrySample(
                T0,
                "002",
                [1.5, 3, null, null, null],
                [true, true, false, false, false, false, false, false]
            ),
        ]);
        var vm = new MainViewModel(engine, time);

        vm.SelectedStation = "N0TLM";
        for (var i = 0; i < 200 && !vm.Details.HasTelemetry; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        Assert.True(vm.Details.HasTelemetry);
        Assert.Equal("Report 002 · 2 minutes ago · 2 reports held", vm.Details.TelemetryText);
        Assert.Equal(["A1 1.5", "A2 3"], vm.Details.Telemetry.Select(c => $"{c.Name} {c.Latest}"));
        Assert.Equal([1, 1.5], vm.Details.Telemetry[0].Values);
        Assert.Contains("1, 1.5", vm.Details.Telemetry[0].ChartName, StringComparison.Ordinal);
        Assert.Equal("11000000", vm.Details.TelemetryBits);
        Assert.True(vm.Details.IsTelemetryRaw); // no definitions: the note says the values are raw

        vm.SelectedStation = null;
        Assert.False(vm.Details.HasTelemetry);
    }

    [Fact]
    public async Task DefinitionsNameTheChannelsWithTheirUnitsAndTheBits()
    {
        var time = new FakeTimeProvider(T0.AddMinutes(2));
        var engine = new FakeEngine();
        engine.Details["N0QBF-11"] = new StationDetail(
            "N0QBF-11",
            StationKind.Station,
            T0,
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
            null,
            null
        );
        engine.Telemetry["N0QBF-11"] = new TelemetryView(
            [
                new TelemetrySample(
                    T0,
                    "005",
                    [1034.8, null, null, null, null],
                    [true, false, false, false, false, false, false, false]
                ),
            ],
            new TelemetryDefinitions(
                [
                    new("Battery", "Volts", true),
                    new(null, null, false),
                    new(null, null, false),
                    new(null, null, false),
                    new(null, null, false),
                ],
                [
                    new("Camra", "Clik"),
                    new("Chute", "OPEN!"),
                    new(null, null),
                    new(null, null),
                    new(null, null),
                    new(null, null),
                    new(null, null),
                    new(null, null),
                ],
                "N0QBF's Big Balloon"
            )
        );
        var vm = new MainViewModel(engine, time);

        vm.SelectedStation = "N0QBF-11";
        for (var i = 0; i < 200 && !vm.Details.HasTelemetry; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["Battery 1034.8 Volts"], vm.Details.Telemetry.Select(c => $"{c.Name} {c.Latest}"));
        Assert.Contains("Battery over the held reports", vm.Details.Telemetry[0].ChartName, StringComparison.Ordinal);
        Assert.Equal(
            [new Stations.DetailLine("Camra", "Clik"), new Stations.DetailLine("Chute", "off")],
            vm.Details.TelemetryBitLines
        );
        Assert.Null(vm.Details.TelemetryBits);
        Assert.Equal("N0QBF's Big Balloon", vm.Details.TelemetryProject);
        Assert.False(vm.Details.IsTelemetryRaw);
    }

    [Fact]
    public async Task TheDetailsListProblemsWithTheirCountAndLatestExample()
    {
        var engine = new FakeEngine();
        engine.Details["N0BAD"] = new StationDetail(
            "N0BAD",
            StationKind.Station,
            T0,
            2,
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
            null,
            null,
            Problems: [new StationProblem(StationProblem.Invalid, 3, new EngineError("aprs.parse.x", "Bad latitude."))]
        );
        var vm = new MainViewModel(engine, new FakeTimeProvider(T0));

        vm.SelectedStation = "N0BAD";
        for (var i = 0; i < 200 && !vm.Details.HasProblems; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        var line = Assert.Single(vm.Details.Problems);
        Assert.Equal("Invalid packets", line.Label);
        Assert.Equal("3 times in the last hour. Latest: Bad latitude.", line.Value);
    }
}
