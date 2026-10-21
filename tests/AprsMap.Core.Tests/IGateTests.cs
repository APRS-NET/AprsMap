// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The receive-only IGate (docs/features/F3.4-rx-igate.md, acceptance tests 1–4).</summary>
public sealed class IGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    private static Engine Create(EngineTests.FakeSourceFactory factory, bool igate = true, bool transmit = true)
    {
        var home = InMemorySettingsStore.Profile("NZ0P-10") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Licensed = true,
            PositionConfirmed = true,
            SourceIds = [AprsIs.Id, Tnc.Id],
            IGate = igate ? true : null,
        };
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home],
                home.Id,
                UnitSettings.Metric,
                Sources: [AprsIs, Tnc],
                Toggles: StationToggles.AllOn with
                {
                    Transmit = transmit,
                }
            )
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T12:00:00Z", null));
        return new Engine(Options.Create(new EngineOptions()), factory, store, time, NullLogger<Engine>.Instance);
    }

    private static List<string> Gated(EngineTests.FakeSource aprsIs) =>
        [.. aprsIs.Sent.Where(l => l.Contains(",qAO,", StringComparison.Ordinal))];

    private static async Task<(
        Task Run,
        CancellationTokenSource Cts,
        EngineTests.FakeSource AprsIs,
        EngineTests.FakeSource Tnc
    )> Run(Engine engine, EngineTests.FakeSourceFactory factory, bool verified = true)
    {
        var aprsIs = factory.Enqueue("T2TEST", verified: verified);
        var tnc = factory.EnqueueFor(Tnc.Id, "Direwolf");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await tnc.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Sources!.All(s => s.State == ConnectionState.Connected)
        );
        return (run, cts, aprsIs, tnc);
    }

    [Fact]
    public async Task WhatTheTncHearsGoesToAprsIsAsReceiveOnly()
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory);
        var (run, cts, aprsIs, tnc) = await Run(engine, factory);
        using (cts)
        {
            await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile");
            await WaitUntil(() => Task.FromResult(Gated(aprsIs).Count == 1));
            Assert.Equal("W1AW-9>APRS,WIDE1-1,qAO,NZ0P-10:!4903.50N/07201.75W>Mobile", Gated(aprsIs).Single());

            await tnc.Write("W1AW-9>APRS,WIDE1-1,WIDE2*:!4903.50N/07201.75W>Mobile"); // a digipeated copy
            await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).IGate is { Refused: 1 });
            var status = (await engine.GetStatusAsync(Ct)).IGate!;
            Assert.Equal(1, status.Gated);
            Assert.Null(status.Held);
            Assert.Single(Gated(aprsIs));
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData("N0ABC>APRS,TCPIP*,qAC,T2TEST:>From the internet")]
    [InlineData("N0ABC>APRS,NOGATE:>Keep it local")]
    [InlineData("N0ABC>APRS,WIDE1-1:?APRS?")]
    public async Task WhatTheRulesKeepBackIsNotGated(string line)
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory);
        var (run, cts, aprsIs, tnc) = await Run(engine, factory);
        using (cts)
        {
            await tnc.Write(line);
            await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).IGate is { Refused: 1 });
            Assert.Empty(Gated(aprsIs));
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task WhatAprsIsSendsIsNeverGatedBack()
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory);
        var (run, cts, aprsIs, _) = await Run(engine, factory);
        using (cts)
        {
            await aprsIs.Write("W1AW-9>APRS,WIDE1-1,qAR,K1ABC:!4903.50N/07201.75W>Mobile");
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
            Assert.Empty(Gated(aprsIs));
            Assert.Equal(0, (await engine.GetStatusAsync(Ct)).IGate!.Refused); // not even considered
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(true, false, EngineErrorCodes.TransmitOff)]
    [InlineData(false, true, EngineErrorCodes.TransmitNotVerified)]
    public async Task ItGatesNothingWhileHeldAndSaysWhy(bool verified, bool transmit, string? code)
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory, transmit: transmit);
        var (run, cts, aprsIs, tnc) = await Run(engine, factory, verified);
        using (cts)
        {
            await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile");
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
            Assert.Empty(Gated(aprsIs));
            var held = (await engine.GetStatusAsync(Ct)).IGate!.Held;
            Assert.NotNull(held);
            if (code is not null)
            {
                Assert.Equal(code, held.Code);
            }
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ItIsOffByDefault()
    {
        var factory = new EngineTests.FakeSourceFactory();
        using var engine = Create(factory, igate: false);
        var (run, cts, aprsIs, tnc) = await Run(engine, factory);
        using (cts)
        {
            await tnc.Write("W1AW-9>APRS,WIDE1-1:!4903.50N/07201.75W>Mobile");
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
            Assert.Empty(Gated(aprsIs));
            Assert.Null((await engine.GetStatusAsync(Ct)).IGate);
            await cts.CancelAsync();
            await run;
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
