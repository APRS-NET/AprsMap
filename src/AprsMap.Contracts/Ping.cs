// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>Who may ping the station (docs/features/F2.13-ping.md, behavior 6).</summary>
public enum PingWho
{
    /// <summary>Any station.</summary>
    Anyone,

    /// <summary>The profile's base callsign, with any SSID.</summary>
    MyCallsign,

    /// <summary>The callsigns listed in <see cref="PingSettings.Callsigns"/>.</summary>
    Callsigns,
}

/// <summary>What happened to a ping (behavior 11).</summary>
public enum PingResult
{
    /// <summary>A pong went back.</summary>
    Answered,

    /// <summary>Answer pings is off.</summary>
    Off,

    /// <summary>The sender isn't one that may ping.</summary>
    NotAllowed,

    /// <summary>Within the interval since the last pong.</summary>
    TooSoon,

    /// <summary>The source it came on can't send now (Transmit off, RX only, switched off, not licensed).</summary>
    CantSend,
}

/// <summary>
/// The ping responder (docs/features/F2.13-ping.md): a message starting with <see cref="EffectivePing"/> is answered
/// with <see cref="EffectivePong"/> and the time, the way it came. Per profile, off by default.
/// </summary>
/// <param name="On">Answer pings (behavior 1).</param>
/// <param name="Ping">The ping phrase; null for "{name} ping" (behavior 3).</param>
/// <param name="Pong">The pong phrase; null for "{name} pong".</param>
/// <param name="Who">Who may ping (behavior 6).</param>
/// <param name="Callsigns">For <see cref="PingWho.Callsigns"/>: callsigns, a base call matching any SSID, a final <c>*</c> any suffix.</param>
/// <param name="IntervalSeconds">At most one pong in this many seconds, for the whole station; never under 10 (behavior 7).</param>
public sealed record PingSettings(
    bool On = false,
    string? Ping = null,
    string? Pong = null,
    PingWho Who = PingWho.Anyone,
    IReadOnlyList<string>? Callsigns = null,
    int IntervalSeconds = PingSettings.DefaultIntervalSeconds
)
{
    /// <summary>One pong a minute, unless set.</summary>
    public const int DefaultIntervalSeconds = 60;

    /// <summary>The shortest interval (maintainer, 2026-10-19).</summary>
    public const int MinIntervalSeconds = 10;

    /// <summary>The longest interval: a day.</summary>
    public const int MaxIntervalSeconds = 86_400;

    /// <summary>The longest phrase, so a pong with its time fits one message.</summary>
    public const int MaxPhraseLength = 40;

    /// <summary>Off, with the defaults.</summary>
    public static PingSettings Off { get; } = new();

    /// <summary>The ping phrase in effect: <see cref="Ping"/>, or the product's name and "ping".</summary>
    public string EffectivePing => string.IsNullOrWhiteSpace(Ping) ? $"{Brand.DisplayName} ping" : Ping.Trim();

    /// <summary>The pong phrase in effect: <see cref="Pong"/>, or the product's name and "pong".</summary>
    public string EffectivePong => string.IsNullOrWhiteSpace(Pong) ? $"{Brand.DisplayName} pong" : Pong.Trim();

    /// <summary>The callsigns, none when there are none.</summary>
    public IReadOnlyList<string> CallsignList => Callsigns ?? [];

    /// <summary>Equal when every field is, comparing the callsigns in order.</summary>
    public bool Equals(PingSettings? other) =>
        other is not null
        && On == other.On
        && Ping == other.Ping
        && Pong == other.Pong
        && Who == other.Who
        && CallsignList.SequenceEqual(other.CallsignList)
        && IntervalSeconds == other.IntervalSeconds;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(On, Ping, Pong, Who, string.Join(',', CallsignList), IntervalSeconds);
}
