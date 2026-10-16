// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>The RF network over the last hour (docs/features/F1.21-rf-paths.md, behavior 4).</summary>
public sealed class NetworkLinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static void Apply(StationStore store, string line, DateTimeOffset at) =>
        store.Apply(AprsPacket.Parse(line), at);

    private static StationStore WithInfrastructure()
    {
        var store = new StationStore();
        Apply(store, "N0GATE-1>APRS,TCPIP*,qAC,T2TEST:!4910.00N/07200.00W&", T0);
        Apply(store, "N0DIGI-1>APRS,TCPIP*,qAC,T2TEST:!4900.00N/07152.00W#", T0);
        return store;
    }

    [Fact]
    public void EachRfPacketCountsTowardTheLinksItCrossed()
    {
        var store = WithInfrastructure();
        Apply(store, "N0CAR-9>APRS,N0DIGI-1*,WIDE2-1,qAR,N0GATE-1:!4900.00N/07200.00W>", T0);
        Apply(store, "N0CAR-9>APRS,N0DIGI-1*,WIDE2-1,qAR,N0GATE-1:!4900.10N/07200.00W>", T0.AddMinutes(1));
        Apply(store, "N0HT-7>APRS,WIDE1-1,qAR,N0GATE-1:!4905.00N/07200.00W[", T0.AddMinutes(1));
        Apply(store, "N0NET>APRS,TCPIP*,qAC,T2TEST:!4905.00N/07200.00W-", T0.AddMinutes(1)); // not RF

        var links = store.Links().ToDictionary(l => (l.From, l.To), l => l.Packets);

        Assert.Equal(2, links[("N0CAR-9", "N0DIGI-1")]);
        Assert.Equal(2, links[("N0DIGI-1", "N0GATE-1")]);
        Assert.Equal(1, links[("N0HT-7", "N0GATE-1")]);
        Assert.Equal(3, links.Count);
    }

    [Fact]
    public void ALinkWhoseEndHasNoPositionIsLeftOut()
    {
        var store = WithInfrastructure();

        Apply(store, "N0CAR-9>APRS,N0NONE-1*,qAR,N0GATE-1:!4900.00N/07200.00W>", T0);

        Assert.Empty(store.Links());
    }

    [Fact]
    public void PacketsOlderThanAnHourStopCounting()
    {
        var store = WithInfrastructure();
        Apply(store, "N0HT-7>APRS,WIDE1-1,qAR,N0GATE-1:!4905.00N/07200.00W[", T0);
        Apply(store, "N0HT-7>APRS,WIDE1-1,qAR,N0GATE-1:!4905.10N/07200.00W[", T0.AddMinutes(50));

        store.Sweep(T0.AddMinutes(70), StationAging.Default);

        Assert.Equal(1, Assert.Single(store.Links()).Packets);
        store.Sweep(T0.AddMinutes(120), StationAging.Default with { RemoveAfterMinutes = null });
        Assert.Empty(store.Links());
    }
}
