// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>Invalid packets flagged per station (docs/features/F1.20-anomalies.md, behaviors 2, 3, 5, 6).</summary>
public sealed class ProblemTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private const string Bad = "N0BAD>APRS,TCPIP*,qAC,T2TEST:!9999.99N/99999.99W- malformed";

    private static void Apply(StationStore store, string line, DateTimeOffset at) =>
        store.Apply(AprsPacket.Parse(line), at);

    [Fact]
    public void AStationsInvalidPacketsAreCountedWithTheLatestIssue()
    {
        var store = new StationStore();
        Apply(store, Bad, T0);
        Apply(store, Bad, T0.AddMinutes(1));

        var problem = Assert.Single(store.Get("N0BAD")!.Problems!);

        Assert.Equal(StationProblem.Invalid, problem.Kind);
        Assert.Equal(2, problem.Count);
        Assert.StartsWith(EngineErrorCodes.AprsParsePrefix, problem.Latest!.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void ProblemsOlderThanAnHourAreForgotten()
    {
        var store = new StationStore();
        Apply(store, Bad, T0);

        store.Sweep(T0.AddMinutes(61), StationAging.Default);

        Assert.Null(store.Get("N0BAD")!.Problems);
    }

    [Fact]
    public void AGoodStationHasNoProblemsAndTheFilterAndListKnow()
    {
        var store = new StationStore();
        Apply(store, Bad, T0);
        Apply(store, "N0BAD>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W-", T0); // on the map, still flagged
        Apply(store, "N0GOOD>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W-", T0);

        Assert.Null(store.Get("N0GOOD")!.Problems);
        store.Subscribe(new StationFilter(HasProblems: true), out var shown, out _);
        Assert.Equal("N0BAD", Assert.Single(shown).Name);
        Assert.True(store.Query(new StationQuery()).Rows.Single(r => r.Name == "N0BAD").HasProblems);
    }
}
