// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Position and status beacons (docs/features/F2.2-beacons.md, acceptance tests 1–7).</summary>
public sealed class BeaconTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SourceSettings AprsIs = InMemorySettingsStore.Rotate;

    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "127.0.0.1");

    private static readonly PositionBeaconSettings Fixed = new(
        On: true,
        Schedule: PositionBeaconSchedule.Fixed,
        IntervalMinutes: 30
    );

    private static readonly PositionBeaconSettings Decay = new(On: true, Schedule: PositionBeaconSchedule.Decay);

    private sealed record Station(Engine Engine, EngineTests.FakeSourceFactory Sources, FakeTimeProvider Time)
        : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }

    private static Station Create(
        PositionBeaconSettings? position = null,
        StatusBeaconSettings? status = null,
        bool confirmed = true,
        bool transmit = true,
        SourceSettings[]? uses = null
    )
    {
        var home = InMemorySettingsStore.Profile("N0TEST") with
        {
            Passcode = 12345,
            Latitude = 49.0583,
            Longitude = -72.0292,
            Comment = "Home",
            Licensed = true,
            PositionConfirmed = confirmed,
            SourceIds = [.. (uses ?? [AprsIs, Tnc]).Select(s => s.Id)],
            PositionBeacon = position,
            StatusBeacon = status,
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
        var profile = (await station.Engine.GetSettingsAsync(Ct)).GetActiveProfile()!;
        await WaitUntil(async () =>
            (await station.Engine.GetStatusAsync(Ct)).Sources!.All(s => s.State == ConnectionState.Connected)
            && (await station.Engine.GetStatusAsync(Ct)).Sources!.Count == profile.SourceIds.Count
        );
        return (run, cts, aprsIs, tnc);
    }

    /// <summary>Moves the clock on and gives the beaconer the time to act on it.</summary>
    private static async Task Advance(Station station, TimeSpan by)
    {
        station.Time.Advance(by);
        await Settle();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
        }
    }

    private static int Positions(EngineTests.FakeSource source) =>
        source.Sent.Count(p => p.Contains(":=", StringComparison.Ordinal));

    private static int Statuses(EngineTests.FakeSource source) =>
        source.Sent.Count(p => p.Contains(":>", StringComparison.Ordinal));

    [Fact]
    public async Task BeaconsAreOffByDefaultAndWaitForAPositionSentByHand()
    {
        Assert.False(InMemorySettingsStore.Profile("N0NEW").EffectivePositionBeacon.On);
        Assert.False(InMemorySettingsStore.Profile("N0NEW").EffectiveStatusBeacon.On);
        using var station = Create(Fixed, new StatusBeaconSettings(On: true, Text: "QRV"), confirmed: false);
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            await Advance(station, TimeSpan.FromHours(2));

            Assert.Empty(aprsIs.Sent);
            Assert.Empty(tnc.Sent);
            var held = (await station.Engine.GetStatusAsync(Ct)).Transmit!.BeaconHeld;
            Assert.Equal(EngineErrorCodes.TransmitPositionUnconfirmed, held?.Code);

            await station.Engine.SendPositionAsync(null, Ct); // by hand: beaconing starts
            await Advance(station, Beaconer.Spacing); // the status, overdue, after the spacing
            Assert.Equal((1, 1), (Positions(aprsIs), Statuses(aprsIs)));
            await Advance(station, TimeSpan.FromMinutes(30)); // the position's interval, from the one sent by hand
            Assert.Null((await station.Engine.GetStatusAsync(Ct)).Transmit!.BeaconHeld);
            Assert.Equal((2, 1), (Positions(aprsIs), Statuses(aprsIs)));
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AFixedBeaconGoesOutEveryIntervalOnlyOnTheSourcesTickedInSendVia()
    {
        using var station = Create(Fixed with { SourceIds = [AprsIs.Id] });
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            var next = (await station.Engine.GetStatusAsync(Ct)).Transmit!.NextBeacon;
            Assert.Equal(
                (BeaconKind.Position, BeaconDueReason.Start, AprsIs.Id),
                (next!.Kind, next.Reason, next.SourceId)
            );
            Assert.True(next.At <= station.Time.GetUtcNow() + Beaconer.MaxStartWait);

            await Advance(station, Beaconer.MaxStartWait);
            Assert.Equal([$"N0TEST>{Brand.Tocall}:=4903.50N/07201.75W-Home"], aprsIs.Sent);

            await Advance(station, TimeSpan.FromMinutes(29));
            Assert.Equal(1, Positions(aprsIs));
            await Advance(station, TimeSpan.FromMinutes(1));
            Assert.Equal(2, Positions(aprsIs));
            Assert.Equal(
                BeaconDueReason.Interval,
                (await station.Engine.GetStatusAsync(Ct)).Transmit!.NextBeacon!.Reason
            );
            Assert.Empty(tnc.Sent);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task DecayDoublesFromThirtySecondsOnAprsIs()
    {
        using var station = Create(Decay, uses: [AprsIs]);
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            await Advance(station, TimeSpan.FromSeconds(3)); // the first, after at most a tenth of the first step
            Assert.Equal(1, Positions(aprsIs));
            foreach (
                var (wait, count) in new[]
                {
                    (30, 2),
                    (60, 3),
                    (120, 4),
                    (240, 5),
                    (480, 6),
                    (960, 7),
                    (1800, 8),
                    (1800, 9),
                }
            )
            {
                await Advance(station, TimeSpan.FromSeconds(wait - 1));
                Assert.Equal(count - 1, Positions(aprsIs));
                await Advance(station, TimeSpan.FromSeconds(1));
                Assert.Equal(count, Positions(aprsIs));
            }
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task OnATncDecayStartsAtTheTenMinuteSpacing()
    {
        using var station = Create(Decay, uses: [Tnc]);
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await Advance(station, TimeSpan.FromMinutes(1));
            Assert.Equal(1, Positions(tnc));
            Assert.StartsWith($"N0TEST>{Brand.Tocall},WIDE1-1,WIDE2-1:=", tnc.Sent.Single(), StringComparison.Ordinal);

            await Advance(station, TimeSpan.FromMinutes(8));
            Assert.Equal(1, Positions(tnc));
            await Advance(station, TimeSpan.FromMinutes(2));
            Assert.Equal(2, Positions(tnc));
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task ATncNeverGetsTwoPositionsWithinTenMinutesCountingOnesSentByHand()
    {
        using var station = Create(Decay, uses: [Tnc]);
        var (run, cts, _, tnc) = await Run(station);
        using (cts)
        {
            await station.Engine.SendPositionAsync(Tnc.Id, Ct); // by hand, before the first beacon
            await Advance(station, TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(50));
            Assert.Equal(1, Positions(tnc));

            await Advance(station, TimeSpan.FromSeconds(10));
            Assert.Equal(2, Positions(tnc));
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task TransmitOffHoldsBeaconsAndTurningItOnSendsOneOfEachTenSecondsApart()
    {
        using var station = Create(
            Fixed,
            new StatusBeaconSettings(On: true, Text: "QRV on 146.52", IntervalMinutes: 60),
            transmit: false,
            uses: [AprsIs]
        );
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            await Advance(station, TimeSpan.FromMinutes(100)); // three missed intervals
            Assert.Empty(aprsIs.Sent);
            Assert.Equal(
                EngineErrorCodes.TransmitOff,
                (await station.Engine.GetStatusAsync(Ct)).Transmit!.BeaconHeld?.Code
            );

            await station.Engine.SetToggleAsync(Toggle.Transmit, true, Ct);
            await Settle();
            Assert.Single(aprsIs.Sent); // one, not three
            await Advance(station, TimeSpan.FromSeconds(9));
            Assert.Single(aprsIs.Sent);
            await Advance(station, TimeSpan.FromSeconds(1));
            Assert.Equal((1, 1), (Positions(aprsIs), Statuses(aprsIs)));
            Assert.Contains($"N0TEST>{Brand.Tocall}:>QRV on 146.52", aprsIs.Sent);

            await Advance(station, TimeSpan.FromMinutes(20));
            Assert.Equal(2, aprsIs.Sent.Count);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task PhgAndCompressedPositionsGoOutAsAprsNetWritesThem()
    {
        using var station = Create(Fixed with { Phg = new PhgSettings(25, 20, 3) }, uses: [AprsIs]);
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            await Advance(station, Beaconer.MaxStartWait);
            Assert.Equal([$"N0TEST>{Brand.Tocall}:=4903.50N/07201.75W-PHG5130Home"], aprsIs.Sent);

            var settings = await station.Engine.GetSettingsAsync(Ct);
            var profile = settings.GetActiveProfile()!;
            await station.Engine.SaveSettingsAsync(
                settings with
                {
                    Profiles = [profile with { PositionBeacon = Fixed with { Compressed = true } }],
                },
                Ct
            );
            await station.Engine.SendPositionAsync(null, Ct); // the format changed what's sent: confirmed again by hand
            var compressed = AprsPacket.Parse(aprsIs.Sent.Last());
            Assert.True(Assert.IsType<PositionReport>(compressed.Payload).IsCompressed);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task SendStatusNowSendsTheTextWithTheTimeOrGridSquareOnTheStatussSources()
    {
        var status = new StatusBeaconSettings(Text: "QRV on 146.52", SourceIds: [AprsIs.Id], Add: StatusAddition.Time);
        using var station = Create(status: status);
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            var sent = await station.Engine.SendStatusAsync(null, Ct);

            Assert.Equal(AprsIs.Id, Assert.Single(sent).SourceId); // Send via (F2.11 behavior 5)
            Assert.Equal([$"N0TEST>{Brand.Tocall}:>171200zQRV on 146.52"], aprsIs.Sent);
            Assert.Empty(tnc.Sent);

            var settings = await station.Engine.GetSettingsAsync(Ct);
            var profile = settings.GetActiveProfile()!;
            await station.Engine.SaveSettingsAsync(
                settings with
                {
                    Profiles = [profile with { StatusBeacon = status with { Add = StatusAddition.GridSquare } }],
                },
                Ct
            );
            await station.Engine.SendStatusAsync(null, Ct);

            var grid = Assert.IsType<StatusReport>(AprsPacket.Parse(aprsIs.Sent.Last()).Payload);
            var expected = MaidenheadLocator.FromPosition(49.0583, -72.0292, 6).Value.ToUpperInvariant();
            Assert.Equal((expected, "QRV on 146.52"), (grid.MaidenheadLocator, grid.Text.Trim()));
            Assert.Equal(new AprsSymbol('/', '-'), grid.Symbol);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AskingQruServersSendsTheGroupOnceWithoutAnIdOnEverySource()
    {
        using var station = Create();
        var (run, cts, aprsIs, tnc) = await Run(station);
        using (cts)
        {
            var sent = await station.Engine.AskQruAsync(" info ", Ct);

            Assert.Equal(2, sent.Count);
            Assert.Equal([$"N0TEST>{Brand.Tocall}::QRU      :INFO"], aprsIs.Sent); // no {id: never acked or retried
            Assert.EndsWith("::QRU      :INFO", Assert.Single(tnc.Sent), StringComparison.Ordinal);
            await Advance(station, TimeSpan.FromMinutes(5));
            Assert.Single(aprsIs.Sent);
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("TENLETTERS")]
    [InlineData("REP{1")]
    public async Task AQruGroupIsOneToNineLettersDigitsAndDashes(string group)
    {
        using var station = Create();

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() => station.Engine.AskQruAsync(group, Ct));

        Assert.Equal(EngineErrorCodes.QruGroupInvalid, error.EngineError.Code);
    }

    [Fact]
    public async Task WithTransmitOffNoQruQueryGoesOut()
    {
        using var station = Create(transmit: false);
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            var error = await Assert.ThrowsAsync<EngineOperationException>(() =>
                station.Engine.AskQruAsync("INFO", Ct)
            );

            Assert.Equal(EngineErrorCodes.TransmitOff, error.EngineError.Code);
            Assert.Empty(aprsIs.Sent);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task SendStatusNowRefusesAnEmptyStatus()
    {
        using var station = Create(status: StatusBeaconSettings.Off);
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            var error = await Assert.ThrowsAsync<EngineOperationException>(() =>
                station.Engine.SendStatusAsync(null, Ct)
            );

            Assert.Equal(EngineErrorCodes.TransmitNoStatus, error.EngineError.Code);
            Assert.Empty(aprsIs.Sent);
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(StatusAddition.None, 62, null)]
    [InlineData(StatusAddition.Time, 55, null)]
    [InlineData(StatusAddition.Time, 56, EngineErrorCodes.ProfileStatusInvalid)]
    [InlineData(StatusAddition.GridSquare, 53, null)]
    [InlineData(StatusAddition.GridSquare, 54, EngineErrorCodes.ProfileStatusInvalid)]
    public async Task AStatusHasRoomForLessWithTheTimeOrGridSquare(StatusAddition add, int length, string? code)
    {
        using var station = Create();
        var settings = await station.Engine.GetSettingsAsync(Ct);
        var profile = settings.GetActiveProfile()! with
        {
            StatusBeacon = new StatusBeaconSettings(Text: new string('x', length), Add: add),
        };

        var save = station.Engine.SaveSettingsAsync(settings with { Profiles = [profile] }, Ct);

        if (code is null)
        {
            await save;
        }
        else
        {
            Assert.Equal(code, (await Assert.ThrowsAsync<EngineArgumentException>(() => save)).EngineError.Code);
        }
    }

    [Theory]
    [InlineData(21, "QRV")]
    [InlineData(1, " ")]
    [InlineData(1, "Café")]
    public async Task SavedStatusTextsAreAtMostTwentyEachOneAStatusCanCarry(int count, string text)
    {
        using var station = Create();
        var settings = await station.Engine.GetSettingsAsync(Ct);
        var saved = Enumerable.Range(0, count).Select(i => count == 1 ? text : $"{text} {i}").ToList();
        var profile = settings.GetActiveProfile()! with { StatusBeacon = new StatusBeaconSettings(SavedTexts: saved) };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            station.Engine.SaveSettingsAsync(settings with { Profiles = [profile] }, Ct)
        );

        Assert.Equal(EngineErrorCodes.ProfileSavedStatusInvalid, error.EngineError.Code);
    }

    [Fact]
    public async Task AMicEPositionGoesOutWithItsStatusAndSendPositionNowSendsTheSame()
    {
        using var station = Create(Fixed with { MicE = MicEStatus.EnRoute }, uses: [AprsIs]);
        var (run, cts, aprsIs, _) = await Run(station);
        using (cts)
        {
            await Advance(station, Beaconer.MaxStartWait);
            var beacon = Assert.IsType<MicEReport>(AprsPacket.Parse(Assert.Single(aprsIs.Sent)).Payload);
            Assert.Equal(MicEMessage.EnRoute, beacon.Message);
            Assert.Equal(
                (49.0583, -72.0292),
                (Math.Round(beacon.Position!.Latitude, 4), Math.Round(beacon.Position.Longitude, 4))
            );
            Assert.Equal("Home", beacon.Comment);

            await station.Engine.SendPositionAsync(null, Ct);
            var byHand = Assert.IsType<MicEReport>(AprsPacket.Parse(aprsIs.Sent.Last()).Payload);
            Assert.Equal(MicEMessage.EnRoute, byHand.Message);
            await cts.CancelAsync();
            await run;
        }
    }

    [Theory]
    [InlineData(MicEStatus.OffDuty, MicEMessage.OffDuty)]
    [InlineData(MicEStatus.Priority, MicEMessage.Priority)]
    [InlineData(MicEStatus.Returning, MicEMessage.Returning)]
    public void EachStatusIsItsMicEMessage(MicEStatus status, MicEMessage message)
    {
        var profile = InMemorySettingsStore.Profile("N0TEST") with
        {
            Latitude = 49.0583,
            Longitude = -72.0292,
            PositionBeacon = Fixed with { MicE = status },
        };

        var packet = AprsPacket.Parse(
            PacketAddress.Parse("N0TEST", Brand.Tocall, []).Create(Beaconer.PositionReport(profile)).ToString()
        );

        Assert.Equal(message, Assert.IsType<MicEReport>(packet.Payload).Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AMicEPositionCantBeCompressedOrCarryPhg(bool compressed, bool phg)
    {
        using var station = Create(Fixed, uses: [AprsIs]);
        var settings = await station.Engine.GetSettingsAsync(Ct);
        var profile = settings.GetActiveProfile()!;
        var beacon = Fixed with
        {
            MicE = MicEStatus.InService,
            Compressed = compressed,
            Phg = phg ? new PhgSettings(25, 20, 3) : null,
        };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            station.Engine.SaveSettingsAsync(
                settings with
                {
                    Profiles = [profile with { PositionBeacon = beacon }],
                },
                Ct
            )
        );

        Assert.Equal(EngineErrorCodes.ProfileMicEInvalid, error.EngineError.Code);
    }

    [Theory]
    [InlineData(
        9,
        StatusBeaconSettings.DefaultIntervalMinutes,
        "",
        null,
        false,
        EngineErrorCodes.ProfileBeaconIntervalInvalid
    )]
    [InlineData(30, 24 * 60 + 1, "", null, false, EngineErrorCodes.ProfileBeaconIntervalInvalid)]
    [InlineData(
        30,
        60,
        "This status is sixty-three characters long, one more than fits!",
        null,
        false,
        EngineErrorCodes.ProfileStatusInvalid
    )]
    [InlineData(30, 60, "Café", null, false, EngineErrorCodes.ProfileStatusInvalid)]
    [InlineData(30, 60, "", 5, false, EngineErrorCodes.ProfilePhgInvalid)]
    [InlineData(30, 60, "", 25, true, EngineErrorCodes.ProfilePhgInvalid)]
    public async Task UnsendableBeaconSettingsAreRefusedOnSave(
        int positionMinutes,
        int statusMinutes,
        string status,
        int? phgWatts,
        bool compressed,
        string code
    )
    {
        using var station = Create();
        var settings = await station.Engine.GetSettingsAsync(Ct);
        var profile = settings.GetActiveProfile()! with
        {
            PositionBeacon = new PositionBeaconSettings(
                IntervalMinutes: positionMinutes,
                Compressed: compressed,
                Phg: phgWatts is { } watts ? new PhgSettings(watts, 20, 3) : null
            ),
            StatusBeacon = new StatusBeaconSettings(Text: status, IntervalMinutes: statusMinutes),
        };

        var refused = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            station.Engine.SaveSettingsAsync(settings with { Profiles = [profile] }, Ct)
        );

        Assert.Equal(code, refused.EngineError.Code);
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
