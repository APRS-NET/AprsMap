// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.Packets;

/// <summary>One row of the packet list (F1.8 behaviors 1–3): the packet, its text in the viewer's language, and whether it's expanded.</summary>
public sealed partial class PacketRow : ObservableObject
{
    /// <summary>How much of the raw text after an issue's position is highlighted, at most.</summary>
    private const int FaultLength = 12;

    internal PacketRow(PacketView packet, Localizer text, TimeZoneInfo zone)
    {
        Packet = packet;
        var local = TimeZoneInfo.ConvertTime(packet.ReceivedAt, zone);
        Time = local.ToString("T", text.Culture);
        Issues =
        [
            .. (packet.Issues ?? []).Select(issue => new PacketIssueRow(
                text[
                    issue.Arg(EngineErrorArgs.Severity) == EngineErrorArgs.SeverityError
                        ? "Packets.Severity.Error"
                        : "Packets.Severity.Warning"
                ],
                text.Format(issue)
            )),
        ];
        (RawBefore, RawFault, RawAfter) = Split(packet);
    }

    /// <summary>The packet as the engine sent it.</summary>
    public PacketView Packet { get; }

    /// <summary>The sending station.</summary>
    public string Source => Packet.Source;

    /// <summary>The one-line summary.</summary>
    public string Summary => Packet.Summary;

    /// <summary>The packet as received.</summary>
    public string Raw => Packet.Raw;

    /// <summary>When it was received, in the viewer's time zone and language.</summary>
    public string Time { get; }

    /// <summary>The packet's symbol as its two characters, for the icon; null when it has none.</summary>
    public string? Symbol => Packet.Symbol;

    /// <summary>The symbol's name, for the icon's accessible name.</summary>
    public string? SymbolName => Packet.SymbolName;

    /// <summary>Whether APRS.NET reported issues (the warning marker).</summary>
    public bool HasIssues => Packet.HasIssues;

    /// <summary>Whether it was carried inside another station's packet (the third-party marker).</summary>
    public bool IsThirdParty => Packet.Markers.HasFlag(PacketMarkers.ThirdParty);

    /// <summary>Each issue in words, for the expanded row.</summary>
    public IReadOnlyList<PacketIssueRow> Issues { get; }

    /// <summary>The raw text before the first issue's position (all of it without one).</summary>
    public string RawBefore { get; }

    /// <summary>The raw text at the first issue's position, highlighted; empty without one.</summary>
    public string RawFault { get; }

    /// <summary>The raw text after the highlighted part.</summary>
    public string RawAfter { get; }

    /// <summary>Whether the row shows its raw text and issues.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>The line written by Save (F1.8 behavior 10): the time received in UTC, then the raw text.</summary>
    internal string LogLine =>
        string.Create(CultureInfo.InvariantCulture, $"{Packet.ReceivedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} {Raw}");

    /// <summary>
    /// The raw text cut around the first issue that gives a position: up to the next space, at most a few characters,
    /// and at least one.
    /// </summary>
    private static (string, string, string) Split(PacketView packet)
    {
        var raw = packet.Raw;
        foreach (var issue in packet.Issues ?? [])
        {
            if (
                int.TryParse(
                    issue.Arg(EngineErrorArgs.Offset),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var at
                )
                && at >= 0
                && at < raw.Length
            )
            {
                var end = raw.IndexOf(' ', at);
                var length = Math.Clamp((end < 0 ? raw.Length : end) - at, 1, FaultLength);
                length = Math.Min(length, raw.Length - at);
                return (raw[..at], raw.Substring(at, length), raw[(at + length)..]);
            }
        }
        return (raw, string.Empty, string.Empty);
    }
}

/// <summary>One parse issue in words.</summary>
/// <param name="Severity">"Error" or "Warning", in words, so it isn't told by colour alone.</param>
/// <param name="Text">What's wrong.</param>
public sealed record PacketIssueRow(string Severity, string Text);
