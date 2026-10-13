// SPDX-License-Identifier: MIT
using System.Globalization;
using AprsMap.Contracts;
using AprsNet.Packets;
using AprsNet.Symbols;

namespace AprsMap.Core;

/// <summary>Turns parsed APRS.NET packets into the plain <see cref="PacketView"/> records the UIs consume.</summary>
public static class PacketSummarizer
{
    /// <summary>Builds the view of a packet received at <paramref name="receivedAt"/>.</summary>
    public static PacketView Summarize(AprsPacket packet, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var source = packet.Source?.Value ?? string.Empty;
        var type = packet.Type.ToString();

        if (packet.Payload is PositionedPayload positioned)
        {
            var symbol = AprsSymbolCatalog.GetName(positioned.Symbol);
            var lat = positioned.Position.Latitude;
            var lon = positioned.Position.Longitude;
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{symbol} at {lat:0.0000}, {lon:0.0000}{Suffix(positioned.Comment)}"
            );
            return new PacketView(receivedAt, source, type, summary, packet.ToString(), lat, lon, symbol);
        }

        var text = packet.Outcome == ParseOutcome.Parsed ? type : $"{type} ({packet.Outcome})";
        return new PacketView(receivedAt, source, type, text, packet.ToString());
    }

    private static string Suffix(string comment) => string.IsNullOrWhiteSpace(comment) ? string.Empty : $": {comment}";
}
