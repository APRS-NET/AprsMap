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

    // APRS101 chapter 13's example: a balloon's definitions, sent as messages to its own callsign.
    private const string Balloon = "N0QBF-11";

    private static string Definition(string from, string text) =>
        $"{from}>APRS,TCPIP*,qAC,T2TEST::{Balloon, -9}:{text}";

    private static readonly string[] Definitions =
    [
        "PARM.Battery,Btemp,ATemp,Pres,Alt,Camra,Chute,Sun,10m,ATV",
        "UNIT.Volts,deg.F,deg.F,Mbar,Kfeet,Clik,OPEN!,on,on,high",
        "EQNS.0,5.2,0,0,.53,-32,3,4.39,49,-32,3,18,1,2,3",
        "BITS.10110000,N0QBF's Big Balloon",
    ];

    private const string BalloonReport = Balloon + ">APRS,TCPIP*,qAC,T2TEST:T#005,199,000,255,073,123,01101001";

    [Fact]
    public void WithoutDefinitionsTheValuesAreRawAndUndefined()
    {
        var store = new StationStore();
        Apply(store, BalloonReport, T0);

        var telemetry = store.Telemetry(Balloon)!;

        Assert.Null(telemetry.Definitions);
        Assert.Equal(199, telemetry.Samples[0].Analog[0]);
        Assert.Equal([false, true, true, false, true, false, false, true], telemetry.Samples[0].Digital); // 1 is on
    }

    [Fact]
    public void DefinitionsThatArriveLaterNameAndScaleTheReportsHeld()
    {
        var store = new StationStore();
        Apply(store, BalloonReport, T0);
        foreach (var text in Definitions)
        {
            Apply(store, Definition(Balloon, text), T0.AddMinutes(1));
        }

        var telemetry = store.Telemetry(Balloon)!;
        var definitions = telemetry.Definitions!;

        Assert.Equal(new TelemetryAnalogChannel("Battery", "Volts", true), definitions.Analog[0]);
        Assert.Equal(new TelemetryDigitalChannel("Camra", "Clik"), definitions.Digital[0]);
        Assert.Equal("N0QBF's Big Balloon", definitions.ProjectName);
        Assert.Equal(5.2 * 199, telemetry.Samples[0].Analog[0]!.Value, 6); // 0·x² + 5.2·x + 0
        // BITS.10110000: a bit is on when it equals the sense, so 01101001 reads off, off, on, off, off, on, on, off.
        Assert.Equal([false, false, true, false, false, true, true, false], telemetry.Samples[0].Digital);
    }

    [Fact]
    public void DefinitionsFromAnotherCallsignApplyToTheStationTheyAddress()
    {
        var store = new StationStore();
        Apply(store, Definition("N0QBF", Definitions[0]), T0); // sent by the balloon's owner, before it's heard
        Apply(store, BalloonReport, T0.AddMinutes(1));

        Assert.Equal("Battery", store.Telemetry(Balloon)!.Definitions!.Analog[0].Name);
        Assert.Null(store.Telemetry("N0QBF")); // the sender has no telemetry of its own
    }

    [Fact]
    public void ClearingTelemetryForgetsTheDefinitionsToo()
    {
        var store = new StationStore();
        Apply(store, Definition(Balloon, Definitions[0]), T0);
        Apply(store, BalloonReport, T0);
        store.ClearTelemetry();

        Apply(store, BalloonReport, T0.AddMinutes(1));

        Assert.Null(store.Telemetry(Balloon)!.Definitions);
    }

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
