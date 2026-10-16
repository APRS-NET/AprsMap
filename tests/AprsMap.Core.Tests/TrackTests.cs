// SPDX-License-Identifier: MIT
using System.Globalization;
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>Position history (docs/features/F1.5-tracks.md).</summary>
public sealed class TrackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A position report <paramref name="step"/> × 185 m north of the first.</summary>
    private static string At(int step, string name = "N0CALL-9", string ambiguity = "") =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{name}>APRS,TCPIP*,qAC,T2TEST:!49{3.5 + (step * 0.1):00.00}N/07201.75W>{ambiguity}"
        );

    private static void Apply(StationStore store, string line, DateTimeOffset at) =>
        store.Apply(AprsPacket.Parse(line), at);

    [Fact]
    public void MovingPositionsBuildAHistoryOldestFirst()
    {
        var store = new StationStore();

        for (var i = 0; i < 3; i++)
        {
            Apply(store, At(i), T0.AddMinutes(i));
        }

        var track = store.GetTrack("N0CALL-9");
        Assert.Equal(3, track.Count);
        Assert.Equal([T0, T0.AddMinutes(1), T0.AddMinutes(2)], track.Select(p => p.Time));
        Assert.True(track[2].Latitude > track[0].Latitude);
    }

    [Fact]
    public void AReportWithin20MetresMovesTheLastPointsTimeOn()
    {
        var store = new StationStore();
        Apply(store, At(0), T0);

        // 0.01' of latitude is about 18.5 m.
        Apply(store, "N0CALL-9>APRS,TCPIP*,qAC,T2TEST:!4903.51N/07201.75W>", T0.AddMinutes(1));

        var point = Assert.Single(store.GetTrack("N0CALL-9"));
        Assert.Equal(T0.AddMinutes(1), point.Time);
    }

    [Fact]
    public void ATrackKeepsItsLengthLimitDroppingTheOldest()
    {
        var store = new StationStore();

        for (var i = 0; i < StationAging.DefaultTrackPoints + 1; i++)
        {
            Apply(store, At(i), T0.AddSeconds(i));
        }

        var track = store.GetTrack("N0CALL-9");
        Assert.Equal(StationAging.DefaultTrackPoints, track.Count);
        Assert.Equal(T0.AddSeconds(1), track[0].Time);
    }

    [Fact]
    public void PointsOlderThanTheTracksAgeDropOnTheSweep()
    {
        var store = new StationStore();
        Apply(store, At(0), T0);
        Apply(store, At(1), T0.AddMinutes(90));

        store.Sweep(T0.AddMinutes(121), StationAging.Default);

        var point = Assert.Single(store.GetTrack("N0CALL-9"));
        Assert.Equal(T0.AddMinutes(90), point.Time);
    }

    [Fact]
    public void ALongerSettingKeepsMore()
    {
        var store = new StationStore();
        store.Sweep(T0, StationAging.Default with { TrackPoints = 2 });

        for (var i = 0; i < 4; i++)
        {
            Apply(store, At(i), T0.AddSeconds(i));
        }

        Assert.Equal(2, store.GetTrack("N0CALL-9").Count);
    }

    [Fact]
    public void AnAmbiguousPositionIsNotAdded()
    {
        var store = new StationStore();
        Apply(store, At(0), T0);

        Apply(store, "N0CALL-9>APRS,TCPIP*,qAC,T2TEST:!49  .  N/0720 .  W>", T0.AddMinutes(1));

        Assert.Single(store.GetTrack("N0CALL-9"));
    }

    [Fact]
    public void ClearTrackKeepsOnlyTheLatestPoint()
    {
        var store = new StationStore();
        for (var i = 0; i < 3; i++)
        {
            Apply(store, At(i), T0.AddMinutes(i));
        }

        Assert.True(store.ClearTrack("N0CALL-9"));

        var point = Assert.Single(store.GetTrack("N0CALL-9"));
        Assert.Equal(T0.AddMinutes(2), point.Time);
        Assert.False(store.ClearTrack("NOBODY"));
    }

    [Fact]
    public void AWatcherGetsEveryTrackFirstThenAddedPoints()
    {
        var store = new StationStore();
        Apply(store, At(0), T0);
        Apply(store, At(1), T0.AddMinutes(1));

        var watcher = store.Subscribe(out _, out var tracks);
        var snapshot = Assert.Single(tracks);
        Assert.True(snapshot.Reset);
        Assert.Equal(2, snapshot.Added.Count);

        Apply(store, At(2), T0.AddMinutes(2));

        var update = Assert.Single(store.Drain(watcher)!.Tracks!);
        Assert.False(update.Reset);
        Assert.Equal(T0.AddMinutes(2), Assert.Single(update.Added).Time);
    }

    [Fact]
    public void AppliedInOrderTheUpdatesGiveTheStoresTrack()
    {
        var store = new StationStore();
        store.Sweep(T0, StationAging.Default with { TrackPoints = 3 });
        var watcher = store.Subscribe(out _, out _);
        var viewer = new List<TrackPoint>();

        for (var i = 0; i < 10; i++)
        {
            Apply(store, At(i), T0.AddSeconds(i));
            if (i % 4 == 0)
            {
                Follow(viewer, store.Drain(watcher));
            }
        }
        store.ClearTrack("N0CALL-9");
        Apply(store, At(20), T0.AddSeconds(20));
        Follow(viewer, store.Drain(watcher));

        Assert.Equal(store.GetTrack("N0CALL-9"), viewer);

        static void Follow(List<TrackPoint> viewer, StationBatch? batch)
        {
            foreach (var update in batch?.Tracks ?? [])
            {
                if (update.Reset)
                {
                    viewer.Clear();
                }
                viewer.AddRange(update.Added);
                viewer.RemoveRange(0, update.Dropped);
            }
        }
    }

    [Fact]
    public void ARemovedStationTakesItsTrackAndComesBackWithANewOne()
    {
        var store = new StationStore();
        Apply(store, At(0), T0);
        Apply(store, At(1), T0.AddMinutes(1));
        var watcher = store.Subscribe(out _, out _);

        store.Sweep(T0.AddMinutes(200), StationAging.Default); // removed
        var removal = store.Drain(watcher)!;
        Assert.Contains("N0CALL-9", removal.Removed);
        Assert.Null(removal.Tracks); // the viewer drops the track with the station

        // Removed and heard again before the viewer's next batch: the old track mustn't join the new one.
        store.Sweep(T0.AddMinutes(400), StationAging.Default);
        Apply(store, At(5), T0.AddMinutes(201));
        Apply(store, At(9), T0.AddMinutes(500));
        store.Sweep(T0.AddMinutes(700), StationAging.Default);
        Apply(store, At(7), T0.AddMinutes(701));
        var batch = store.Drain(watcher)!;
        Assert.DoesNotContain("N0CALL-9", batch.Removed);
        var update = Assert.Single(batch.Tracks!);
        Assert.True(update.Reset);
        Assert.Equal(T0.AddMinutes(701), Assert.Single(update.Added).Time);
    }

    [Fact]
    public void ObjectsHaveTracksToo()
    {
        var store = new StationStore();

        Apply(store, "N0CALL>APRS,TCPIP*,qAC,T2TEST:;BALLOON  *111111z4903.50N/07201.75WO", T0);
        Apply(store, "N0CALL>APRS,TCPIP*,qAC,T2TEST:;BALLOON  *111211z4904.50N/07201.75WO", T0.AddMinutes(1));

        Assert.Equal(2, store.GetTrack("BALLOON").Count);
    }
}
