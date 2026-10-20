// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Objects and items (docs/features/F2.7-objects-and-items.md, acceptance tests 1–6).</summary>
public sealed class ObjectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    private static readonly OwnObject Net = new(
        "net",
        "NETCTL",
        OwnObjectKind.Object,
        49.0583,
        -72.0292,
        Comment: "Net 7pm"
    );

    private sealed record Station(Engine Engine, EngineTests.FakeSourceFactory Sources, FakeTimeProvider Time)
        : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }

    private static Station Create(bool licensed = true)
    {
        var home = InMemorySettingsStore.Profile("N0TEST") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Licensed = licensed,
            PositionConfirmed = true,
            SourceIds = [AprsIs.Id, Tnc.Id],
        };
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home],
                home.Id,
                UnitSettings.Metric,
                Sources: [AprsIs, Tnc],
                Toggles: StationToggles.AllOn with
                {
                    Transmit = true,
                }
            )
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z", null));
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
        await WaitUntil(async () =>
            (await station.Engine.GetStatusAsync(Ct)).Sources is { Count: 2 } sources
            && sources.All(s => s.State == ConnectionState.Connected)
        );
        return (run, cts, aprsIs, tnc);
    }

    private static async Task Advance(Station station, TimeSpan by)
    {
        station.Time.Advance(by);
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
        }
    }

    private static List<string> Objects(EngineTests.FakeSource source) =>
        [
            .. source.Sent.Where(l =>
                l.Contains(":;", StringComparison.Ordinal) || l.Contains(":)", StringComparison.Ordinal)
            ),
        ];

    private static async Task<IReadOnlyList<OwnObject>> Stored(Station station) =>
        (await station.Engine.GetSettingsAsync(Ct)).GetActiveProfile()!.ObjectList;

    [Fact]
    public async Task APlacedObjectGoesOutAtOnceOnEachSourceThenDecays()
    {
        using var station = Create();
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await station.Engine.PlaceObjectAsync(Net, Ct);
            await WaitUntil(() => Task.FromResult(Objects(aprsIs).Count == 1 && Objects(tnc).Count == 1));

            Assert.Equal([$"N0TEST>{Brand.Tocall}:;NETCTL   *171200z4903.50N/07201.75W.Net 7pm"], Objects(aprsIs));
            Assert.EndsWith(":;NETCTL   *171200z4903.50N/07201.75W.Net 7pm", Objects(tnc)[0], StringComparison.Ordinal);

            await Advance(station, TimeSpan.FromSeconds(30)); // APRS-IS: 30 s, then 1 minute…
            Assert.Equal(2, Objects(aprsIs).Count);
            Assert.Single(Objects(tnc)); // a TNC waits 2 minutes first (behavior 5)
            await Advance(station, TimeSpan.FromSeconds(60));
            Assert.Equal(3, Objects(aprsIs).Count);
            await Advance(station, TimeSpan.FromSeconds(30));
            Assert.Equal(2, Objects(tnc).Count);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AnItemGoesOutWithoutATime()
    {
        using var station = Create();
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            await station.Engine.PlaceObjectAsync(
                Net with
                {
                    Name = "AID-1",
                    Kind = OwnObjectKind.Item,
                    Symbol = "/+",
                    Comment = "",
                },
                Ct
            );
            await WaitUntil(() => Task.FromResult(Objects(aprsIs).Count == 1));

            Assert.Equal([$"N0TEST>{Brand.Tocall}:)AID-1!4903.50N/07201.75W+"], Objects(aprsIs));
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task MovingOneSendsTheNewPlaceAtOnceAndStartsItsScheduleAgain()
    {
        using var station = Create();
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            await station.Engine.PlaceObjectAsync(Net, Ct);
            await WaitUntil(() => Task.FromResult(Objects(aprsIs).Count == 1));
            await Advance(station, TimeSpan.FromMinutes(5));
            var before = Objects(aprsIs).Count;

            await station.Engine.PlaceObjectAsync(Net with { Latitude = 49.1 }, Ct);
            await Advance(station, Beaconer.Spacing); // the 10 s between beacons on a source still holds
            await WaitUntil(() => Task.FromResult(Objects(aprsIs).Count == before + 1));

            var moved = Assert.IsType<ObjectReport>(AprsPacket.Parse(Objects(aprsIs)[^1]).Payload);
            Assert.Equal(49.1, Math.Round(moved.Position.Latitude, 2));
            await Advance(station, TimeSpan.FromSeconds(30)); // the decay begins again
            Assert.Equal(before + 2, Objects(aprsIs).Count);
            Assert.Single(await Stored(station));
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AKilledObjectGoesOutKilledThreeTimesThenItsGone()
    {
        using var station = Create();
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await station.Engine.PlaceObjectAsync(Net with { SourceIds = [AprsIs.Id] }, Ct);
            await WaitUntil(() => Task.FromResult(Objects(aprsIs).Count == 1));

            await station.Engine.RemoveObjectAsync("net", kill: true, Ct);
            await Advance(station, Beaconer.Spacing);
            await WaitUntil(() => Task.FromResult(Objects(aprsIs).Count == 2));
            Assert.False(Assert.Single(await Stored(station)).Live);
            await Advance(station, TimeSpan.FromSeconds(30));
            await Advance(station, TimeSpan.FromSeconds(60));

            Assert.Equal(3, Objects(aprsIs).Count(l => l.Contains(";NETCTL   _", StringComparison.Ordinal)));
            await WaitUntil(async () => (await Stored(station)).Count == 0);
            await Advance(station, TimeSpan.FromMinutes(10));
            Assert.Equal(4, Objects(aprsIs).Count); // nothing more once it's gone
            Assert.Empty(Objects(tnc)); // Send via
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ForgetRemovesOneWithoutSendingAndAnOldSaveDoesntBringItBack()
    {
        using var station = Create();
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            await station.Engine.PlaceObjectAsync(Net, Ct);
            await WaitUntil(() => Task.FromResult(Objects(aprsIs).Count == 1));
            var old = await station.Engine.GetSettingsAsync(Ct);

            await station.Engine.RemoveObjectAsync("net", kill: false, Ct);
            await station.Engine.SaveSettingsAsync(old with { Units = UnitSettings.Imperial }, Ct);
            await Advance(station, TimeSpan.FromMinutes(5));

            Assert.Single(Objects(aprsIs));
            Assert.Empty(await Stored(station));
            var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
                station.Engine.RemoveObjectAsync("net", kill: true, Ct)
            );
            Assert.Equal(EngineErrorCodes.ObjectUnknown, error.EngineError.Code);
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(OwnObjectKind.Object, "", "/.", "", 30, EngineErrorCodes.ObjectNameInvalid)]
    [InlineData(OwnObjectKind.Object, "TENCHARSXX", "/.", "", 30, EngineErrorCodes.ObjectNameInvalid)]
    [InlineData(OwnObjectKind.Object, " LEAD", "/.", "", 30, EngineErrorCodes.ObjectNameInvalid)]
    [InlineData(OwnObjectKind.Item, "AB", "/.", "", 30, EngineErrorCodes.ObjectNameInvalid)]
    [InlineData(OwnObjectKind.Item, "AID_1", "/.", "", 30, EngineErrorCodes.ObjectNameInvalid)]
    [InlineData(OwnObjectKind.Object, "NET", "\\!", "", 30, EngineErrorCodes.ObjectInvalid)] // never the emergency symbol
    [InlineData(
        OwnObjectKind.Object,
        "NET",
        "/.",
        "A comment far too long for an object to carry",
        30,
        EngineErrorCodes.ObjectInvalid
    )]
    [InlineData(OwnObjectKind.Object, "NET", "/.", "", 5, EngineErrorCodes.ObjectInvalid)]
    public async Task UnsendableObjectsAreRefused(
        OwnObjectKind kind,
        string name,
        string symbol,
        string comment,
        int interval,
        string code
    )
    {
        using var station = Create();

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            station.Engine.PlaceObjectAsync(new OwnObject("x", name, kind, 49, -72, symbol, comment, interval), Ct)
        );

        Assert.Equal(code, error.EngineError.Code);
    }

    [Fact]
    public async Task TwoObjectsCantShareANameAndAListeningProfilePlacesNone()
    {
        using var station = Create();
        await station.Engine.PlaceObjectAsync(Net, Ct);

        var taken = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            station.Engine.PlaceObjectAsync(Net with { Id = "other" }, Ct)
        );
        Assert.Equal(EngineErrorCodes.ObjectNameTaken, taken.EngineError.Code);

        using var listener = Create(licensed: false);
        await Assert.ThrowsAsync<EngineOperationException>(() => listener.Engine.PlaceObjectAsync(Net, Ct));
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
