// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Answering APRS queries (docs/features/F2.8-queries.md, acceptance tests 1–4).</summary>
public sealed class QueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    private sealed record Station(Engine Engine, EngineTests.FakeSourceFactory Sources, FakeTimeProvider Time)
        : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }

    private static Station Create(bool answer = true, bool transmit = true, bool confirmed = true)
    {
        var home = InMemorySettingsStore.Profile("NZ0P-10") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Licensed = true,
            PositionConfirmed = confirmed,
            SourceIds = [AprsIs.Id, Tnc.Id],
            StatusBeacon = new StatusBeaconSettings(Text: "QRV on 146.52"),
            AnswerQueries = answer ? null : false,
            Objects = [new OwnObject("net", "NETCTL", OwnObjectKind.Object, 49.1, -72.1, Comment: "Net 7pm")],
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
        var factory = new EngineTests.FakeSourceFactory();
        var engine = new Engine(Options.Create(new EngineOptions()), factory, store, time, NullLogger<Engine>.Instance);
        return new Station(engine, factory, time);
    }

    private static async Task<(
        Task Run,
        CancellationTokenSource Cts,
        EngineTests.FakeSource AprsIs,
        EngineTests.FakeSource Tnc
    )> Run(Station station)
    {
        var aprsIs = station.Sources.Enqueue("T2TEST", verified: true);
        var tnc = station.Sources.EnqueueFor(Tnc.Id, "Direwolf");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = station.Engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await tnc.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () =>
            (await station.Engine.GetStatusAsync(Ct)).Sources!.All(s => s.State == ConnectionState.Connected)
        );
        return (run, cts, aprsIs, tnc);
    }

    /// <summary>What the station sent on <paramref name="source"/> besides objects' own beacons.</summary>
    private static List<AprsPacket> Answers(EngineTests.FakeSource source) =>
        [.. source.Sent.Select(l => AprsPacket.Parse(l)).Where(p => p.Payload is not ObjectReport)];

    [Fact]
    public async Task WhereAndWhatStatusAreAnsweredOnTheSourceTheyCameFrom()
    {
        using var station = Create();
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :?APRSP");
            await WaitUntil(() => Task.FromResult(Answers(tnc).Any(p => p.Payload is PositionReport)));
            var position = Assert.IsType<PositionReport>(Answers(tnc).First(p => p.Payload is PositionReport).Payload);
            Assert.Equal(49.0583, Math.Round(position.Position.Latitude, 4));

            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :?APRSS");
            await WaitUntil(() => Task.FromResult(Answers(tnc).Any(p => p.Payload is StatusReport)));
            Assert.Equal("QRV on 146.52", Answers(tnc).Select(p => p.Payload).OfType<StatusReport>().Single().Text);
            Assert.Empty(Answers(aprsIs)); // back the way it came
            Assert.Empty(await station.Engine.GetConversationsAsync(Ct)); // a query, not a message (behavior 7)
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AGeneralQueryIsAnsweredAfterAWaitAndOnlyOnce()
    {
        using var station = Create();
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("N0ABC>APRS,WIDE1-1:?APRS?");
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            Assert.Empty(Answers(tnc)); // not straight away: every station hears it

            station.Time.Advance(TimeSpan.FromSeconds(61));
            await WaitUntil(() => Task.FromResult(Answers(tnc).Any(p => p.Payload is PositionReport)));
            var answered = Answers(tnc).Count;

            await tnc.Write("N0ABC>APRS,WIDE1-1,WIDE2*:?APRS?"); // a digipeated copy
            station.Time.Advance(TimeSpan.FromSeconds(61));
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            Assert.Equal(answered, Answers(tnc).Count);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ObjectsAndTheStationsHeardDirectAreAnswered()
    {
        using var station = Create();
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("W1AW-9>APRS:!4903.50N/07201.75W>"); // heard direct
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :?APRSD");
            await WaitUntil(() =>
                Task.FromResult(
                    Answers(tnc)
                        .Any(p =>
                            p.Payload is MessagePayload { Text: var t }
                            && t.StartsWith("Directs=", StringComparison.Ordinal)
                        )
                )
            );
            Assert.Contains(
                Answers(tnc).Select(p => p.Payload).OfType<MessagePayload>(),
                m => m.Text.Contains("W1AW-9", StringComparison.Ordinal)
            );

            var before = tnc.Sent.Count(l => l.Contains(";NETCTL", StringComparison.Ordinal));
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :?APRSO");
            await WaitUntil(() =>
                Task.FromResult(tnc.Sent.Count(l => l.Contains(";NETCTL", StringComparison.Ordinal)) > before)
            );
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(false, true, true)] // the switch off
    [InlineData(true, false, true)] // Transmit off
    [InlineData(true, true, false)] // the position not yet sent by hand
    public async Task NothingIsAnsweredWhenTheStationMayNot(bool answer, bool transmit, bool confirmed)
    {
        using var station = Create(answer, transmit, confirmed);
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await tnc.Write("NZ0P-9>APRS,WIDE1-1::NZ0P-10  :?APRSP");
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);

            Assert.DoesNotContain(Answers(tnc), p => p.Payload is PositionReport);
            Assert.DoesNotContain(Answers(aprsIs), p => p.Payload is PositionReport);
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
