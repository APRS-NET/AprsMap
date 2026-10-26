// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsNet.Ax25;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The digipeater (docs/features/F3.5-digipeater.md, acceptance tests 1–5), against a fake TNC.</summary>
public sealed class DigipeaterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    /// <summary>A TNC that hears what the test gives it and keeps the frames the station sends.</summary>
    private sealed class FakeTnc : IPacketSource, IPacketSender, IFrameSender
    {
        private readonly Channel<AprsPacket> _heard = Channel.CreateUnbounded<AprsPacket>();

        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<Ax25Frame> Sent { get; } = new();

        public ValueTask Hear(string tnc2) => _heard.Writer.WriteAsync(AprsPacket.Parse(tnc2), Ct);

        public Task<PacketSourceLogin> StartAsync(
            Action<PacketSourceEvent> stateChanged,
            CancellationToken cancellationToken
        )
        {
            Connected.TrySetResult();
            return Task.FromResult(new PacketSourceLogin("tnc", false));
        }

        public async IAsyncEnumerable<AprsPacket> ReadPacketsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await foreach (var packet in _heard.Reader.ReadAllAsync(cancellationToken))
            {
                yield return packet;
            }
        }

        public ValueTask SetFilterAsync(string filter, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask SendAsync(AprsPacket packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask SendFrameAsync(Ax25Frame frame, CancellationToken cancellationToken)
        {
            Sent.Enqueue(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Station(Engine Engine, FakeTnc Tnc, Task Run, CancellationTokenSource Cts) : IAsyncDisposable
    {
        public ConcurrentQueue<PacketView> Listed { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await Cts.CancelAsync();
            await Run;
            Cts.Dispose();
            Engine.Dispose();
        }
    }

    private static async Task<Station> Start(DigipeatMode mode, bool transmit = true, string? aliases = null)
    {
        var tnc = new SourceSettings("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1")
        {
            Digipeat = mode,
            DigipeatAliases = aliases,
        };
        var home = InMemorySettingsStore.Profile("N0TEST") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Licensed = true,
            SourceIds = [AprsIs.Id, tnc.Id],
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
        var factory = new EngineTests.FakeSourceFactory();
        var fake = new FakeTnc();
        factory.Give(tnc.Id, fake);
        var aprsIs = factory.Enqueue("T2TEST", verified: true);
        var engine = new Engine(
            Options.Create(new EngineOptions()),
            factory,
            store,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-10-26T12:00:00Z", null)),
            NullLogger<Engine>.Instance
        );
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await aprsIs.Connected.Task.WaitAsync(Ct);
        await fake.Connected.Task.WaitAsync(Ct);
        await WaitUntil(async () =>
            (await engine.GetStatusAsync(Ct)).Sources?.FirstOrDefault(s => s.Id == "tnc")
                is { State: ConnectionState.Connected }
        );
        var station = new Station(engine, fake, run, cts);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var packet in engine.WatchPacketsAsync(null, cts.Token))
                    {
                        station.Listed.Enqueue(packet);
                    }
                }
                catch (OperationCanceledException) { }
            },
            Ct
        );
        return station;
    }

    private static string PathOf(Ax25Frame frame) =>
        string.Join(',', (frame.ToAprsPacket().Path ?? []).Select(p => p.ToString()));

    /// <summary>Gives the TNC a packet, then one that's never repeated, so what's been sent by then is all of it.</summary>
    private static async Task HearAndSettle(Station station, string tnc2)
    {
        await station.Tnc.Hear(tnc2);
        var before = station.Listed.Count(p => p.Source == "N0LAST");
        await station.Tnc.Hear("N0LAST>APRS:>marker " + before);
        await WaitUntil(() => Task.FromResult(station.Listed.Count(p => p.Source == "N0LAST") > before));
    }

    [Fact]
    public async Task AFillInRepeatsWide1ButNotWide2()
    {
        await using var station = await Start(DigipeatMode.FillIn);

        await HearAndSettle(station, "N0CAR-9>APRS,WIDE1-1,WIDE2-1:!4903.50N/07201.75W>");
        await HearAndSettle(station, "N0FAR-9>APRS,WIDE2-2:!4904.50N/07201.75W>");

        var sent = Assert.Single(station.Tnc.Sent);
        Assert.Equal("N0CAR-9", sent.ToAprsPacket().Source?.Value);
        Assert.StartsWith("N0TEST*", PathOf(sent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWideAreaDigipeaterRepeatsWide2AndTrapsWide3()
    {
        await using var station = await Start(DigipeatMode.Wide);

        await HearAndSettle(station, "N0CAR-9>APRS,WIDE2-2:!4903.50N/07201.75W>");
        await HearAndSettle(station, "N0FAR-9>APRS,WIDE3-3:!4904.50N/07201.75W>");

        var sent = station.Tnc.Sent.ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal("N0TEST*,WIDE2-1", PathOf(sent[0]));
        Assert.StartsWith("N0TEST*", PathOf(sent[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("WIDE3-2", PathOf(sent[1]), StringComparison.Ordinal); // repeated once, no further
    }

    [Fact]
    public async Task NothingIsRepeatedWithTransmitOffNorOwnFramesNorDuplicates()
    {
        await using (var off = await Start(DigipeatMode.FillIn, transmit: false))
        {
            await HearAndSettle(off, "N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>");
            Assert.Empty(off.Tnc.Sent);
        }

        await using var on = await Start(DigipeatMode.FillIn);
        await HearAndSettle(on, "N0TEST>APRS,WIDE1-1:!4903.50N/07201.75W-"); // the station's own
        await HearAndSettle(on, "N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>");
        await HearAndSettle(on, "N0CAR-9>APRS,N0DIGI*,WIDE1-1:!4903.50N/07201.75W>"); // the same, via another digi
        var sent = Assert.Single(on.Tnc.Sent);
        Assert.Equal("N0CAR-9", sent.ToAprsPacket().Source?.Value);
    }

    [Fact]
    public async Task TheRepeatedPacketIsMarkedAndCounted()
    {
        await using var station = await Start(DigipeatMode.FillIn);

        await HearAndSettle(station, "N0CAR-9>APRS,WIDE1-1:!4903.50N/07201.75W>");

        var listed = station.Listed.Single(p => p.Source == "N0CAR-9");
        Assert.True(listed.Markers.HasFlag(PacketMarkers.DigipeatedHere));
        await WaitUntil(async () =>
            (await station.Engine.GetStatusAsync(Ct)).Sources?.Single(s => s.Id == "tnc").Digipeated == 1
        );
    }

    [Fact]
    public async Task ABadAliasIsRefused()
    {
        var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );
        var settings = await engine.GetSettingsAsync(Ct);
        var tnc = new SourceSettings("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1")
        {
            Digipeat = DigipeatMode.FillIn,
            DigipeatAliases = "PLAINS, not-a-call-at-all",
        };

        var refused = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(settings with { Sources = [.. settings.SourceList, tnc] }, Ct)
        );
        Assert.Equal(EngineErrorCodes.DigipeatAliasInvalid, refused.EngineError.Code);
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
