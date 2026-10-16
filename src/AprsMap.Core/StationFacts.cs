// SPDX-License-Identifier: MIT
using System.Text;
using AprsMap.Contracts;
using AprsNet.DeviceId;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// What a station's last packet says about how it was sent and how it arrived (docs/features/F1.6-station-details.md):
/// worked out from the packet's raw text when the details are asked for, so the store keeps only the bytes.
/// </summary>
internal static class StationFacts
{
    /// <summary>The device, arrival and raw text of <paramref name="raw"/>, as received.</summary>
    public static (StationDevice? Device, StationArrival? Arrival, string? Text) Of(byte[]? raw)
    {
        if (raw is null)
        {
            return (null, null, null);
        }
        var packet = AprsPacket.Parse(raw);
        var text = Encoding.Latin1.GetString(raw);
        if (packet.Payload is ThirdPartyPayload { Packet: { HasHeader: true } inner })
        {
            packet = inner; // the station inside sent it, by the route inside
        }
        return (Device(packet), Arrival(packet), text);
    }

    /// <summary>The sender's equipment from its destination (TOCALL) or Mic-E code; null when the list doesn't know it.</summary>
    public static StationDevice? Device(AprsPacket packet)
    {
        // The list names generic destinations (plain APRS) "Unknown": that says nothing.
        if (AprsDevices.Identify(packet) is not { } device)
        {
            return null;
        }
        var vendor = Known(device.Vendor);
        var model = Known(device.Model);
        return vendor is null && model is null ? null : new StationDevice(vendor, model, device.Class?.ToString());
    }

    private static string? Known(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : name;

    /// <summary>How the packet travelled, from its path; null when it has no readable header.</summary>
    public static StationArrival? Arrival(AprsPacket packet)
    {
        if (packet.Path is null)
        {
            return null;
        }
        var route = PathAnalysis.FromPacket(packet);
        var kind =
            route.IsFromInternet ? ArrivalKind.Internet
            : route.IsHeardDirectly ? ArrivalKind.Direct
            : route.UsedHops.Count > 0 ? ArrivalKind.Digipeated
            : ArrivalKind.Unknown;
        return new StationArrival(
            kind,
            kind == ArrivalKind.Digipeated ? [.. route.Digipeaters.Select(d => d.Value)] : [],
            route.EntryStation?.Value
        );
    }
}
