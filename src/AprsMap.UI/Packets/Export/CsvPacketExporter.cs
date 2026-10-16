// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text;
using AprsMap.Contracts;

namespace AprsMap.UI.Packets.Export;

/// <summary>
/// Comma-separated values (RFC 4180) with a header row of the field names, lines ending CRLF. Lists are joined with
/// "; " and each issue is written "code: message".
/// </summary>
public sealed class CsvPacketExporter : PacketExporter
{
    /// <inheritdoc />
    public override PacketExportFormat Format => PacketExportFormat.Csv;

    /// <inheritdoc />
    public override string Extension => "csv";

    /// <inheritdoc />
    public override string MediaType => "text/csv";

    /// <inheritdoc />
    public override string Export(IReadOnlyList<PacketView> packets, Func<string, string> sourceName)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var csv = new StringBuilder();
        Row(csv, PacketFields.Names);
        foreach (var packet in packets)
        {
            var f = PacketFields.Of(packet, sourceName);
            Row(
                csv,
                [
                    f.Time,
                    Text(f.Source),
                    Text(f.Destination),
                    Text(f.Path),
                    Text(f.Type),
                    f.Kind,
                    Text(f.Summary),
                    Number(f.Latitude),
                    Number(f.Longitude),
                    Text(f.Symbol),
                    Text(f.SymbolName),
                    f.Hops.ToString(CultureInfo.InvariantCulture),
                    string.Join("; ", f.Markers),
                    Text(f.HeardOn),
                    Text(string.Join("; ", f.SeenOn)),
                    Text(string.Join("; ", f.Issues.Select(i => $"{i.Code}: {i.Message}"))),
                    Text(f.Raw),
                ]
            );
        }
        return csv.ToString();
    }

    private static string Number(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Text from the air, made safe to open in a spreadsheet: a value starting with =, +, -, @, a tab or a carriage
    /// return gets a leading apostrophe so it isn't run as a formula (any station can send such a comment).
    /// </summary>
    internal static string Text(string? value) =>
        value is { Length: > 0 } && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value ?? string.Empty;

    private static void Row(StringBuilder csv, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                csv.Append(',');
            }
            var value = values[i];
            if (
                value.AsSpan().IndexOfAny(",\"\r\n") >= 0
                || (value.Length > 0 && (value[0] == ' ' || value[^1] == ' '))
            )
            {
                csv.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                csv.Append(value);
            }
        }
        csv.Append("\r\n");
    }
}
