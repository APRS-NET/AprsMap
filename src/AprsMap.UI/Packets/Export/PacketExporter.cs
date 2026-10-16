// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;

namespace AprsMap.UI.Packets.Export;

/// <summary>The file formats Save offers (F1.8 behavior 10).</summary>
public enum PacketExportFormat
{
    /// <summary>A log: the time received in UTC, then the raw text, a line a packet.</summary>
    Text,

    /// <summary>Comma-separated values with a header row, for a spreadsheet.</summary>
    Csv,

    /// <summary>A JSON array of packets, compact.</summary>
    Json,

    /// <summary>The same JSON array, indented for reading.</summary>
    JsonIndented,

    /// <summary>A YAML sequence of packets.</summary>
    Yaml,
}

/// <summary>
/// Writes the packets Save was given, oldest first, in one format (F1.8 behavior 10). Every structured format carries
/// the same fields, in the same order (<see cref="PacketFields"/>).
/// </summary>
public abstract class PacketExporter
{
    /// <summary>The format written.</summary>
    public abstract PacketExportFormat Format { get; }

    /// <summary>The file name's extension, without the dot.</summary>
    public abstract string Extension { get; }

    /// <summary>The media type, for the save dialog's file type and the browser's download.</summary>
    public abstract string MediaType { get; }

    /// <summary>The string key of the format's name in the Save menu.</summary>
    public string NameKey => "Packets.Format." + Format;

    /// <summary>The packets as the file's text.</summary>
    /// <param name="packets">The packets, oldest first.</param>
    /// <param name="sourceName">A source's name from its id (F1.25), for where each packet was heard.</param>
    public abstract string Export(IReadOnlyList<PacketView> packets, Func<string, string> sourceName);

    /// <summary>Every exporter, in the order the Save menu lists them.</summary>
    public static IReadOnlyList<PacketExporter> All { get; } =
    [
        new TextPacketExporter(),
        new CsvPacketExporter(),
        new JsonPacketExporter(indented: false),
        new JsonPacketExporter(indented: true),
        new YamlPacketExporter(),
    ];

    /// <summary>The exporter for <paramref name="format"/>.</summary>
    public static PacketExporter For(PacketExportFormat format) => All.First(e => e.Format == format);

    /// <summary>The time a packet was received, in UTC to the millisecond, as every format writes it.</summary>
    internal static string TimeOf(PacketView packet) =>
        packet.ReceivedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

/// <summary>One packet's fields for the structured formats, named as they're written.</summary>
internal sealed record PacketFields(
    string Time,
    string Source,
    string? Destination,
    string? Path,
    string Type,
    string Kind,
    string Summary,
    double? Latitude,
    double? Longitude,
    string? Symbol,
    string? SymbolName,
    int Hops,
    IReadOnlyList<string> Markers,
    string? HeardOn,
    IReadOnlyList<string> SeenOn,
    IReadOnlyList<EngineError> Issues,
    string Raw
)
{
    /// <summary>The field names, in the order every structured format writes them.</summary>
    public static IReadOnlyList<string> Names { get; } =
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
    ];

    public static PacketFields Of(PacketView packet, Func<string, string> sourceName) =>
        new(
            PacketExporter.TimeOf(packet),
            packet.Source,
            packet.Destination,
            packet.Path,
            packet.Type,
            packet.Kind.ToString(),
            packet.Summary,
            packet.Latitude,
            packet.Longitude,
            packet.Symbol,
            packet.SymbolName,
            packet.Hops,
            MarkersOf(packet.Markers),
            packet.SourceId is { } id ? sourceName(id) : null,
            [.. (packet.SeenOn ?? []).Select(sourceName)],
            packet.Issues ?? [],
            packet.Raw
        );

    private static string[] MarkersOf(PacketMarkers markers) =>
        [
            .. Enum.GetValues<PacketMarkers>()
                .Where(m => m != PacketMarkers.None && markers.HasFlag(m))
                .Select(m => char.ToLowerInvariant(m.ToString()[0]) + m.ToString()[1..]),
        ];
}
