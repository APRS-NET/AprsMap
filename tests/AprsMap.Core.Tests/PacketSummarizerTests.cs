// SPDX-License-Identifier: MIT
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
    public void NonPositionedPacketIsSummarisedByType()
    {
        var view = PacketSummarizer.Summarize(AprsPacket.Parse("N0CALL>APRS:>Net tonight 8pm"), Now);

        Assert.Equal("Status", view.Type);
        Assert.Equal("Status", view.Summary);
        Assert.Null(view.Latitude);
        Assert.Null(view.SymbolName);
    }

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
