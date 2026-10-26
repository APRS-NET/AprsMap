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
    IReadOnlyList<string>? SeenOn = null,
    PacketAnomalies Anomalies = PacketAnomalies.None
)
{
    /// <summary>Whether APRS.NET reported any issue with the packet.</summary>
    public bool HasIssues => Issues is { Count: > 0 };

    /// <summary>Whether the packet has parse issues or doesn't fit its station's track (F1.20).</summary>
    public bool HasProblems => HasIssues || Anomalies != PacketAnomalies.None;
}

/// <summary>
/// What looks wrong about a position report given its station's track (docs/features/F1.20-anomalies.md, behavior 1),
/// from APRS.NET's track analysis (G24).
/// </summary>
[Flags]
public enum PacketAnomalies
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The same position sent again too soon, or a late copy of an earlier one.</summary>
    RepeatedPosition = 1,

    /// <summary>Further from the previous position than the station could have gone.</summary>
    ImpossibleSpeed = 2,

    /// <summary>Less than a second after the station's previous report.</summary>
    RapidRepeat = 4,

    /// <summary>The report's own time is earlier than the previous timestamped report's.</summary>
    TimeBackwards = 8,

    /// <summary>The position moved only because its ambiguity changed.</summary>
    AmbiguityJump = 16,
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

    /// <summary>
    /// Heard by this station's own receiver, a KISS TNC (F3.1 behavior 8), with <see cref="Direct"/> or
    /// <see cref="Digipeated"/> saying how.
    /// </summary>
    HeardHere = 16,

    /// <summary>Sent by this station (docs/features/F2.1-own-station.md, behavior 13).</summary>
    Sent = 32,

    /// <summary>Repeated by this station as a digipeater (docs/features/F3.5-digipeater.md, behavior 5).</summary>
    DigipeatedHere = 64,
}

/// <summary>
/// What the packet list shows (F1.8 behavior 7). Every part that is set must match; a filter with nothing set matches
/// every packet.
/// </summary>
/// <param name="Text">Matches the source, destination, path or raw text, ignoring case.</param>
/// <param name="Station">Only this station's packets, ignoring case.</param>
/// <param name="Kinds">Only these kinds; null or empty for all.</param>
/// <param name="IssuesOnly">Only packets with parse issues or track anomalies (F1.20 behavior 4).</param>
/// <param name="SourceId">
/// Only packets heard on this source (F3.1 behavior 10): arrived on it, or also seen on it (<see cref="PacketView.SeenOn"/>).
/// </param>
public sealed record PacketFilter(
    string? Text = null,
    string? Station = null,
    IReadOnlyList<PacketKind>? Kinds = null,
    bool IssuesOnly = false,
    string? SourceId = null
)
{
    /// <summary>Whether the filter lets every packet through.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Text)
        && string.IsNullOrWhiteSpace(Station)
        && Kinds is not { Count: > 0 }
        && !IssuesOnly
        && SourceId is null;

    /// <summary>Whether <paramref name="packet"/> passes the filter.</summary>
    public bool Matches(PacketView packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (IssuesOnly && !packet.HasProblems)
        {
            return false;
        }
        if (Kinds is { Count: > 0 } kinds && !kinds.Contains(packet.Kind))
        {
            return false;
        }
        if (SourceId is { } source && packet.SourceId != source && packet.SeenOn?.Contains(source) != true)
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
