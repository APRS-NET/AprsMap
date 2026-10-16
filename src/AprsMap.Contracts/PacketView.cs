// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// A received packet as the UIs show it. Plain, serializable values only: APRS.NET types stay inside the engine so
/// the contract can cross the SignalR boundary unchanged.
/// </summary>
/// <param name="ReceivedAt">When the engine received it.</param>
/// <param name="Source">The sending station, or empty when the packet has no valid header.</param>
/// <param name="Type">The packet type name, for example <c>Position</c> or <c>MicE</c>.</param>
/// <param name="Summary">A one-line, human-readable description.</param>
/// <param name="Raw">The packet as received (TNC2 text, Latin-1).</param>
/// <param name="Latitude">Latitude in degrees, when the packet carries a position.</param>
/// <param name="Longitude">Longitude in degrees, when the packet carries a position.</param>
/// <param name="SymbolName">The symbol's name, when the packet carries a symbol.</param>
/// <param name="Kind">The packet's kind, for the type filter (F1.8 behavior 7).</param>
/// <param name="Destination">The header's destination (usually the sending device), when there is a header.</param>
/// <param name="Path">The header's path as sent, entries separated by commas; empty when there is none.</param>
/// <param name="Issues">
/// What APRS.NET couldn't decode or found off-spec (F1.8 behavior 3): codes <c>aprs.parse.*</c> with
/// <see cref="EngineErrorArgs.Offset"/> and <see cref="EngineErrorArgs.Severity"/>; empty for a clean packet.
/// </param>
/// <param name="Markers">How the packet reached APRS-IS (F1.8 behavior 2).</param>
/// <param name="Symbol">The symbol as its two APRS characters (table, code), when the packet carries one.</param>
/// <param name="SourceId">The source it arrived on (F1.25 behavior 8), by <see cref="SourceSettings.Id"/>.</param>
/// <param name="SeenOn">Other sources it was heard on within the duplicate window (F1.25 behavior 9).</param>
/// <param name="Hops">How many digipeaters repeated it, for <see cref="PacketMarkers.Digipeated"/>; 0 otherwise.</param>
public sealed record PacketView(
    DateTimeOffset ReceivedAt,
    string Source,
    string Type,
    string Summary,
    string Raw,
    double? Latitude = null,
    double? Longitude = null,
    string? SymbolName = null,
    PacketKind Kind = PacketKind.Other,
    string? Destination = null,
    string? Path = null,
    IReadOnlyList<EngineError>? Issues = null,
    PacketMarkers Markers = PacketMarkers.None,
    string? Symbol = null,
    int Hops = 0,
    string? SourceId = null,
    IReadOnlyList<string>? SeenOn = null
)
{
    /// <summary>Whether APRS.NET reported any issue with the packet.</summary>
    public bool HasIssues => Issues is { Count: > 0 };
}

/// <summary>The kinds of packet the packet list filters by (F1.8 behavior 7).</summary>
public enum PacketKind
{
    /// <summary>Anything not listed below: capabilities, user-defined, unknown.</summary>
    Other,

    /// <summary>A position report, with or without a timestamp.</summary>
    Position,

    /// <summary>A Mic-E report.</summary>
    MicE,

    /// <summary>An object or an item.</summary>
    ObjectOrItem,

    /// <summary>A message, acknowledgement, rejection or bulletin.</summary>
    Message,

    /// <summary>A status report.</summary>
    Status,

    /// <summary>A weather report without a position.</summary>
    Weather,

    /// <summary>Telemetry, or a telemetry definition.</summary>
    Telemetry,
}

/// <summary>How a packet reached APRS-IS (F1.8 behavior 2), from APRS.NET's path analysis and duplicate detection.</summary>
[Flags]
public enum PacketMarkers
{
    /// <summary>Nothing known.</summary>
    None = 0,

    /// <summary>Carried inside another station's packet (third-party); the view shows the packet inside.</summary>
    ThirdParty = 1,

    /// <summary>Heard on RF straight from the sender by the IGate that sent it to APRS-IS, with no digipeater.</summary>
    Direct = 2,

    /// <summary>Repeated by digipeaters on RF before an IGate heard it; <see cref="PacketView.Hops"/> says how many.</summary>
    Digipeated = 4,

    /// <summary>
    /// A copy of a packet already received within 30 s (same source, destination and information field, any path). It is
    /// listed but doesn't update the station (F1.3 behavior 11).
    /// </summary>
    Duplicate = 8,
}

/// <summary>
/// What the packet list shows (F1.8 behavior 7). Every part that is set must match; a filter with nothing set matches
/// every packet.
/// </summary>
/// <param name="Text">Matches the source, destination, path or raw text, ignoring case.</param>
/// <param name="Station">Only this station's packets, ignoring case.</param>
/// <param name="Kinds">Only these kinds; null or empty for all.</param>
/// <param name="IssuesOnly">Only packets with parse issues.</param>
public sealed record PacketFilter(
    string? Text = null,
    string? Station = null,
    IReadOnlyList<PacketKind>? Kinds = null,
    bool IssuesOnly = false
)
{
    /// <summary>Whether the filter lets every packet through.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Text)
        && string.IsNullOrWhiteSpace(Station)
        && Kinds is not { Count: > 0 }
        && !IssuesOnly;

    /// <summary>Whether <paramref name="packet"/> passes the filter.</summary>
    public bool Matches(PacketView packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (IssuesOnly && !packet.HasIssues)
        {
            return false;
        }
        if (Kinds is { Count: > 0 } kinds && !kinds.Contains(packet.Kind))
        {
            return false;
        }
        if (
            !string.IsNullOrWhiteSpace(Station)
            && !string.Equals(packet.Source, Station.Trim(), StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(Text))
        {
            return true;
        }
        var text = Text.Trim();
        return packet.Source.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (packet.Destination?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
            || (packet.Path?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
            || packet.Raw.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
