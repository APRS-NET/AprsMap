// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text;
using AprsMap.Contracts;

namespace AprsMap.UI.Packets.Export;

/// <summary>
/// A YAML 1.2 sequence of packet mappings. Every string is double-quoted with its special characters escaped, so text
/// from the air (colons, hashes, quotes, leading dashes) can never change the document's structure; numbers and null
/// are plain. Written by hand: the format needs nothing a library would add.
/// </summary>
public sealed class YamlPacketExporter : PacketExporter
{
    /// <inheritdoc />
    public override PacketExportFormat Format => PacketExportFormat.Yaml;

    /// <inheritdoc />
    public override string Extension => "yaml";

    /// <inheritdoc />
    public override string MediaType => "application/yaml";

    /// <inheritdoc />
    public override string Export(IReadOnlyList<PacketView> packets, Func<string, string> sourceName)
    {
        ArgumentNullException.ThrowIfNull(packets);
        if (packets.Count == 0)
        {
            return "[]\n";
        }
        var yaml = new StringBuilder();
        foreach (var packet in packets)
        {
            var f = PacketFields.Of(packet, sourceName);
            yaml.Append("- time: ").Append(Quote(f.Time)).Append('\n');
            Field(yaml, "source", Quote(f.Source));
            Field(yaml, "destination", Quote(f.Destination));
            Field(yaml, "path", Quote(f.Path));
            Field(yaml, "type", Quote(f.Type));
            Field(yaml, "kind", Quote(f.Kind));
            Field(yaml, "summary", Quote(f.Summary));
            Field(yaml, "latitude", Number(f.Latitude));
            Field(yaml, "longitude", Number(f.Longitude));
            Field(yaml, "symbol", Quote(f.Symbol));
            Field(yaml, "symbolName", Quote(f.SymbolName));
            Field(yaml, "hops", f.Hops.ToString(CultureInfo.InvariantCulture));
            Strings(yaml, "markers", f.Markers);
            Field(yaml, "heardOn", Quote(f.HeardOn));
            Strings(yaml, "seenOn", f.SeenOn);
            if (f.Issues.Count == 0)
            {
                Field(yaml, "issues", "[]");
            }
            else
            {
                yaml.Append("  issues:\n");
                foreach (var issue in f.Issues)
                {
                    yaml.Append("    - code: ").Append(Quote(issue.Code)).Append('\n');
                    yaml.Append("      message: ").Append(Quote(issue.Message)).Append('\n');
                }
            }
            Field(yaml, "raw", Quote(f.Raw));
        }
        return yaml.ToString();
    }

    private static void Field(StringBuilder yaml, string name, string value) =>
        yaml.Append("  ").Append(name).Append(": ").Append(value).Append('\n');

    private static void Strings(StringBuilder yaml, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            Field(yaml, name, "[]");
            return;
        }
        yaml.Append("  ").Append(name).Append(":\n");
        foreach (var value in values)
        {
            yaml.Append("    - ").Append(Quote(value)).Append('\n');
        }
    }

    private static string Number(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    /// <summary>A double-quoted YAML scalar: backslash, quote, line breaks and control characters escaped; null for none.</summary>
    internal static string Quote(string? value)
    {
        if (value is null)
        {
            return "null";
        }
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            _ = c switch
            {
                '\\' => quoted.Append(@"\\"),
                '"' => quoted.Append("\\\""),
                '\n' => quoted.Append(@"\n"),
                '\r' => quoted.Append(@"\r"),
                '\t' => quoted.Append(@"\t"),
                '\u0085' => quoted.Append(@"\N"),
                '\u2028' => quoted.Append(@"\L"),
                '\u2029' => quoted.Append(@"\P"),
                _ when char.IsControl(c) => quoted.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:X2}"),
                _ => quoted.Append(c),
            };
        }
        return quoted.Append('"').ToString();
    }
}
