// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The two-way IGate (docs/features/F3.4-two-way-igate.md, acceptance tests 1–5), against fake sources.</summary>
public sealed class IGateToRfTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static SourceSettings Tnc(bool rxOnly = false) =>
        new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1") { RxOnly = rxOnly };

    private static Engine Create(
        EngineTests.FakeSourceFactory factory,
        bool toRf = true,
        bool transmit = true,
        bool rxOnly = false
    )
    {
        var tnc = Tnc(rxOnly);
        var home = InMemorySettingsStore.Profile("NZ0P-10") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Licensed = true,
            PositionConfirmed = true,
            SourceIds = [AprsIs.Id, tnc.Id],
            IGate = true,
            IGateToRf = toRf ? true : null,
        };
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home],
                home.Id,
                UnitSettings.Metric,
                Sources: [AprsIs, tnc],
                Toggles: StationToggles.AllOn with
                {
                    Transmit = transmit,
                }
            )
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-26T12:00:00Z", null));
        return new Engine(Options.Create(new EngineOptions()), factory, store, time, NullLogger<Engine>.Instance);
    }

    private static async Task<(
        Task Run,
        CancellationTokenSource Cts,
        EngineTests.FakeSource AprsIs,
        EngineTests.FakeSource Tnc
    )> Run(Engine engine, EngineTests.FakeSourceFactory factory)
    {
        var aprsIs = factory.Enqueue("T2TEST", verified: true);
        var tnc = factory.EnqueueFor("tnc", "Direwolf");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await tnc.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Sources!.All(s => s.State == ConnectionState.Connected)
        );
        return (run, cts, aprsIs, tnc);
    }

    /// <summary>What the station sent on the TNC that came from APRS-IS: third-party packets.</summary>
    private static List<string> ToRf(EngineTests.FakeSource tnc) =>
        [.. tnc.Sent.Where(l => l.Contains(":}", StringComparison.Ordinal))];

    /// <summary>Gives APRS-IS a packet, then one that's never gated, so whatever was going to be sent has been.</summary>
    private static async Task Settle(Engine engine, EngineTests.FakeSource aprsIs, string line)
    {
        var before = (await engine.GetStatusAsync(Ct)).PacketsReceived;
        await aprsIs.Write(line);
        await aprsIs.Write("N0LAST>APRS,TCPIP*,qAC,T2TEST:>marker " + before);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).PacketsReceived >= before + 2);
    }

    [Fact]
    public async Task AMessageForAStationHeardHereGoesOutOnItsTncAndTheSendersPositionFollowsOnce()
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory);
        var (run, cts, aprsIs, tnc) = await Run(engine, factory);
        using (cts)
        {
            await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile"); // heard directly: local
            await WaitUntil(() =>
                Task.FromResult(aprsIs.Sent.Any(l => l.Contains(",qAR,NZ0P-10", StringComparison.Ordinal)))
            );

            await Settle(engine, aprsIs, "N0NET>APRS,TCPIP*,qAC,T2TEST::W1AW-9   :Are you there?{1");
            await WaitUntil(() => Task.FromResult(ToRf(tnc).Count == 1));
            Assert.Equal(
                "NZ0P-10>" + Brand.Tocall + ",WIDE1-1,WIDE2-1:}N0NET>APRS,TCPIP,NZ0P-10*::W1AW-9   :Are you there?{1",
                ToRf(tnc).Single()
            );

            await Settle(engine, aprsIs, "N0NET>APRS,TCPIP*,qAC,T2TEST:!4910.00N/07210.00W-Home");
            await Settle(engine, aprsIs, "N0NET>APRS,TCPIP*,qAC,T2TEST:!4910.00N/07210.00W-Home again");
            await WaitUntil(() => Task.FromResult(ToRf(tnc).Count == 2));
            Assert.Contains(
                ":}N0NET>APRS,TCPIP,NZ0P-10*:!4910.00N/07210.00W-Home",
                ToRf(tnc)[1],
                StringComparison.Ordinal
            );
            Assert.Equal(2, ToRf(tnc).Count); // the position once
            Assert.Equal(2, (await engine.GetStatusAsync(Ct)).IGate!.GatedToRf);

            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task MessagesForStationsNotHeardOrFromLocalSendersOrBulletinsStayOffRf()
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory);
        var (run, cts, aprsIs, tnc) = await Run(engine, factory);
        using (cts)
        {
            await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile");
            await tnc.Write("N0NEAR>APRS,WIDE1-1:!4904.50N/07201.75W>Near");
            await WaitUntil(() =>
                Task.FromResult(aprsIs.Sent.Count(l => l.Contains(",qAR,", StringComparison.Ordinal)) == 2)
            );

            await Settle(engine, aprsIs, "N0NET>APRS,TCPIP*,qAC,T2TEST::N0FAR-9  :Not heard here{2");
            await Settle(engine, aprsIs, "N0NEAR>APRS,TCPIP*,qAC,T2TEST::W1AW-9   :You can hear me{3");
            await Settle(engine, aprsIs, "N0NET>APRS,TCPIP*,qAC,T2TEST::BLN1     :A bulletin");

            Assert.Empty(ToRf(tnc));
            Assert.Equal(3, (await engine.GetStatusAsync(Ct)).IGate!.RefusedToRf);
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(false, true, false)] // the switch off
    [InlineData(true, false, false)] // Transmit off
    [InlineData(true, true, true)] // a receive-only TNC
    public async Task NothingGoesToRfWhenItMayNot(bool toRf, bool transmit, bool rxOnly)
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory, toRf, transmit, rxOnly);
        var (run, cts, aprsIs, tnc) = await Run(engine, factory);
        using (cts)
        {
            await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile");
            await Settle(engine, aprsIs, "N0NET>APRS,TCPIP*,qAC,T2TEST::W1AW-9   :Are you there?{1");

            Assert.Empty(ToRf(tnc));
            if (!toRf)
            {
                Assert.Contains(aprsIs.Sent, l => l.Contains(",qAO,NZ0P-10", StringComparison.Ordinal)); // still receive only
            }
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task TheSeventhInAMinuteIsRefused()
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory);
        var (run, cts, aprsIs, tnc) = await Run(engine, factory);
        using (cts)
        {
            await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile");
            for (var i = 1; i <= 7; i++)
            {
                await Settle(engine, aprsIs, $"N0NET>APRS,TCPIP*,qAC,T2TEST::W1AW-9   :Message {i}{{{i}");
            }

            await WaitUntil(() => Task.FromResult(ToRf(tnc).Count == 6));
            Assert.Equal(6, ToRf(tnc).Count); // Dire Wolf's limit: 6 a minute
            Assert.DoesNotContain(ToRf(tnc), l => l.Contains("Message 7", StringComparison.Ordinal));
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task TheDashboardListsTheStationsHeardOnRf()
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory);
        var (run, cts, _, tnc) = await Run(engine, factory);
        using (cts)
        {
            await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile");
            await tnc.Write("N0DIGI>APRS,N0RELAY*,WIDE2-1:!4904.50N/07201.75W#");
            await tnc.Write("NZ0P-10>APRS,N0RELAY*:!4905.50N/07201.75W-"); // the station itself, heard back
            await WaitUntil(async () =>
                (await engine.GetDashboardAsync(DashboardRange.Hour, null, Ct)).HeardOnRfTotal == 2
            );

            var heard = (await engine.GetDashboardAsync(DashboardRange.Hour, null, Ct)).HeardOnRf!;
            Assert.Equal(["N0DIGI", "W1AW-9"], heard.Select(h => h.Station).Order(StringComparer.Ordinal));
            Assert.Equal(1, heard.Single(h => h.Station == "N0DIGI").Hops);
            await cts.CancelAsync();
            await run;
        }
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(20, Ct);
        }
    }
}
