// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Stations;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>A station's longer track and kept packets in its details (docs/features/F1.41-persistent-history.md, behaviors 8, 9).</summary>
public sealed class DetailsHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static StationDetailsViewModel Details(FakeEngine engine) =>
        new(
            engine,
            new FakeTimeProvider(T0),
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            () => UnitSettings.Metric
        );

    private static PacketView Packet(int hoursAgo) =>
        new(T0.AddHours(-hoursAgo), "N0CAR-9", "Position", "", $"N0CAR-9>APRS:!{hoursAgo}");

    [Fact]
    public async Task SevenDaysAsksForTheTrackAndAnotherStationGoesBackToLive()
    {
        var engine = new FakeEngine
        {
            LongTrack = [new TrackPoint(49, -72, T0.AddDays(-6)), new TrackPoint(49.1, -72, T0)],
        };
        var details = Details(engine);
        await details.ShowAsync("N0CAR-9");
        Assert.Equal(["Live", "24 hours", "7 days"], details.TrackSpans.Select(s => s.Label));
        Assert.Null(details.LongTrack);

        details.TrackSpans[2].IsSelected = true;
        await WaitUntil(() => details.LongTrack is not null);

        Assert.Equal(("N0CAR-9", T0.AddDays(-7)), engine.TrackQueries[^1]);
        Assert.Equal(2, details.LongTrack!.Points.Count);
        await details.ShowAsync("N0BIKE-7");
        Assert.Equal((TrackSpan.Live, (LongTrack?)null), (details.TrackSpan, details.LongTrack));
        Assert.True(details.TrackSpans[0].IsSelected);
    }

    [Fact]
    public async Task KeptPacketsComeAPageAtATimeNewestFirst()
    {
        var engine = new FakeEngine { KeptPackets = [.. Enumerable.Range(1, 150).Select(Packet)] };
        var details = Details(engine);
        await details.ShowAsync("N0CAR-9");
        Assert.Empty(details.KeptPackets);

        await details.ShowKeptPacketsCommand.ExecuteAsync(null);
        Assert.Equal(StationDetailsViewModel.KeptPage, details.KeptPackets.Count);
        Assert.Equal("N0CAR-9>APRS:!1", details.KeptPackets[0].Raw);
        Assert.True(details.HasOlderPackets);
        Assert.False(details.ShowKeptPacketsCommand.CanExecute(null));

        await details.OlderPacketsCommand.ExecuteAsync(null);
        Assert.Equal(150, details.KeptPackets.Count);
        Assert.False(details.HasOlderPackets);

        await details.ShowAsync("N0BIKE-7");
        Assert.Empty(details.KeptPackets);
        Assert.True(details.ShowKeptPacketsCommand.CanExecute(null));
    }

    [Fact]
    public async Task NoKeptPacketsSaysSo()
    {
        var details = Details(new FakeEngine());
        await details.ShowAsync("N0CAR-9");

        await details.ShowKeptPacketsCommand.ExecuteAsync(null);

        Assert.Equal("No packets are kept for this station.", details.KeptPacketsNote);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
}
