// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// The away message (docs/features/F2.10-auto-reply.md): sent once per correspondent, after a delay, while no message
/// has been sent by hand for a while. Per profile, off by default.
/// </summary>
/// <param name="On">Send it (behavior 1).</param>
/// <param name="Text">The reply; null for the default (behavior 2).</param>
/// <param name="AwayMinutes">Only after this long without a message sent by hand; 0 for always (behavior 4).</param>
/// <param name="OncePerMinutes">At most once per correspondent in this many minutes (behavior 5).</param>
/// <param name="DelaySeconds">Wait this long first, cancelled by a reply by hand (behavior 6).</param>
public sealed record AutoReplySettings(
    bool On = false,
    string? Text = null,
    int AwayMinutes = AutoReplySettings.DefaultAwayMinutes,
    int OncePerMinutes = AutoReplySettings.DefaultOncePerMinutes,
    int DelaySeconds = AutoReplySettings.DefaultDelaySeconds
)
{
    /// <summary>The reply unless one is set (proposed).</summary>
    public const string DefaultText = "I'm away from the station and will reply when I'm back.";

    /// <summary>15 minutes without a message sent by hand.</summary>
    public const int DefaultAwayMinutes = 15;

    /// <summary>Once an hour per correspondent.</summary>
    public const int DefaultOncePerMinutes = 60;

    /// <summary>Half a minute's wait.</summary>
    public const int DefaultDelaySeconds = 30;

    /// <summary>The longest away time and once-per interval: a day.</summary>
    public const int MaxMinutes = 1440;

    /// <summary>The longest delay: ten minutes.</summary>
    public const int MaxDelaySeconds = 600;

    /// <summary>Off, with the defaults.</summary>
    public static AutoReplySettings Off { get; } = new();

    /// <summary>The reply in effect.</summary>
    public string EffectiveText => string.IsNullOrWhiteSpace(Text) ? DefaultText : Text.Trim();
}
