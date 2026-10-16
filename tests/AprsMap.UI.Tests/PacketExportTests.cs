// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using AprsMap.Contracts;
using AprsMap.UI.Packets.Export;

namespace AprsMap.UI.Tests;

/// <summary>Save's file formats (docs/features/F1.8-packet-list.md, behavior 10).</summary>
public sealed class PacketExportTests
{
    // A position heard through a digipeater, with a comment written to break naive writers: a comma, a quote, a
    // colon and hash for YAML, a line break, and an issue.
    private static readonly PacketView Position = new(
        DateTimeOffset.UnixEpoch.AddSeconds(1.5),
        "N0CAR-9",
        "Position",
        "Car at 40.0000, -105.0000: say \"hi\", #1: ok\nbye",
        "N0CAR-9>APRS,N0DIGI-1*,qAR,N0GATE:!4000.00N/10500.00W>say \"hi\", #1",
        40,
        -105,
        "Car",
        PacketKind.Position,
        "APRS",
        "N0DIGI-1*,qAR,N0GATE",
        [new EngineError("aprs.bad-comment", "The comment is too long")],
        PacketMarkers.Digipeated | PacketMarkers.ThirdParty,
        "/>",
        1,
        "src1",
        ["src2"]
    );

    // A bare status with no position, no source and a comment a spreadsheet would run as a formula.
    private static readonly PacketView Status = new(
        DateTimeOffset.UnixEpoch.AddSeconds(2),
        "=cmd|' /C calc'!A0",
        "Status",
        "-2+3",
        "N0X>APRS:>=1+1"
    );

    private static readonly PacketView[] Packets = [Position, Status];

    private static string Names(string id) => id == "src1" ? "Direwolf" : "noam.aprs2.net";

    [Fact]
    public void EveryFormatIsListedOnceWithItsExtension()
    {
        Assert.Equal(
            [
                PacketExportFormat.Text,
                PacketExportFormat.Csv,
                PacketExportFormat.Json,
                PacketExportFormat.JsonIndented,
                PacketExportFormat.Yaml,
            ],
            PacketExporter.All.Select(e => e.Format)
        );
        Assert.Equal(["txt", "csv", "json", "json", "yaml"], PacketExporter.All.Select(e => e.Extension));
    }

    [Fact]
    public void TextIsTheTimeInUtcThenTheRawLine() =>
        Assert.Equal(
            $"1970-01-01T00:00:01.500Z {Position.Raw}{Environment.NewLine}"
                + $"1970-01-01T00:00:02.000Z {Status.Raw}{Environment.NewLine}",
            new TextPacketExporter().Export(Packets, Names)
        );

    [Fact]
    public void CsvHasAHeaderQuotesWhatNeedsItAndDefusesFormulas()
    {
        var csv = new CsvPacketExporter().Export(Packets, Names);
        var lines = csv.Split("\r\n");

        Assert.Equal(
            "time,source,destination,path,type,kind,summary,latitude,longitude,symbol,symbolName,hops,markers,heardOn,"
                + "seenOn,issues,raw",
            lines[0]
        );
        Assert.StartsWith(
            "1970-01-01T00:00:01.500Z,N0CAR-9,APRS,\"N0DIGI-1*,qAR,N0GATE\",Position,Position,"
                + "\"Car at 40.0000, -105.0000: say \"\"hi\"\", #1: ok\nbye\",40,-105,/>,Car,1,thirdParty; digipeated,"
                + "Direwolf,noam.aprs2.net,aprs.bad-comment: The comment is too long,",
            csv[(lines[0].Length + 2)..],
            StringComparison.Ordinal
        );
        Assert.Equal(
            "1970-01-01T00:00:02.000Z,'=cmd|' /C calc'!A0,,,Status,Other,'-2+3,,,,,0,,,,,N0X>APRS:>=1+1",
            lines[2]
        );
        Assert.EndsWith("\r\n", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonIsAnArrayWithEveryFieldOnEveryPacket(bool indented)
    {
        var text = new JsonPacketExporter(indented).Export(Packets, Names);
        using var json = JsonDocument.Parse(text);

        var first = json.RootElement[0];
        Assert.Equal(
            [
                "time",
                "source",
                "destination",
                "path",
                "type",
                "kind",
                "summary",
                "latitude",
                "longitude",
                "symbol",
                "symbolName",
                "hops",
                "markers",
                "heardOn",
                "seenOn",
                "issues",
                "raw",
            ],
            first.EnumerateObject().Select(p => p.Name)
        );
        Assert.Equal(Position.Summary, first.GetProperty("summary").GetString());
        Assert.Equal(-105, first.GetProperty("longitude").GetDouble());
        Assert.Equal(
            ["thirdParty", "digipeated"],
            first.GetProperty("markers").EnumerateArray().Select(m => m.GetString())
        );
        Assert.Equal("Direwolf", first.GetProperty("heardOn").GetString());
        Assert.Equal("aprs.bad-comment", first.GetProperty("issues")[0].GetProperty("code").GetString());
        var second = json.RootElement[1];
        Assert.Equal(JsonValueKind.Null, second.GetProperty("latitude").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("heardOn").ValueKind);
        Assert.Equal(0, second.GetProperty("issues").GetArrayLength());
        Assert.Equal(indented, text.Contains('\n', StringComparison.Ordinal));
    }

    [Fact]
    public void YamlQuotesEveryStringSoTextFromTheAirCantBreakIt()
    {
        var yaml = new YamlPacketExporter().Export(Packets, Names);

        Assert.StartsWith(
            """
            - time: "1970-01-01T00:00:01.500Z"
              source: "N0CAR-9"
              destination: "APRS"
              path: "N0DIGI-1*,qAR,N0GATE"
              type: "Position"
              kind: "Position"
              summary: "Car at 40.0000, -105.0000: say \"hi\", #1: ok\nbye"
              latitude: 40
              longitude: -105
              symbol: "/>"
              symbolName: "Car"
              hops: 1
              markers:
                - "thirdParty"
                - "digipeated"
              heardOn: "Direwolf"
              seenOn:
                - "noam.aprs2.net"
              issues:
                - code: "aprs.bad-comment"
                  message: "The comment is too long"
            """.ReplaceLineEndings("\n"),
            yaml,
            StringComparison.Ordinal
        );
        Assert.Contains("  destination: null\n", yaml, StringComparison.Ordinal);
        Assert.Contains("  markers: []\n  heardOn: null\n  seenOn: []\n  issues: []\n", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tab\there", "\"tab\\there\"")]
    [InlineData("bell\u0007", "\"bell\\x07\"")]
    [InlineData("line\u2028sep", "\"line\\Lsep\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    public void YamlEscapesControlAndBreakCharacters(string value, string quoted) =>
        Assert.Equal(quoted, YamlPacketExporter.Quote(value));

    [Fact]
    public void NoPacketsGiveAnEmptyButValidFile()
    {
        Assert.Equal(string.Empty, new TextPacketExporter().Export([], Names));
        Assert.Equal("[]", new JsonPacketExporter(indented: false).Export([], Names));
        Assert.Equal("[]\n", new YamlPacketExporter().Export([], Names));
        Assert.StartsWith("time,", new CsvPacketExporter().Export([], Names), StringComparison.Ordinal);
    }
}
