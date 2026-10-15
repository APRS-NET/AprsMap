// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core.Tests;

public class PacketFilterTests
{
    private static readonly PacketView Packet = new(
        DateTimeOffset.UnixEpoch,
        "N0CALL-9",
        "Position",
        "Car",
        "N0CALL-9>APDR16,WIDE1-1:!4903.50N/07201.75W>",
        Kind: PacketKind.Position,
        Destination: "APDR16",
        Path: "WIDE1-1"
    );

    [Fact]
    public void AnEmptyFilterMatchesEverything()
    {
        var filter = new PacketFilter(Text: " ", Kinds: []);

        Assert.True(filter.IsEmpty);
        Assert.True(filter.Matches(Packet));
    }

    [Theory]
    [InlineData("n0call")]
    [InlineData("apdr")]
    [InlineData("wide1")]
    [InlineData("4903.50")]
    public void TextMatchesSourceDestinationPathOrRawIgnoringCase(string text) =>
        Assert.True(new PacketFilter(Text: text).Matches(Packet));

    [Fact]
    public void TextThatIsNowhereDoesNotMatch() => Assert.False(new PacketFilter(Text: "K1XYZ").Matches(Packet));

    [Fact]
    public void StationMatchesTheWholeSourceIgnoringCase()
    {
        Assert.True(new PacketFilter(Station: " n0call-9 ").Matches(Packet));
        Assert.False(new PacketFilter(Station: "N0CALL").Matches(Packet));
    }

    [Fact]
    public void KindsAndIssuesNarrowTheList()
    {
        Assert.True(new PacketFilter(Kinds: [PacketKind.MicE, PacketKind.Position]).Matches(Packet));
        Assert.False(new PacketFilter(Kinds: [PacketKind.Message]).Matches(Packet));
        Assert.False(new PacketFilter(IssuesOnly: true).Matches(Packet));
        var withIssue = Packet with { Issues = [new EngineError("aprs.parse.invalid-latitude", "Bad latitude.")] };
        Assert.True(new PacketFilter(IssuesOnly: true).Matches(withIssue));
    }

    [Fact]
    public void EveryPartMustMatch() =>
        Assert.False(new PacketFilter(Text: "N0CALL", Kinds: [PacketKind.Message]).Matches(Packet));
}
