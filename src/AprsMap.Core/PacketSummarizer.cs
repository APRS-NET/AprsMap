// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using AprsMap.Contracts;
using AprsNet.Packets;
using AprsNet.Symbols;

namespace AprsMap.Core;

/// <summary>Turns parsed APRS.NET packets into the plain <see cref="PacketView"/> records the UIs consume.</summary>
public static class PacketSummarizer
{
    /// <summary>
    /// Builds the view of a packet received at <paramref name="receivedAt"/>. A third-party packet is shown as the
    /// packet inside it, marked <see cref="PacketMarkers.ThirdParty"/>, with the raw text as received. The direct and
    /// digipeated markers come from the outer path: how the packet reached APRS-IS.
    /// </summary>
    /// <param name="packet">The packet as received.</param>
    /// <param name="receivedAt">When it was received.</param>
    /// <param name="duplicate">Whether it repeats a packet received within the duplicate window.</param>
    public static PacketView Summarize(AprsPacket packet, DateTimeOffset receivedAt, bool duplicate = false)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var raw = Encoding.Latin1.GetString(packet.Raw.Span);
        var issues = Issues(packet, 0);
        var route = PathAnalysis.FromPacket(packet);
        var hops = route.IsFromInternet ? 0 : route.UsedHops.Count;
        var markers = duplicate ? PacketMarkers.Duplicate : PacketMarkers.None;
        if (route.IsHeardDirectly)
        {
            markers |= PacketMarkers.Direct;
        }
        else if (hops > 0)
        {
            markers |= PacketMarkers.Digipeated;
        }
        if (packet.Payload is ThirdPartyPayload { Packet: { HasHeader: true } inner })
        {
            // The packet inside is the end of the line, so its offsets move by what comes before it.
            issues = [.. issues, .. Issues(inner, packet.Raw.Length - inner.Raw.Length)];
            packet = inner;
            markers |= PacketMarkers.ThirdParty;
        }

        var source = packet.Source?.Value ?? string.Empty;
        var type = packet.Type.ToString();
        var view = new PacketView(
            receivedAt,
            source,
            type,
            type,
            raw,
            Kind: Kind(packet.Type),
            Destination: packet.Destination?.Value,
            Path: packet.Path is { } path ? string.Join(',', path.Select(p => p.ToString())) : null,
            Issues: issues,
            Markers: markers,
            Hops: hops
        );

        switch (packet.Payload)
        {
            case PositionedPayload positioned when !positioned.IsPositionUnknown:
            {
                var symbol = AprsSymbolCatalog.GetName(positioned.Symbol);
                var lat = positioned.Position.Latitude;
                var lon = positioned.Position.Longitude;
                var summary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{symbol} at {lat:0.0000}, {lon:0.0000}{Suffix(positioned.Comment)}"
                );
                return view with
                {
                    Summary = summary,
                    Latitude = lat,
                    Longitude = lon,
                    SymbolName = symbol,
                    Symbol = positioned.Symbol.ToString(),
                };
            }
            case PositionedPayload positioned:
                return view with
                {
                    Summary = $"{type}{Suffix(positioned.Comment)}",
                    SymbolName = AprsSymbolCatalog.GetName(positioned.Symbol),
                    Symbol = positioned.Symbol.ToString(),
                };
            case StatusReport { Symbol: { } symbol } status:
                return view with
                {
                    Summary = $"{type}{Suffix(status.Text)}",
                    SymbolName = AprsSymbolCatalog.GetName(symbol),
                    Symbol = symbol.ToString(),
                };
            case StatusReport status:
                return view with { Summary = $"{type}{Suffix(status.Text)}" };
        }

        return packet.Outcome == ParseOutcome.Parsed ? view : view with { Summary = $"{type} ({packet.Outcome})" };
    }

    /// <summary>The kind the packet list filters by.</summary>
    public static PacketKind Kind(PacketType type) =>
        type switch
        {
            PacketType.Position => PacketKind.Position,
            PacketType.MicE => PacketKind.MicE,
            PacketType.Object or PacketType.Item => PacketKind.ObjectOrItem,
            PacketType.Message => PacketKind.Message,
            PacketType.Status => PacketKind.Status,
            PacketType.Weather => PacketKind.Weather,
            PacketType.Telemetry => PacketKind.Telemetry,
            _ => PacketKind.Other,
        };

    /// <summary>The packet's parse issues as engine errors, for the UI to translate (F1.8 behavior 3).</summary>
    internal static EngineError[] Issues(AprsPacket packet, int shift = 0) =>
        [
            .. packet.Issues.Select(issue => new EngineError(
                EngineErrorCodes.AprsParsePrefix + AprsErrors.Kebab(issue.Code.ToString()),
                issue.Code.Describe(),
                new Dictionary<string, string>
                {
                    [EngineErrorArgs.Offset] = (issue.Offset + shift).ToString(CultureInfo.InvariantCulture),
                    [EngineErrorArgs.Severity] =
                        issue.Severity == ParseIssueSeverity.Error
                            ? EngineErrorArgs.SeverityError
                            : EngineErrorArgs.SeverityWarning,
                }
            )),
        ];

    private static string Suffix(string? comment) => string.IsNullOrWhiteSpace(comment) ? string.Empty : $": {comment}";
}
