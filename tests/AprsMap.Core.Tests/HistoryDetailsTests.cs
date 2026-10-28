// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>A station's longer track and kept packets (docs/features/F1.41-persistent-history.md, behaviors 8, 9).</summary>
public sealed class HistoryDetailsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-21T12:00:00Z", null);

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("aprsmap-details-");
    private readonly FakeTimeProvider _time = new(T0);

    public void Dispose() => _folder.Delete(recursive: true);

    private Engine Create(bool history = true) =>
        new(
            Options.Create(new EngineOptions { History = { Folder = history ? _folder.FullName : null } }),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            _time,
            NullLogger<Engine>.Instance
        );

    private static string At(int minutes) =>
        $"N0CAR-9>APRS,WIDE1-1:!49{minutes / 10:00}.{minutes % 10}0N/07201.75W>moving";

    [Fact]
    public async Task ASevenDayTrackReachesPastTheLiveOne()
    {
        using var engine = Create();
        await engine.GetSettingsAsync(Ct);
        for (var d = 0; d < 6; d++)
        {
            engine.Accept(At(d * 10)); // a position a day, kilometres apart
            engine.Accept(At(d * 10)); // the same again: a copy, left out
            _time.Advance(TimeSpan.FromDays(1));
        }
        engine.Accept(At(60));
        await engine.FlushHistoryAsync();
        engine.SweepNow(_time.GetUtcNow()); // the live track keeps its 2 hours

        var week = await engine.GetTrackAsync("N0CAR-9", _time.GetUtcNow().AddDays(-7), Ct);
        var day = await engine.GetTrackAsync("N0CAR-9", _time.GetUtcNow().AddDays(-1), Ct);

        Assert.Equal(7, week.Count);
        Assert.Equal(T0, week[0].Time);
        Assert.Equal(2, day.Count); // yesterday's and today's
        Assert.True(engine.TrackOf("N0CAR-9").Count < week.Count); // the live track is 2 hours
    }

    [Fact]
    public void PointsWithin20MetresMoveTheLastOnesTimeAndLongTracksAreThinned()
    {
        var near = new[]
        {
            new TrackPoint(49, -72, T0),
            new TrackPoint(49.0001, -72, T0.AddMinutes(1)), // about 11 m
            new TrackPoint(49.01, -72, T0.AddMinutes(2)),
        };
        var joined = Engine.LongTrack(near);
        Assert.Equal([T0.AddMinutes(1), T0.AddMinutes(2)], joined.Select(p => p.Time));

        var many = Enumerable
            .Range(0, 25_000)
            .Select(i => new TrackPoint(49 + (i * 0.001), -72, T0.AddMinutes(i)))
            .ToList();
        var thinned = Engine.LongTrack(many);
        Assert.Equal(TrackPoint.MaxLongTrack, thinned.Count);
        Assert.Equal((many[0], many[^1]), (thinned[0], thinned[^1]));
    }

    [Fact]
    public async Task KeptPacketsPageBackNewestFirst()
    {
        using var engine = Create();
        await engine.GetSettingsAsync(Ct);
        for (var i = 0; i < 5; i++)
        {
            engine.Accept(At(i));
            _time.Advance(TimeSpan.FromHours(12)); // across three days' files
        }
        await engine.FlushHistoryAsync();

        var first = await engine.GetStationPacketsAsync("N0CAR-9", null, 3, Ct);
        var next = await engine.GetStationPacketsAsync("N0CAR-9", first[^1].ReceivedAt, 3, Ct);

        Assert.Equal([T0.AddHours(48), T0.AddHours(36), T0.AddHours(24)], first.Select(p => p.ReceivedAt));
        Assert.Equal([T0.AddHours(12), T0], next.Select(p => p.ReceivedAt));
        Assert.Equal(At(4), first[0].Raw);
        Assert.Empty(await engine.GetStationPacketsAsync("N0OTHER", null, 3, Ct));
    }

    [Fact]
    public async Task WithoutAHistoryTheTrackIsTheLiveOneAndNoPacketsAreKept()
    {
        using var engine = Create(history: false);
        engine.Accept(At(0));
        _time.Advance(TimeSpan.FromMinutes(5));
        engine.Accept(At(10));

        var track = await engine.GetTrackAsync("N0CAR-9", T0.AddDays(-7), Ct);

        Assert.Equal(engine.TrackOf("N0CAR-9"), track);
        Assert.Empty(await engine.GetStationPacketsAsync("N0CAR-9", null, 10, Ct));
    }
}
