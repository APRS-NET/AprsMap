// SPDX-License-Identifier: MIT
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.Core;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

public class PacketSummarizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public void PositionReportCarriesPositionSymbolAndComment()
    {
        var packet = AprsPacket.Parse("N0CALL-9>APRS,WIDE1-1:!4903.50N/07201.75W>Heading home");

        var view = PacketSummarizer.Summarize(packet, Now);

        Assert.Equal("N0CALL-9", view.Source);
        Assert.Equal("Position", view.Type);
        Assert.Equal(Now, view.ReceivedAt);
        Assert.NotNull(view.Latitude);
        Assert.Equal(49.0583, view.Latitude.Value, 3);
        Assert.Equal(-72.0292, view.Longitude!.Value, 3);
        Assert.False(string.IsNullOrEmpty(view.SymbolName));
        Assert.EndsWith(": Heading home", view.Summary, StringComparison.Ordinal);
        Assert.Equal("N0CALL-9>APRS,WIDE1-1:!4903.50N/07201.75W>Heading home", view.Raw);
    }

    [Fact]
    public void PositionWithoutCommentHasNoTrailingColon()
    {
        var view = PacketSummarizer.Summarize(AprsPacket.Parse("N0CALL>APRS:!4903.50N/07201.75W-"), Now);

        Assert.DoesNotContain(':', view.Summary.Split(" at ")[1]);
    }

    [Fact]
    public void StatusIsSummarisedByTypeAndText()
    {
        var view = PacketSummarizer.Summarize(AprsPacket.Parse("N0CALL>APRS:>Net tonight 8pm"), Now);

        Assert.Equal("Status", view.Type);
        Assert.Equal(PacketKind.Status, view.Kind);
        Assert.Equal("Status: Net tonight 8pm", view.Summary);
        Assert.Null(view.Latitude);
        Assert.Null(view.SymbolName);
    }

    [Fact]
    public void NonPositionedPacketIsSummarisedByType()
    {
        var view = PacketSummarizer.Summarize(AprsPacket.Parse("N0CALL>APRS::N1CALL   :Hello{01"), Now);

        Assert.Equal("Message", view.Summary);
        Assert.Equal(PacketKind.Message, view.Kind);
    }

    [Fact]
    public void HeaderPartsAreShown()
    {
        var view = PacketSummarizer.Summarize(
            AprsPacket.Parse("N0CALL-9>APDR16,WIDE1-1,WIDE2-1,qAR,N1CALL:!4903.50N/07201.75W>"),
            Now
        );

        Assert.Equal("APDR16", view.Destination);
        Assert.Equal("WIDE1-1,WIDE2-1,qAR,N1CALL", view.Path);
        Assert.Equal(PacketKind.Position, view.Kind);
        Assert.Equal("/>", view.Symbol);
        Assert.False(view.HasIssues);
        Assert.Equal(PacketMarkers.None, view.Markers);
    }

    [Fact]
    public void StatusWithASymbolCarriesIt()
    {
        var view = PacketSummarizer.Summarize(AprsPacket.Parse("N0CALL>APRS:>FN42AB/- Home station"), Now);

        Assert.Equal("/-", view.Symbol);
        Assert.False(string.IsNullOrEmpty(view.SymbolName));
    }

    [Fact]
    public void ParseIssuesBecomeEngineErrorsWithOffsetAndSeverity()
    {
        const string Line = "N0CALL>APRS:!9999.99N/99999.99W-";
        var view = PacketSummarizer.Summarize(AprsPacket.Parse(Line), Now);

        Assert.True(view.HasIssues);
        var issue = view.Issues![0];
        Assert.Equal("aprs.parse.invalid-latitude", issue.Code);
        Assert.Equal(EngineErrorArgs.SeverityError, issue.Arg(EngineErrorArgs.Severity));
        Assert.Equal(
            Line.IndexOf("9999.99N", StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture),
            issue.Arg(EngineErrorArgs.Offset)
        );
        Assert.False(string.IsNullOrEmpty(issue.Message));
    }

    [Fact]
    public void ThirdPartyPacketIsShownAsThePacketInsideWithItsIssuesInTheOuterText()
    {
        const string Line = "N1GATE>APRS:}N0CALL>APRS,TCPIP,N1GATE*:!9999.99N/99999.99W-";

        var view = PacketSummarizer.Summarize(AprsPacket.Parse(Line), Now);

        Assert.Equal("N0CALL", view.Source);
        Assert.Equal(PacketMarkers.ThirdParty, view.Markers);
        Assert.Equal(Line, view.Raw);
        var issue = Assert.Single(view.Issues!, i => i.Code == "aprs.parse.invalid-latitude");
        Assert.Equal(
            Line.IndexOf("9999.99N", StringComparison.Ordinal),
            int.Parse(issue.Arg(EngineErrorArgs.Offset)!, CultureInfo.InvariantCulture)
        );
    }

    [Theory]
    [InlineData("N0CALL>APRS:;LEADER   *092345z4903.50N/07201.75W>", PacketKind.ObjectOrItem)]
    [InlineData("N0CALL>APRS:)AID #2!4903.50N/07201.75WA", PacketKind.ObjectOrItem)]
    [InlineData("N0CALL>APRS:_10090556c220s004g005t077", PacketKind.Weather)]
    [InlineData("N0CALL>APRS:T#005,199,000,255,073,123,01101001", PacketKind.Telemetry)]
    [InlineData("N0CALL>APRS:<IGATE,MSG_CNT=30", PacketKind.Other)]
    public void PacketsAreSortedIntoKinds(string line, PacketKind kind) =>
        Assert.Equal(kind, PacketSummarizer.Summarize(AprsPacket.Parse(line), Now).Kind);

    [Fact]
    public void MalformedPacketShowsItsOutcome()
    {
        var view = PacketSummarizer.Summarize(AprsPacket.Parse("N0CALL>APRS:!9999.99N/99999.99W-"), Now);

        Assert.Contains("Malformed", view.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void LineWithoutAHeaderHasNoSource()
    {
        var view = PacketSummarizer.Summarize(AprsPacket.Parse("not a packet"), Now);

        Assert.Equal(string.Empty, view.Source);
        Assert.Equal("not a packet", view.Raw);
    }

    [Fact]
    public void NullPacketIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => PacketSummarizer.Summarize(null!, Now));
}
