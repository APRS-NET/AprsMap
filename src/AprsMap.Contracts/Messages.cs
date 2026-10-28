// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>Where a message is or was in its life (docs/features/F2.4-messaging.md, behaviors 7, 13).</summary>
public enum ChatMessageState
{
    /// <summary>Received from the correspondent.</summary>
    Received,

    /// <summary>Waiting: for an earlier message to the same station, or for a source that can send.</summary>
    Queued,

    /// <summary>Sent, waiting for the ack.</summary>
    Sent,

    /// <summary>Sent again, still waiting for the ack.</summary>
    Retrying,

    /// <summary>The correspondent acked it.</summary>
    Delivered,

    /// <summary>The correspondent rejected it.</summary>
    Rejected,

    /// <summary>No ack after every try.</summary>
    Failed,

    /// <summary>Cancelled before it was delivered.</summary>
    Cancelled,
}

/// <summary>Which retry schedule a conversation's messages follow (behavior 9).</summary>
public enum MessageRetries
{
    /// <summary>The APRS-IS schedule over APRS-IS, the default one over RF.</summary>
    Automatic,

    /// <summary>The default schedule, for RF.</summary>
    Default,

    /// <summary>Fewer, slower tries, for APRS-IS.</summary>
    AprsIs,

    /// <summary>Long waits, for slow HF modems.</summary>
    SlowHf,

    /// <summary>Sent once, not retried.</summary>
    None,
}

/// <summary>
/// Which way a conversation's messages go (behavior 8): <see cref="Automatic"/>, or each of
/// <see cref="SourceIds"/>.
/// </summary>
/// <param name="SourceIds">The sources ticked when not automatic.</param>
public sealed record SendVia(IReadOnlyList<string>? SourceIds = null)
{
    /// <summary>
    /// RF to a station a TNC heard in the last 30 minutes, APRS-IS otherwise, and every TNC when APRS-IS can't send.
    /// </summary>
    public bool Automatic => SourceIds is null || SourceIds.Count == 0;

    /// <summary>Automatic.</summary>
    public static SendVia Auto { get; } = new();
}

/// <summary>One message in a conversation (behaviors 6, 7, 12, 13).</summary>
/// <param name="Id">Stable identifier for the app; never shown.</param>
/// <param name="Correspondent">The other station, with its SSID.</param>
/// <param name="Outgoing">Sent by this station, rather than received.</param>
/// <param name="Text">The text.</param>
/// <param name="At">When it was received, or written.</param>
/// <param name="State">Where it is in its life.</param>
/// <param name="MessageId">The APRS message ID, when it has one.</param>
/// <param name="Attempts">How many times it was sent.</param>
/// <param name="MaxAttempts">How many tries its schedule allows.</param>
/// <param name="NextTry">When it's sent again, while retrying.</param>
/// <param name="SourceIds">The sources it came on, or was sent on.</param>
/// <param name="HeardDirect">For a message received on RF: heard straight from the sender.</param>
/// <param name="NotAcknowledged">Received while this station couldn't send, so no ack went back (behavior 13).</param>
/// <param name="Read">For a received message: its conversation was open since it came.</param>
/// <param name="Automatic">Sent by the station itself: a pong (docs/features/F2.13-ping.md, behavior 11).</param>
/// <param name="Ping">For a received ping: what happened to it (F2.13 behavior 11); null for any other message.</param>
/// <param name="Group">
/// The ANSRVR group it belongs to (docs/features/F2.5-bulletins.md, behaviors 8, 9): a member's post relayed to this
/// station (from that member), ANSRVR's notice about the group (from ANSRVR), or this station's post (to ANSRVR, its
/// text the post's whole <c>CQ GROUP text</c>); null for any other message.
/// </param>
public sealed record ChatMessage(
    string Id,
    string Correspondent,
    bool Outgoing,
    string Text,
    DateTimeOffset At,
    ChatMessageState State,
    string? MessageId = null,
    int Attempts = 0,
    int MaxAttempts = 0,
    DateTimeOffset? NextTry = null,
    IReadOnlyList<string>? SourceIds = null,
    bool? HeardDirect = null,
    bool NotAcknowledged = false,
    bool Read = true,
    bool Automatic = false,
    PingResult? Ping = null,
    string? Group = null
);

/// <summary>A conversation with one station (behavior 1).</summary>
/// <param name="Correspondent">The other station, with its SSID.</param>
/// <param name="Unread">How many received messages are unread.</param>
/// <param name="Last">The newest message.</param>
/// <param name="SendVia">Which way its messages go.</param>
/// <param name="Retries">Which retry schedule they follow.</param>
/// <param name="Route">Which way the next message goes now, by source name ("Direwolf"); null when none can send.</param>
/// <param name="Waiting">
/// Why nothing can send it now, naming the source (a <c>transmit.*</c> code; behavior 8), or null when
/// <paramref name="Route"/> is set.
/// </param>
/// <param name="Group">
/// For an ANSRVR group's conversation (F2.5 behavior 8): the group; its <paramref name="Correspondent"/> is then
/// <see cref="ForGroup"/> of it, and what's sent to it is posted to the group.
/// </param>
public sealed record Conversation(
    string Correspondent,
    int Unread,
    ChatMessage? Last,
    SendVia SendVia,
    MessageRetries Retries,
    string? Route = null,
    EngineError? Waiting = null,
    string? Group = null
)
{
    /// <summary>The prefix of a group conversation's correspondent: no callsign starts with it.</summary>
    public const string GroupPrefix = "#";

    /// <summary>The correspondent of <paramref name="group"/>'s conversation, such as <c>#NEDR</c>.</summary>
    public static string ForGroup(string group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return GroupPrefix + group.Trim().ToUpperInvariant();
    }

    /// <summary>The group a conversation's <paramref name="correspondent"/> stands for; null for a station.</summary>
    public static string? GroupOf(string correspondent) =>
        correspondent is { Length: > 1 } && correspondent.StartsWith(GroupPrefix, StringComparison.Ordinal)
            ? correspondent[GroupPrefix.Length..].Trim().ToUpperInvariant()
            : null;
}
