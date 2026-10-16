// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using AprsMap.Contracts;

namespace AprsMap.UI.Packets.Export;

/// <summary>
/// A JSON array of packet objects, compact or indented. Written field by field with <see cref="Utf8JsonWriter"/>, so
/// it needs no reflection and survives trimming in the web app. Absent values are written as null, so every object has
/// every field.
/// </summary>
public sealed class JsonPacketExporter(bool indented) : PacketExporter
{
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    /// <inheritdoc />
    public override PacketExportFormat Format => indented ? PacketExportFormat.JsonIndented : PacketExportFormat.Json;

    /// <inheritdoc />
    public override string Extension => "json";

    /// <inheritdoc />
    public override string MediaType => "application/json";

    /// <inheritdoc />
    public override string Export(IReadOnlyList<PacketView> packets, Func<string, string> sourceName)
    {
        ArgumentNullException.ThrowIfNull(packets);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, Encoder = Encoder }))
        {
            json.WriteStartArray();
            foreach (var packet in packets)
            {
                Write(json, PacketFields.Of(packet, sourceName));
            }
            json.WriteEndArray();
        }
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return indented ? text + "\n" : text;
    }

    private static void Write(Utf8JsonWriter json, PacketFields f)
    {
        json.WriteStartObject();
        json.WriteString("time", f.Time);
        json.WriteString("source", f.Source);
        json.WriteString("destination", f.Destination);
        json.WriteString("path", f.Path);
        json.WriteString("type", f.Type);
        json.WriteString("kind", f.Kind);
        json.WriteString("summary", f.Summary);
        Number(json, "latitude", f.Latitude);
        Number(json, "longitude", f.Longitude);
        json.WriteString("symbol", f.Symbol);
        json.WriteString("symbolName", f.SymbolName);
        json.WriteNumber("hops", f.Hops);
        Strings(json, "markers", f.Markers);
        json.WriteString("heardOn", f.HeardOn);
        Strings(json, "seenOn", f.SeenOn);
        json.WriteStartArray("issues");
        foreach (var issue in f.Issues)
        {
            json.WriteStartObject();
            json.WriteString("code", issue.Code);
            json.WriteString("message", issue.Message);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteString("raw", f.Raw);
        json.WriteEndObject();
    }

    private static void Number(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } number)
        {
            json.WriteNumber(name, number);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void Strings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }
        json.WriteEndArray();
    }
}
