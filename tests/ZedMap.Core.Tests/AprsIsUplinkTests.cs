// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ZedMap.Contracts;

namespace ZedMap.Core.Tests;

/// <summary>The APRS-IS uplink apart from Transmit (docs/features/F1.45-aprs-is-uplink.md, acceptance 1–5).</summary>
public sealed class AprsIsUplinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    private sealed record Station(Engine Engine, InMemorySettingsStore Store, EngineTests.FakeSourceFactory Factory)
        : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }

    private static Station Create(StationToggles toggles, bool igate = false, bool licensed = true)
    {
        var home = InMemorySettingsStore.Profile("NZ0P-10") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Comment = "Home",
            Licensed = licensed,
            PositionConfirmed = true,
            SourceIds = [AprsIs.Id, Tnc.Id],
            IGate = igate ? true : null,
        };
        var store = new InMemorySettingsStore(
            new AppSettings([home], home.Id, UnitSettings.Metric, Sources: [AprsIs, Tnc], Toggles: toggles)
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-19T12:00:00Z", null));
        var factory = new EngineTests.FakeSourceFactory();
        var engine = new Engine(Options.Create(new EngineOptions()), factory, store, time, NullLogger<Engine>.Instance);
        return new Station(engine, store, factory);
    }

    private static async Task<(
        Task Run,
        CancellationTokenSource Cts,
        EngineTests.FakeSource AprsIs,
        EngineTests.FakeSource Tnc
    )> Run(Station station)
    {
        var engine = station.Engine;
        var aprsIs = station.Factory.Enqueue("T2TEST", verified: true);
        var tnc = station.Factory.EnqueueFor(Tnc.Id, "Direwolf");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await tnc.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Sources!.All(s => s.State == ConnectionState.Connected)
        );
        return (run, cts, aprsIs, tnc);
    }

    private static async Task<string?> RefusalOf(Func<Task> act) =>
        (await Assert.ThrowsAnyAsync<InvalidOperationException>(act) as IHasEngineError)?.EngineError.Code;

    [Fact]
    public void AStationSavedBeforeItHasTheUplinkAsItsTransmitWas()
    {
        // Acceptance 1 (decision 3): nothing changes until the operator changes it.
        Assert.True((StationToggles.AllOn with { Transmit = true }).UplinkOn);
        Assert.False((StationToggles.AllOn with { Transmit = false }).UplinkOn);
        Assert.False((StationToggles.AllOn with { Transmit = true, AprsIsUplink = false }).UplinkOn);
        Assert.True((StationToggles.AllOn with { Transmit = false, AprsIsUplink = true }).UplinkOn);
        Assert.False(StationToggles.AllOn.UplinkOn); // a new station: both off
    }

    [Fact]
    public async Task TogglingTransmitNoLongerMovesTheUplink()
    {
        // Behavior 1: from the first toggle on, the two are saved apart.
        using var station = Create(StationToggles.AllOn with { Transmit = true });
        var engine = station.Engine;
        {
            var (run, cts, _, _) = await Run(station);
            using (cts)
            {
                await engine.SetToggleAsync(Toggle.Transmit, false, Ct);
                Assert.False(station.Store.Stored!.EffectiveToggles.Transmit);
                Assert.True(station.Store.Stored.EffectiveToggles.UplinkOn);

                await engine.SetToggleAsync(Toggle.AprsIsUplink, false, Ct);
                await engine.SetToggleAsync(Toggle.Transmit, true, Ct);
                Assert.True(station.Store.Stored!.EffectiveToggles.Transmit);
                Assert.False(station.Store.Stored.EffectiveToggles.UplinkOn);
                await cts.CancelAsync();
                await run;
            }
        }
    }

    [Fact]
    public async Task WithTransmitOffAndTheUplinkOnTheIGateGatesAndTheRadioStaysQuiet()
    {
        // Acceptance 2 (behavior 5).
        using var station = Create(StationToggles.AllOn with { Transmit = false, AprsIsUplink = true }, igate: true);
        var engine = station.Engine;
        {
            var (run, cts, aprsIs, tnc) = await Run(station);
            using (cts)
            {
                await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile");
                await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).IGate is { Gated: 1 });
                Assert.Equal("W1AW-9>APRS,WIDE1-1,qAO,NZ0P-10:!4903.50N/07201.75W>Mobile", Assert.Single(aprsIs.Sent));
                Assert.Null((await engine.GetStatusAsync(Ct)).IGate!.Held);

                Assert.Single(await engine.SendPositionAsync(null, Ct)); // own traffic: APRS-IS only
                Assert.Empty(tnc.Sent);
                Assert.Equal(EngineErrorCodes.TransmitOff, await RefusalOf(() => engine.SendPositionAsync(Tnc.Id, Ct)));
                await cts.CancelAsync();
                await run;
            }
        }
    }

    [Fact]
    public async Task WithTheUplinkOffTheIGateIsHeldForThatReasonAndTheRadioStillSends()
    {
        // Acceptance 3 and 4 (behaviors 2 and 5).
        using var station = Create(StationToggles.AllOn with { Transmit = true, AprsIsUplink = false }, igate: true);
        var engine = station.Engine;
        {
            var (run, cts, aprsIs, tnc) = await Run(station);
            using (cts)
            {
                await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile");
                await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
                Assert.Empty(aprsIs.Sent);
                var status = await engine.GetStatusAsync(Ct);
                Assert.Equal(EngineErrorCodes.TransmitUplinkOff, status.IGate!.Held!.Code);
                Assert.True(status.Transmit!.On);
                Assert.Null(status.Transmit.Blocked); // the Transmit pill isn't held for the uplink's reason
                Assert.False(status.Transmit.UplinkOn);

                var sent = await engine.SendPositionAsync(null, Ct);
                Assert.Equal([Tnc.Id], sent.Select(p => p.SourceId));
                Assert.Single(tnc.Sent);
                Assert.Empty(aprsIs.Sent);
                Assert.Equal(
                    EngineErrorCodes.TransmitUplinkOff,
                    await RefusalOf(() => engine.SendPositionAsync(AprsIs.Id, Ct))
                );
                await cts.CancelAsync();
                await run;
            }
        }
    }

    [Fact]
    public async Task WithBothOffSendingIsRefusedAsTransmitOff()
    {
        // Acceptance 4.
        using var station = Create(StationToggles.AllOn with { Transmit = false, AprsIsUplink = false });
        var engine = station.Engine;
        {
            var (run, cts, aprsIs, tnc) = await Run(station);
            using (cts)
            {
                Assert.Equal(EngineErrorCodes.TransmitOff, await RefusalOf(() => engine.SendPositionAsync(null, Ct)));
                Assert.Empty(aprsIs.Sent);
                Assert.Empty(tnc.Sent);
                await cts.CancelAsync();
                await run;
            }
        }
    }

    [Fact]
    public async Task TurningTheUplinkOnIsCheckedLikeTransmit()
    {
        // Acceptance 5 (behavior 3, decision 4); off always works.
        using var station = Create(StationToggles.AllOn with { Transmit = false }, licensed: false);
        var engine = station.Engine;
        {
            var (run, cts, _, _) = await Run(station);
            using (cts)
            {
                Assert.Equal(
                    EngineErrorCodes.TransmitNotLicensed,
                    await RefusalOf(() => engine.SetToggleAsync(Toggle.AprsIsUplink, true, Ct))
                );
                Assert.False(station.Store.Stored!.EffectiveToggles.UplinkOn);
                await engine.SetToggleAsync(Toggle.AprsIsUplink, false, Ct);
                await cts.CancelAsync();
                await run;
            }
        }
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
        Assert.Fail("Condition was not met in time.");
    }
}
