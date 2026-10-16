// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>Telemetry reports in the store (docs/features/F1.11-telemetry.md, behavior 1).</summary>
public sealed class TelemetryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static void Apply(StationStore store, string line, DateTimeOffset at) =>
        store.Apply(AprsPacket.Parse(line), at);

    private static string Report(int sequence) =>
        $"N0TLM>APRS,TCPIP*,qAC,T2TEST:T#{sequence:000},{sequence:000},002,003,004,005,10110000";

    [Fact]
    public void ReportsAreKeptRawWithTheirSequenceAndBits()
    {
        var store = new StationStore();
        Apply(store, Report(1), T0);
        Apply(store, Report(2), T0.AddMinutes(10));

        var telemetry = store.Telemetry("N0TLM")!;

        Assert.Equal(2, telemetry.Samples.Count);
        var latest = telemetry.Samples[^1];
        Assert.Equal("002", latest.Sequence);
        Assert.Equal(2, latest.Analog[0]);
        Assert.True(latest.Digital[0]);
        Assert.False(latest.Digital[1]);
    }

    [Fact]
    public void AtMostAHundredReportsAndNoneOlderThanADay()
    {
        var store = new StationStore();
        for (var i = 0; i < 105; i++)
        {
            Apply(store, Report(i), T0.AddMinutes(i));
        }
        Assert.Equal(StationStore.TelemetryReports, store.Telemetry("N0TLM")!.Samples.Count);

        store.Sweep(T0.AddHours(27), StationAging.Default with { RemoveAfterMinutes = null });

        Assert.Null(store.Telemetry("N0TLM"));
    }

    [Fact]
    public void TheFilterAndClearKnowAboutTelemetry()
    {
        var store = new StationStore();
        Apply(store, Report(1), T0);
        Apply(store, "N0TLM>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W>", T0);
        Apply(store, "N0CAR>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W>", T0);

        store.Subscribe(new StationFilter(WithTelemetry: true), out var shown, out _);
        Assert.Equal("N0TLM", Assert.Single(shown).Name);

        Assert.Equal(1, store.ClearTelemetry());
        Assert.Null(store.Telemetry("N0TLM"));
    }

    [Fact]
    public void AStationWithoutTelemetryHasNone() => Assert.Null(new StationStore().Telemetry("NOBODY"));
}
