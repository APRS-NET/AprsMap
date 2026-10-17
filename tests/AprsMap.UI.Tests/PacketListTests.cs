// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Packets;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class PacketListTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PacketListViewModel Create(FakeEngine? engine = null, FakeTimeProvider? time = null) =>
        new MainViewModel(engine ?? new FakeEngine(), time ?? new FakeTimeProvider()).PacketList;

    [Fact]
    public async Task PacketsArriveInBatchesNoMoreOftenThanTheInterval()
    {
        var time = new FakeTimeProvider();
        var list = Create(time: time);

        list.Receive(FakeEngine.Packet("N1"));
        list.Receive(FakeEngine.Packet("N2"));
        list.Receive(FakeEngine.Packet("N3"));
        Assert.Equal(["N1"], list.Rows.Select(r => r.Source)); // the first at once, the rest in the next batch

        time.Advance(PacketListViewModel.BatchInterval);
        await WaitUntil(() => list.Rows.Count == 3);
        Assert.Equal(["N3", "N2", "N1"], list.Rows.Select(r => r.Source));
    }

    [Fact]
    public void PauseHoldsPacketsAndCountsThemAndResumeAddsThem()
    {
        var list = Create();
        list.Receive(FakeEngine.Packet("N1"));

        list.TogglePause();
        list.Receive(FakeEngine.Packet("N2"));
        list.Receive(FakeEngine.Packet("N3"));

        Assert.Single(list.Rows);
        Assert.Equal(2, list.NewWhilePaused);
        Assert.Equal("Paused · 2 new", list.PauseText);

        list.TogglePause();
        Assert.Equal(["N3", "N2", "N1"], list.Rows.Select(r => r.Source));
        Assert.Equal("Pause", list.PauseText);
    }

    [Fact]
    public void ScrollingDownPausesAndReturningToTheTopResumes()
    {
        var list = Create();

        list.Scrolled(atTop: false);
        Assert.True(list.IsPaused);
        list.Scrolled(atTop: true);
        Assert.False(list.IsPaused);

        list.TogglePause(); // paused by the viewer: the top doesn't resume it
        list.Scrolled(atTop: true);
        Assert.True(list.IsPaused);
    }

    [Fact]
    public void SelectingARowPausesLikeScrollingAndTheTopResumes()
    {
        var list = Create();

        list.RowSelected();
        Assert.True(list.IsPaused);
        list.RowSelected(); // another row: still paused
        Assert.True(list.IsPaused);
        list.Scrolled(atTop: true);
        Assert.False(list.IsPaused);

        list.TogglePause(); // paused by the viewer: selecting and the top leave it paused
        list.RowSelected();
        list.Scrolled(atTop: true);
        Assert.True(list.IsPaused);
    }

    [Fact]
    public void ResumingRaisesResumedSoThePanelGoesBackToTheTop()
    {
        var list = Create();
        var resumed = 0;
        list.Resumed += (_, _) => resumed++;

        list.TogglePause();
        Assert.Equal(0, resumed); // pausing doesn't move the list
        list.TogglePause();
        Assert.Equal(1, resumed);
        list.Scrolled(atTop: false);
        list.Scrolled(atTop: true);
        Assert.Equal(2, resumed);
    }

    [Fact]
    public async Task ChangingAFilterEmptiesTheListAndAsksTheEngineAgainWithIt()
    {
        var engine = new FakeEngine();
        var list = Create(engine);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var watching = list.WatchAsync(cts.Token);
        await WaitUntil(() => engine.PacketSubscriptions == 1);
        engine.Push(FakeEngine.Packet("N0CALL"));
        await WaitUntil(() => list.Rows.Count == 1);

        list.IssuesOnly = true;
        list.SelectedKind = list.Kinds.Single(k => k.Kind == PacketKind.Position);
        list.Station = "n1call";

        Assert.Empty(list.Rows);
        Assert.Equal("No packets match the filters.", list.EmptyText);
        Assert.True(list.IsFiltered);
        await WaitUntil(() => engine.PacketFilter?.Station == "n1call");
        Assert.True(engine.PacketFilter!.IssuesOnly);
        Assert.Equal([PacketKind.Position], engine.PacketFilter.Kinds);

        list.ClearFilters();
        Assert.False(list.IsFiltered);
        await WaitUntil(() => engine.PacketFilter is { IsEmpty: true });
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
    }

    [Fact]
    public async Task TheTextFilterWaitsForTypingToStop()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider();
        var list = Create(engine, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var watching = list.WatchAsync(cts.Token);
        await WaitUntil(() => engine.PacketSubscriptions == 1);

        list.FilterText = "N0";
        list.FilterText = "N0CALL";
        Assert.Equal(1, engine.PacketSubscriptions);

        time.Advance(PacketListViewModel.TypingDelay);
        await WaitUntil(() => engine.PacketSubscriptions == 2);
        Assert.Equal("N0CALL", engine.PacketFilter?.Text);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
    }

    [Fact]
    public void TheTypeFilterOffersEveryKindAndAll()
    {
        var list = Create();

        Assert.Null(list.Kinds[0].Kind);
        Assert.Equal("All types", list.Kinds[0].Name);
        Assert.Equal(Enum.GetValues<PacketKind>().Length + 1, list.Kinds.Count);
        Assert.DoesNotContain(list.Kinds, k => k.Name.StartsWith('⟦'));
    }

    [Fact]
    public void AnIssueIsShownInWordsWithItsPartOfTheRawTextMarked()
    {
        const string Raw = "N0CALL>APRS:!9999.99N/99999.99W-";
        var packet = FakeEngine.Packet("N0CALL") with
        {
            Raw = Raw,
            Issues =
            [
                new EngineError(
                    "aprs.parse.invalid-latitude",
                    "English",
                    new Dictionary<string, string>
                    {
                        [EngineErrorArgs.Offset] = "13",
                        [EngineErrorArgs.Severity] = EngineErrorArgs.SeverityError,
                    }
                ),
            ],
        };

        var row = new PacketRow(packet, Localizer.Instance, TimeZoneInfo.Utc);

        Assert.True(row.HasIssues);
        var issue = Assert.Single(row.Issues);
        Assert.Equal("Error", issue.Severity);
        Assert.Equal("The latitude isn't valid.", issue.Text);
        Assert.Equal("N0CALL>APRS:!", row.RawBefore);
        Assert.Equal("9999.99N/999", row.RawFault);
        Assert.Equal(Raw, row.RawBefore + row.RawFault + row.RawAfter);
    }

    [Fact]
    public void ACleanPacketHasNoMarkedPart()
    {
        var row = new PacketRow(FakeEngine.Packet("N0CALL"), Localizer.Instance, TimeZoneInfo.Utc);

        Assert.Equal(row.Raw, row.RawBefore);
        Assert.Empty(row.RawFault);
        Assert.False(row.IsThirdParty);
    }

    [Fact]
    public void RouteMarkersAreNamedInWords()
    {
        var digipeated = new PacketRow(
            FakeEngine.Packet("N0CALL") with
            {
                Markers = PacketMarkers.Digipeated | PacketMarkers.Duplicate,
                Hops = 2,
            },
            Localizer.Instance,
            TimeZoneInfo.Utc
        );
        var direct = new PacketRow(
            FakeEngine.Packet("N0CALL") with
            {
                Markers = PacketMarkers.Direct,
            },
            Localizer.Instance,
            TimeZoneInfo.Utc
        );

        Assert.True(digipeated.IsDigipeated && digipeated.IsDuplicate && !digipeated.IsDirect);
        Assert.Equal("2", digipeated.HopsText);
        Assert.Equal("Digipeated: repeated by 2 digipeaters", digipeated.DigipeatedText);
        Assert.True(direct.IsDirect && !direct.IsDigipeated && !direct.IsDuplicate);
    }

    [Fact]
    public void CopyAndSaveWriteRawLinesOldestFirst()
    {
        var list = Create();
        list.Receive(FakeEngine.Packet("N1") with { ReceivedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) });
        list.Flush();
        list.Receive(FakeEngine.Packet("N2") with { ReceivedAt = DateTimeOffset.UnixEpoch.AddSeconds(2) });
        list.Flush();

        Assert.Equal($"N1>APRS:>hi{Environment.NewLine}N2>APRS:>hi", PacketListViewModel.CopyText(list.Rows));
        Assert.Equal(
            $"1970-01-01T00:00:01.000Z N1>APRS:>hi{Environment.NewLine}1970-01-01T00:00:02.000Z N2>APRS:>hi{Environment.NewLine}",
            list.SaveText(new AprsMap.UI.Packets.Export.TextPacketExporter())
        );
        Assert.Matches(
            @"^packets-\d{8}-\d{6}\.txt$",
            list.SaveName(new AprsMap.UI.Packets.Export.TextPacketExporter())
        );
        Assert.Matches(
            @"^packets-\d{8}-\d{6}\.yaml$",
            list.SaveName(new AprsMap.UI.Packets.Export.YamlPacketExporter())
        );
    }

    [Fact]
    public void ATrackAnomalyGetsItsMarkerNamedAndExplained()
    {
        var row = new PacketRow(
            FakeEngine.Packet("N0CAR-9") with
            {
                Anomalies = PacketAnomalies.ImpossibleSpeed | PacketAnomalies.RapidRepeat,
            },
            new AprsMap.UI.Localization.Localizer(System.Globalization.CultureInfo.GetCultureInfo("en-US")),
            TimeZoneInfo.Utc
        );

        Assert.True(row.HasAnomalies);
        Assert.False(row.HasIssues);
        Assert.Equal("Doesn't fit its track: Impossible speed, Rapid repeats", row.AnomalyText);
        Assert.Equal(["Track", "Track"], row.Issues.Select(i => i.Severity));
        Assert.StartsWith("It's further from the previous position", row.Issues[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALargeBatchReplacesTheListInOneGo()
    {
        var list = Create();
        list.Receive(FakeEngine.Packet("N0"));
        var changes = 0;
        list.Rows.CollectionChanged += (_, _) => changes++;

        for (var i = 1; i <= PacketRows.ResetThreshold; i++)
        {
            list.Receive(FakeEngine.Packet($"N{i}"));
        }
        list.Flush();

        Assert.Equal(1, changes);
        Assert.Equal($"N{PacketRows.ResetThreshold}", list.Rows[0].Source);
        Assert.Equal("N0", list.Rows[^1].Source);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
