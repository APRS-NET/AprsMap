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
public sealed record PacketView(
    DateTimeOffset ReceivedAt,
    string Source,
    string Type,
    string Summary,
    string Raw,
    double? Latitude = null,
    double? Longitude = null,
    string? SymbolName = null
);
