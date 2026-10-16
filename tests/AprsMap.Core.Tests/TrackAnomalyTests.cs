// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Track anomalies from APRS.NET's analysis (docs/features/F1.20-anomalies.md, behaviors 1, 3–5).</summary>
public sealed class TrackAnomalyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A position report from N0CAR-9 with <paramref name="symbol"/>, <paramref name="minutes"/> of latitude past 49°.</summary>
    private static string Car(double minutes, string symbol = "/>") =>
        FormattableString.Invariant(
            $"N0CAR-9>APRS,TCPIP*,qAC,T2TEST:!49{minutes:00.00}N{symbol[0]}07201.75W{symbol[1]}"
        );

    private static PacketAnomalies Apply(StationStore store, string line, DateTimeOffset at) =>
        store.Apply(AprsPacket.Parse(line), at);

    private static IReadOnlyList<StationProblem> Problems(StationStore store) => store.Get("N0CAR-9")!.Problems ?? [];

    [Fact]
    public void ANormalDriveRaisesNothing()
    {
        var store = new StationStore();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(PacketAnomalies.None, Apply(store, Car(3 + i * 0.3), T0.AddMinutes(i))); // ~33 km/h
        }

        Assert.Empty(Problems(store));
    }

    [Fact]
    public void TheSamePositionTooSoonIsARepeat()
    {
        var store = new StationStore();
        Apply(store, Car(3), T0);

        var flags = Apply(store, Car(3), T0.AddMinutes(1)); // after the 30 s duplicate window, before 2 minutes

        Assert.Equal(PacketAnomalies.RepeatedPosition, flags);
        var problem = Assert.Single(Problems(store));
        Assert.Equal(StationProblem.RepeatedPosition, problem.Kind);
        Assert.Equal("anomaly.repeated-position", problem.Latest!.Code);
        Assert.Equal("60", problem.Latest.Arg("seconds"));
    }

    [Fact]
    public void AJumpTooFastForACarIsImpossibleButNotForAnAircraft()
    {
        var store = new StationStore();
        Apply(store, Car(3), T0);

        var car = Apply(store, Car(33), T0.AddMinutes(1)); // 30' of latitude, ~56 km in a minute: ~3,300 km/h

        Assert.Equal(PacketAnomalies.ImpossibleSpeed, car);
        var problem = Assert.Single(Problems(store));
        Assert.Equal("anomaly.impossible-speed", problem.Latest!.Code);
        Assert.Equal("55.6", problem.Latest.Arg("distance"));

        var air = new StationStore();
        Apply(air, Car(3, "/^"), T0);
        Assert.Equal(PacketAnomalies.None, Apply(air, Car(18, "/^"), T0.AddMinutes(1))); // ~1,670 km/h: a jet
    }

    [Fact]
    public void ReportsUnderASecondApartAreRapid()
    {
        var store = new StationStore();
        Apply(store, Car(3), T0);

        Assert.Equal(PacketAnomalies.RapidRepeat, Apply(store, Car(3.001), T0.AddMilliseconds(400)));
        Assert.Equal("400", Assert.Single(Problems(store)).Latest!.Arg("milliseconds"));
    }

    [Fact]
    public void ATimestampEarlierThanTheLastIsTimeBackwards()
    {
        var store = new StationStore();
        Apply(store, "N0CAR-9>APRS,TCPIP*,qAC,T2TEST:@161159z4903.00N/07201.75W>", T0);

        var flags = Apply(store, "N0CAR-9>APRS,TCPIP*,qAC,T2TEST:@161150z4903.30N/07201.75W>", T0.AddMinutes(1));

        Assert.True(flags.HasFlag(PacketAnomalies.TimeBackwards));
        Assert.Contains(Problems(store), p => p.Kind == StationProblem.TimeBackwards);
    }

    [Fact]
    public void FlagsCountOverASlidingHour()
    {
        var store = new StationStore();
        Apply(store, Car(3), T0);
        Apply(store, Car(3), T0.AddMinutes(1));
        Apply(store, Car(3), T0.AddMinutes(2));
        Assert.Equal(2, Assert.Single(Problems(store)).Count);

        store.Sweep(T0.AddMinutes(62), StationAging.Default); // the first repeat is over an hour old

        Assert.Equal(1, Assert.Single(Problems(store)).Count);
    }

    [Fact]
    public async Task TheEngineMarksThePacketAndIssuesOnlyFindsIt()
    {
        var time = new FakeTimeProvider(T0);
        using var engine = new Engine(
            Options.Create(new EngineOptions { RecentPacketCapacity = 100 }),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            time,
            NullLogger<Engine>.Instance
        );
        engine.Accept(Car(3));
        time.Advance(TimeSpan.FromMinutes(1));
        engine.Accept(Car(33));

        var flagged = new List<PacketView>();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var packet in engine.WatchPacketsAsync(new PacketFilter(IssuesOnly: true), cancel.Token))
        {
            flagged.Add(packet);
            break;
        }

        Assert.Equal(PacketAnomalies.ImpossibleSpeed, Assert.Single(flagged).Anomalies);
        Assert.True(flagged[0].HasProblems);
    }
}
