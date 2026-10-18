// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// My last position coming back (docs/features/F3.3-rf-paths.md, behaviors 1, 2, 4): each position sent is followed for
/// <see cref="FollowFor"/>, and each copy heard says how it got out: repeated by a digipeater on this station's TNC,
/// gated to APRS-IS by an IGate, or sent to APRS-IS by this station.
/// </summary>
public sealed partial class Engine
{
    /// <summary>How long a position's copies are listened for (decision 1).</summary>
    internal static readonly TimeSpan FollowFor = TimeSpan.FromMinutes(10);

    /// <summary>How close together sends of the same position count as one (a beacon on several sources).</summary>
    private static readonly TimeSpan SameSend = TimeSpan.FromSeconds(30);

    private Followed? _followed;

    /// <summary>A position sent on <paramref name="source"/>: followed, or added to the one just sent.</summary>
    private void FollowSent(AprsPacket packet, SourceSettings source)
    {
        if (packet.Type != PacketType.Position || packet.Source?.Value is not { } callsign)
        {
            return;
        }
        var now = _time.GetUtcNow();
        var information = packet.Information.ToArray();
        lock (_gate)
        {
            if (
                _followed is { } same
                && now - same.At < SameSend
                && same.Information.AsSpan().SequenceEqual(information)
            )
            {
                same.Sources.Add(source.Name);
                same.OnRf |= source.Kind != SourceKind.AprsIs;
            }
            else
            {
                _followed = new Followed(callsign, information, now) { OnRf = source.Kind != SourceKind.AprsIs };
                _followed.Sources.Add(source.Name);
            }
            if (source.Kind == SourceKind.AprsIs)
            {
                _followed.Heard.Add(new HeardBack(HeardBackKind.Direct, source.Name, now));
            }
        }
    }

    /// <summary>A packet received: a copy of the position followed, and how it got out, if it is one.</summary>
    private void CheckHeardBack(AprsPacket packet, string? sourceId)
    {
        Followed? followed;
        lock (_gate)
        {
            followed = _followed;
        }
        if (
            followed is null
            || _time.GetUtcNow() - followed.At > FollowFor
            || !string.Equals(packet.Source?.Value, followed.Callsign, StringComparison.OrdinalIgnoreCase)
            || !packet.Information.Span.SequenceEqual(followed.Information)
        )
        {
            return;
        }
        var route = PathAnalysis.FromPacket(packet);
        HeardBack? heard =
            route.IsGatedFromRadio && route.EntryStation is { } igate
                ? new HeardBack(HeardBackKind.Gated, igate.Value, _time.GetUtcNow())
            : route.Digipeaters.Count > 0 && IsOwnReceiver(sourceId)
                ? new HeardBack(HeardBackKind.Repeated, route.Digipeaters[^1].Value, _time.GetUtcNow())
            : null;
        if (heard is null)
        {
            return;
        }
        lock (_gate)
        {
            if (!followed.Heard.Any(h => h.Kind == heard.Kind && h.By == heard.By))
            {
                followed.Heard.Add(heard);
            }
        }
    }

    /// <summary>The last position and how it got out, for the status and this station's details. Call under <see cref="_gate"/>.</summary>
    private LastPositionHeard? LastPositionHeard() =>
        _followed is { } f
            ? new LastPositionHeard(f.At, [.. f.Sources], [.. f.Heard], _time.GetUtcNow() - f.At <= FollowFor)
            {
                SentOnRf = f.OnRf,
            }
            : null;

    /// <summary>A position being followed.</summary>
    private sealed record Followed(string Callsign, byte[] Information, DateTimeOffset At)
    {
        public List<string> Sources { get; } = [];

        public List<HeardBack> Heard { get; } = [];

        public bool OnRf { get; set; }
    }
}
