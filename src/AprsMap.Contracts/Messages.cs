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
    /// <summary>RF to a station a TNC heard in the last 30 minutes, APRS-IS otherwise.</summary>
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
    bool Read = true
);

/// <summary>A conversation with one station (behavior 1).</summary>
/// <param name="Correspondent">The other station, with its SSID.</param>
/// <param name="Unread">How many received messages are unread.</param>
/// <param name="Last">The newest message.</param>
/// <param name="SendVia">Which way its messages go.</param>
/// <param name="Retries">Which retry schedule they follow.</param>
/// <param name="Route">Which way the next message goes now, by source name ("Direwolf"); null when none can send.</param>
public sealed record Conversation(
    string Correspondent,
    int Unread,
    ChatMessage? Last,
    SendVia SendVia,
    MessageRetries Retries,
    string? Route = null
);
