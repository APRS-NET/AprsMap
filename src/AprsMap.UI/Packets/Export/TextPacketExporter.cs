// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using AprsMap.Contracts;

namespace AprsMap.UI.Packets.Export;

/// <summary>A log of raw lines, each after the time it was received in UTC: the format Save always wrote.</summary>
public sealed class TextPacketExporter : PacketExporter
{
    /// <inheritdoc />
    public override PacketExportFormat Format => PacketExportFormat.Text;

    /// <inheritdoc />
    public override string Extension => "txt";

    /// <inheritdoc />
    public override string MediaType => "text/plain";

    /// <inheritdoc />
    public override string Export(IReadOnlyList<PacketView> packets, Func<string, string> sourceName)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var text = new StringBuilder();
        foreach (var packet in packets)
        {
            text.Append(TimeOf(packet)).Append(' ').Append(packet.Raw).Append(Environment.NewLine);
        }
        return text.ToString();
    }
}
