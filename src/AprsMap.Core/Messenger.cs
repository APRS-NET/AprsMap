// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Messaging;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AprsMap.Core;

/// <summary>What the messenger needs from the engine: who may send where, and the sending itself.</summary>
internal interface IMessengerHost
{
    /// <summary>Why the active profile may not transmit at all (F2.1 behavior 4), or null.</summary>
    EngineError? ProfileBlock();

    /// <summary>Whether a packet can go out on <paramref name="sourceId"/> now: Transmit on and every F2.1 layer.</summary>
    bool CanSendOn(string sourceId);

    /// <summary>The profile's sources that may send (not RX only), in order, with whether each is RF (a TNC).</summary>
    IReadOnlyList<(string Id, string Name, bool IsRf)> TransmitSources();

    /// <summary>
    /// Why a packet can't go out on <paramref name="sourceId"/> now, or, with null, on any source (F2.1 behavior 7a);
    /// null when it can.
    /// </summary>
    EngineError? WhyNotOn(string? sourceId);

    /// <summary>Sends <paramref name="payload"/> on <paramref name="sourceId"/>, listed as Sent; false when it failed.</summary>
    Task<bool> SendAsync(string sourceId, MessagePayload payload, CancellationToken cancellationToken);
}

/// <summary>
/// The active profile's conversations (docs/features/F2.4-messaging.md) on APRS.NET's <see cref="MessagingStation"/>:
/// the offline queue, the route each message takes (Send via), the state of each message as the app shows it, unread
/// counts and the stored history.
/// </summary>
internal sealed partial class Messenger : IDisposable
{
    /// <summary>The most characters one compose may have (behavior 6).</summary>
    public const int MaxComposeLength = 300;

    /// <summary>The most characters one APRS message carries (APRS101 chapter 14).</summary>
    public const int MaxMessageLength = 67;

    /// <summary>How recently a TNC must have heard a station for Automatic to send over RF (behavior 8.1).</summary>
    public static readonly TimeSpan RfHeardWindow = TimeSpan.FromMinutes(30);

    /// <summary>How long a message may wait queued before it fails (behavior 10).</summary>
    public static readonly TimeSpan QueueLimit = TimeSpan.FromHours(24);

    /// <summary>How many messages a conversation keeps, and for how long (behavior 5).</summary>
    public const int KeepPerConversation = 500;

    /// <summary>How long messages are kept (behavior 5).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    private readonly object _gate = new();
    private readonly IMessengerHost _host;
    private readonly ILogger _log;
    private readonly LoggingState _logging;
    private readonly IMessageStore _store;
    private readonly TimeProvider _time;
    private readonly List<ChatMessage> _messages = [];
    private readonly Dictionary<string, (SendVia Via, MessageRetries Retries)> _conversations = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly List<string> _pending = []; // queued ids not yet handed to the station, in order
    private readonly Dictionary<OutgoingMessage, string> _inFlight = [];
    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string SourceId, DateTimeOffset At)> _heardOnRf = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly List<System.Threading.Channels.Channel<ChatMessage>> _watchers = [];
    private readonly ITimer _timer;
    private MessagingStation? _station;
    private CancellationTokenSource? _pump;
    private string? _profileId;
    private string? _callsign;
    private string? _lastMessageId;
    private int _saving;

    /// <summary>Which sources one outgoing message goes on, and whether it fell back to APRS-IS (behavior 8).</summary>
    private sealed record Route(IReadOnlyList<string> SourceIds, bool Automatic, bool OverRf, bool FellBack = false);

    public Messenger(
        IMessengerHost host,
        IMessageStore store,
        TimeProvider time,
        ILogger? logger = null,
        LoggingState? logging = null
    )
    {
        _host = host;
        _store = store;
        _time = time;
        _log = logger ?? NullLogger.Instance;
        _logging = logging ?? new LoggingState();
        _timer = time.CreateTimer(_ => Pump(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Unread received messages, for the status bar (behavior 4).</summary>
    public int Unread
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count(m => !m.Outgoing && !m.Read);
            }
        }
    }

    /// <summary>
    /// Takes up the conversations of <paramref name="profileId"/> as <paramref name="callsign"/>, loading what was kept,
    /// or stops when there's no profile. Nothing happens when it's the one already in use.
    /// </summary>
    public async Task UseAsync(string? profileId, string? callsign, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (profileId == _profileId && string.Equals(callsign, _callsign, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        var archive = profileId is null
            ? null
            : await _store.LoadAsync(profileId, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            StopStation();
            _messages.Clear();
            _conversations.Clear();
            _pending.Clear();
            _routes.Clear();
            _profileId = profileId;
            _callsign = callsign;
            _lastMessageId = archive?.LastMessageId;
            if (archive is not null)
            {
                foreach (var message in archive.Messages)
                {
                    // A message in flight when the app closed lost its tries: it's shown failed, to send again.
                    _messages.Add(
                        message.State is ChatMessageState.Sent or ChatMessageState.Retrying
                            ? message with
                            {
                                State = ChatMessageState.Failed,
                                NextTry = null,
                            }
                            : message
                    );
                    if (message.State == ChatMessageState.Queued)
                    {
                        _pending.Add(message.Id);
                    }
                }
                foreach (var conversation in archive.Conversations)
                {
                    _conversations[conversation.Correspondent] = (conversation.SendVia, conversation.Retries);
                }
            }
            if (callsign is not null && Callsign.TryParse(callsign, out var station))
            {
                _station = new MessagingStation(
                    station,
                    new MessagingStationOptions { TimeProvider = _time, LastMessageId = _lastMessageId }
                );
                _station.MessageReceived += OnReceived;
                _station.LastMessageIdChanged += (_, _) =>
                {
                    lock (_gate)
                    {
                        _lastMessageId = _station?.LastMessageId;
                    }
                    SaveLater();
                };
                _pump = new CancellationTokenSource();
                _ = PumpOutgoingAsync(_station, _pump.Token);
            }
            foreach (var watcher in _watchers)
            {
                watcher.Writer.TryComplete(); // a new profile: each viewer starts a new stream
            }
            _watchers.Clear();
        }
        Pump();
    }

    /// <summary>Takes in a packet received from <paramref name="sourceId"/>; <paramref name="heardHere"/> on a TNC.</summary>
    public void Receive(AprsPacket packet, string? sourceId, bool heardHere)
    {
        MessagingStation? station;
        lock (_gate)
        {
            station = _station;
            if (heardHere && sourceId is not null && packet.Source is { } from)
            {
                _heardOnRf[from.Value] = (sourceId, _time.GetUtcNow());
            }
        }
        station?.Receive(packet, sourceId);
    }

    /// <summary>The conversations, newest activity first (behavior 1).</summary>
    public IReadOnlyList<Conversation> Conversations()
    {
        lock (_gate)
        {
            return
            [
                .. _messages
                    .GroupBy(m => m.Correspondent, StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var (via, retries) = Settings(g.Key);
                        var route = PlanRoute(g.Key, via);
                        return new Conversation(
                            g.Key,
                            g.Count(m => !m.Outgoing && !m.Read),
                            g.MaxBy(m => m.At),
                            via,
                            retries,
                            route is null ? null : string.Join(", ", route.SourceIds.Select(NameOf)),
                            route is null ? WhyWaiting(via) : null
                        );
                    })
                    .OrderByDescending(c => c.Last?.At),
                // Started, with Send via or retries chosen, before the first message.
                .. _conversations
                    .Keys.Where(k =>
                        !_messages.Any(m => string.Equals(m.Correspondent, k, StringComparison.OrdinalIgnoreCase))
                    )
                    .Select(k =>
                    {
                        var (via, retries) = Settings(k);
                        var route = PlanRoute(k, via);
                        return new Conversation(
                            k,
                            0,
                            null,
                            via,
                            retries,
                            route is null ? null : string.Join(", ", route.SourceIds.Select(NameOf)),
                            route is null ? WhyWaiting(via) : null
                        );
                    }),
            ];
        }
    }

    /// <summary>The messages kept, then each one added or changed.</summary>
    public async IAsyncEnumerable<ChatMessage> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatMessage>();
        List<ChatMessage> kept;
        lock (_gate)
        {
            kept = [.. _messages];
            _watchers.Add(channel);
        }
        try
        {
            foreach (var message in kept)
            {
                yield return message;
            }
            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            lock (_gate)
            {
                _watchers.Remove(channel);
            }
        }
    }

    /// <summary>
    /// Queues <paramref name="text"/> to <paramref name="to"/> as messages of at most 67 characters (behaviors 6, 10).
    /// </summary>
    public IReadOnlyList<ChatMessage> Send(string to, string text)
    {
        var correspondent = to.Trim().ToUpperInvariant();
        if (correspondent.Length is 0 or > 9 || !Callsign.TryParse(correspondent, out _))
        {
            throw EngineErrors.ToException(
                new EngineError(
                    EngineErrorCodes.MessageCallsignInvalid,
                    $"'{to}' isn't a callsign a message can go to.",
                    new Dictionary<string, string> { [EngineErrorArgs.Value] = to }
                )
            );
        }
        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > MaxComposeLength)
        {
            throw EngineErrors.ToException(
                new EngineError(EngineErrorCodes.MessageTooLong, "A message has 1 to 300 characters.")
            );
        }
        if (trimmed.Any(c => c is < ' ' or > '~' or '|' or '{'))
        {
            throw EngineErrors.ToException(
                new EngineError(
                    EngineErrorCodes.MessageTextInvalid,
                    "A message can only have plain letters, digits and punctuation, without |, ~ or {."
                )
            );
        }
        if (_host.ProfileBlock() is { } blocked)
        {
            throw EngineErrors.ToException(blocked);
        }
        var now = _time.GetUtcNow();
        var made = Split(trimmed)
            .Select(part => new ChatMessage(
                Guid.NewGuid().ToString("N"),
                correspondent,
                Outgoing: true,
                part,
                now,
                ChatMessageState.Queued
            ))
            .ToList();
        lock (_gate)
        {
            foreach (var message in made)
            {
                _messages.Add(message);
                _pending.Add(message.Id);
                Publish(message);
            }
        }
        Pump();
        SaveLater();
        return made;
    }

    /// <summary>Splits <paramref name="text"/> into messages of at most 67 characters, between words where it can.</summary>
    internal static IEnumerable<string> Split(string text)
    {
        var rest = text;
        while (rest.Length > MaxMessageLength)
        {
            var cut = rest.LastIndexOf(' ', MaxMessageLength);
            if (cut <= 0)
            {
                cut = MaxMessageLength;
            }
            yield return rest[..cut].TrimEnd();
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    /// <summary>Stops a queued or retrying message (behavior 7).</summary>
    public void Cancel(string id)
    {
        lock (_gate)
        {
            if (_pending.Remove(id))
            {
                Update(id, m => m with { State = ChatMessageState.Cancelled, NextTry = null });
            }
            else if (_inFlight.FirstOrDefault(p => p.Value == id).Key is { } outgoing)
            {
                _station?.Cancel(outgoing);
            }
        }
        SaveLater();
    }

    /// <summary>Sends a finished message's text again, as a new message (behavior 7).</summary>
    public void Resend(string id)
    {
        ChatMessage? message;
        lock (_gate)
        {
            message = _messages.FirstOrDefault(m => m.Id == id && m.Outgoing);
        }
        if (message is { State: ChatMessageState.Failed or ChatMessageState.Rejected or ChatMessageState.Cancelled })
        {
            Send(message.Correspondent, message.Text);
        }
    }

    /// <summary>Marks every message from <paramref name="correspondent"/> read (behavior 4).</summary>
    public void MarkRead(string correspondent)
    {
        lock (_gate)
        {
            foreach (
                var message in _messages
                    .Where(m =>
                        !m.Outgoing
                        && !m.Read
                        && string.Equals(m.Correspondent, correspondent, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToList()
            )
            {
                Update(message.Id, m => m with { Read = true });
            }
        }
        SaveLater();
    }

    /// <summary>Sets a conversation's Send via and retries (behaviors 8, 9).</summary>
    public void SetConversation(string correspondent, SendVia via, MessageRetries retries)
    {
        lock (_gate)
        {
            _conversations[correspondent.Trim().ToUpperInvariant()] = (via, retries);
        }
        SaveLater();
        Pump();
    }

    /// <summary>
    /// Hands queued messages to the station when a source can take them, in order, and fails those queued too long
    /// (behavior 10). Called on each send, on a timer, and when the engine's state changes.
    /// </summary>
    public void Pump()
    {
        lock (_gate)
        {
            if (_station is not { } station)
            {
                return;
            }
            var now = _time.GetUtcNow();
            foreach (var id in _pending.ToList())
            {
                if (_messages.FirstOrDefault(m => m.Id == id) is not { } message)
                {
                    _pending.Remove(id);
                    continue;
                }
                if (now - message.At > QueueLimit)
                {
                    _pending.Remove(id);
                    Update(id, m => m with { State = ChatMessageState.Failed });
                    continue;
                }
                var (via, retries) = Settings(message.Correspondent);
                if (PlanRoute(message.Correspondent, via) is not { } route)
                {
                    continue; // nothing can send it yet: it waits (behavior 10)
                }
                if (!Callsign.TryParse(message.Correspondent, out var to))
                {
                    _pending.Remove(id);
                    Update(id, m => m with { State = ChatMessageState.Failed });
                    continue;
                }
                _pending.Remove(id);
                _routes[id] = route;
                var outgoing = station.Send(to, message.Text, ProfileFor(retries, route.OverRf));
                Track(outgoing, id);
            }
        }
    }

    private void Track(OutgoingMessage outgoing, string id)
    {
        _inFlight[outgoing] = id;
        outgoing.StateChanged += (_, _) => OnStateChanged(outgoing);
        outgoing.Attempted += (_, _) => OnStateChanged(outgoing); // a retry keeps its state; its tries and next time move
        OnStateChanged(outgoing);
    }

    private void OnStateChanged(OutgoingMessage outgoing)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(outgoing, out var id))
            {
                return;
            }
            var state = outgoing.State switch
            {
                OutgoingMessageState.Queued => ChatMessageState.Queued,
                OutgoingMessageState.Sent => ChatMessageState.Sent,
                OutgoingMessageState.Retrying => ChatMessageState.Retrying,
                OutgoingMessageState.Delivered => ChatMessageState.Delivered,
                OutgoingMessageState.Rejected => ChatMessageState.Rejected,
                OutgoingMessageState.Failed => ChatMessageState.Failed,
                _ => ChatMessageState.Cancelled,
            };
            if (
                state == ChatMessageState.Failed
                && _routes.TryGetValue(id, out var route)
                && route is { Automatic: true, OverRf: true, FellBack: false }
                && _host.TransmitSources().FirstOrDefault(s => !s.IsRf) is { Id: { } aprsIs }
                && _station is { } station
            )
            {
                // Every RF try went unacked: once more over APRS-IS, for when no IGate hears this station (behavior 8.1).
                _inFlight.Remove(outgoing);
                _routes[id] = new Route([aprsIs], Automatic: true, OverRf: false, FellBack: true);
                Track(station.Send(outgoing.To, outgoing.Text, RetryProfile.AprsIs), id);
                return;
            }
            if (outgoing.IsFinished)
            {
                _inFlight.Remove(outgoing);
                LogSettled(_log, outgoing.To.Value, outgoing.MessageId ?? "-", state, outgoing.Attempts);
            }
            Update(
                id,
                m =>
                    m with
                    {
                        State = state,
                        MessageId = outgoing.MessageId ?? m.MessageId,
                        Attempts = outgoing.Attempts,
                        MaxAttempts = outgoing.RetryProfile.Sends,
                        NextTry = outgoing.IsFinished ? null : outgoing.NextDue,
                    }
            );
        }
        SaveLater();
    }

    private void OnReceived(object? sender, MessageReceivedEventArgs e)
    {
        var message = e.Message;
        var source = message.Source;
        var route = PathAnalysis.FromPacket(message.Packet);
        var isRf = source is not null && _host.TransmitSources().Any(s => s.Id == source && s.IsRf);
        var received = new ChatMessage(
            Guid.NewGuid().ToString("N"),
            message.From.Value.ToUpperInvariant(),
            Outgoing: false,
            message.Text,
            message.ReceivedAt,
            ChatMessageState.Received,
            message.MessageId,
            SourceIds: source is null ? null : [source],
            HeardDirect: isRf ? route.UsedHops.Count == 0 : null,
            // No ack goes back while this station can't send there (behavior 13).
            NotAcknowledged: message.MessageId is not null && (source is null || !_host.CanSendOn(source)),
            Read: false
        );
        // Who, when and its ID; the text only with Log message texts on (docs/features/F1.32-logging.md, behavior 5.2).
        LogMessageReceived(
            _log,
            received.Correspondent,
            message.MessageId ?? "-",
            source ?? "APRS-IS",
            _logging.Current.MessageTexts ? message.Text : "(text not logged)"
        );
        lock (_gate)
        {
            _messages.Add(received);
            Publish(received);
            Trim(received.Correspondent);
        }
        SaveLater();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Message from {From} ({Id}) on {Source}: {Text}")]
    private static partial void LogMessageReceived(ILogger logger, string from, string id, string source, string text);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message to {To} ({Id}) {State} after {Attempts} tries")]
    private static partial void LogSettled(ILogger logger, string to, string id, ChatMessageState state, int attempts);

    /// <summary>Sends what the station hands back: messages on their route, acks and rejs where the message came from.</summary>
    private async Task PumpOutgoingAsync(MessagingStation station, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in station.ReadOutgoingAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyList<string> targets;
                if (packet.Payload.Kind is MessageKind.Acknowledgement or MessageKind.Rejection)
                {
                    var back = packet.Source ?? _host.TransmitSources().FirstOrDefault(s => !s.IsRf).Id;
                    targets = back is not null && _host.CanSendOn(back) ? [back] : [];
                }
                else
                {
                    lock (_gate)
                    {
                        var match = _inFlight.FirstOrDefault(p =>
                            string.Equals(p.Key.To.Value, packet.To.Value, StringComparison.OrdinalIgnoreCase)
                            && p.Key.MessageId == packet.Payload.MessageId
                        );
                        var id = match.Value;
                        targets =
                            id is not null && _routes.TryGetValue(id, out var route)
                                ? [.. route.SourceIds.Where(_host.CanSendOn)]
                                : [];
                    }
                }
                foreach (var target in targets)
                {
                    await _host.SendAsync(target, packet.Payload, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Which sources a message to <paramref name="correspondent"/> goes on now (behavior 8), or null when none can send.
    /// Call under the lock.
    /// </summary>
    private Route? PlanRoute(string correspondent, SendVia via)
    {
        var sources = _host.TransmitSources();
        if (!via.Automatic)
        {
            var chosen = via.SourceIds!.Where(id => sources.Any(s => s.Id == id) && _host.CanSendOn(id)).ToList();
            return chosen.Count == 0
                ? null
                : new Route(chosen, Automatic: false, OverRf: chosen.Any(id => sources.Any(s => s.Id == id && s.IsRf)));
        }
        if (
            _heardOnRf.TryGetValue(correspondent, out var heard)
            && _time.GetUtcNow() - heard.At <= RfHeardWindow
            && sources.Any(s => s.Id == heard.SourceId && s.IsRf)
            && _host.CanSendOn(heard.SourceId)
        )
        {
            return new Route([heard.SourceId], Automatic: true, OverRf: true);
        }
        if (sources.FirstOrDefault(s => !s.IsRf && _host.CanSendOn(s.Id)) is { Id: { } aprsIs })
        {
            return new Route([aprsIs], Automatic: true, OverRf: false);
        }
        // No APRS-IS that can send, as on an RF-only station: every TNC that can, so a station not heard lately is
        // still reached through the digipeaters (behavior 8.1).
        var tncs = sources.Where(s => s.IsRf && _host.CanSendOn(s.Id)).Select(s => s.Id).ToList();
        return tncs.Count > 0 ? new Route(tncs, Automatic: true, OverRf: true) : null;
    }

    /// <summary>
    /// Why a message sent <paramref name="via"/> waits (behavior 8): the first chosen source's reason, or for Automatic
    /// the first source's, or why nothing may send at all. Call under the lock.
    /// </summary>
    private EngineError? WhyWaiting(SendVia via)
    {
        var sources = _host.TransmitSources();
        var candidates = via.Automatic
            ? sources.Select(s => s.Id)
            : via.SourceIds!.Where(id => sources.Any(s => s.Id == id));
        return candidates.Select(_host.WhyNotOn).FirstOrDefault(e => e is not null) ?? _host.WhyNotOn(null);
    }

    /// <summary>
    /// Takes sources that are gone out of every conversation's Send via; one left with none goes back to Automatic
    /// (behavior 8.2). Called when the settings change, and after the conversations load.
    /// </summary>
    public void KeepSources(IReadOnlyCollection<string> sourceIds)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var (correspondent, (via, retries)) in _conversations.ToList())
            {
                if (via.Automatic || via.SourceIds!.All(sourceIds.Contains))
                {
                    continue;
                }
                var kept = via.SourceIds!.Where(sourceIds.Contains).ToList();
                _conversations[correspondent] = (kept.Count == 0 ? SendVia.Auto : new SendVia(kept), retries);
                changed = true;
            }
        }
        if (changed)
        {
            SaveLater();
        }
    }

    private string NameOf(string sourceId) =>
        _host.TransmitSources().FirstOrDefault(s => s.Id == sourceId).Name ?? sourceId;

    private (SendVia Via, MessageRetries Retries) Settings(string correspondent) =>
        _conversations.TryGetValue(correspondent, out var settings)
            ? settings
            : (SendVia.Auto, MessageRetries.Automatic);

    /// <summary>The retry schedule for <paramref name="retries"/> (behavior 9).</summary>
    internal static RetryProfile ProfileFor(MessageRetries retries, bool overRf) =>
        retries switch
        {
            MessageRetries.Default => RetryProfile.Default,
            MessageRetries.AprsIs => RetryProfile.AprsIs,
            MessageRetries.SlowHf => RetryProfile.SlowHf,
            MessageRetries.None => RetryProfile.None,
            _ => overRf ? RetryProfile.Default : RetryProfile.AprsIs,
        };

    /// <summary>Replaces message <paramref name="id"/> and tells the viewers. Call under the lock.</summary>
    private void Update(string id, Func<ChatMessage, ChatMessage> change)
    {
        var index = _messages.FindIndex(m => m.Id == id);
        if (index < 0)
        {
            return;
        }
        var updated = change(_messages[index]);
        if (updated == _messages[index])
        {
            return;
        }
        _messages[index] = updated;
        Publish(updated);
    }

    private void Publish(ChatMessage message)
    {
        foreach (var watcher in _watchers)
        {
            watcher.Writer.TryWrite(message);
        }
    }

    /// <summary>Keeps 500 messages per conversation, for 90 days (behavior 5). Call under the lock.</summary>
    private void Trim(string correspondent)
    {
        var cutoff = _time.GetUtcNow() - KeepFor;
        _messages.RemoveAll(m => m.At < cutoff && !_pending.Contains(m.Id));
        var theirs = _messages
            .Where(m => string.Equals(m.Correspondent, correspondent, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var old in theirs.Take(Math.Max(0, theirs.Count - KeepPerConversation)))
        {
            _messages.Remove(old);
        }
    }

    /// <summary>Saves the history in the background, one save at a time; a change during a save saves again after.</summary>
    private void SaveLater()
    {
        if (Interlocked.Exchange(ref _saving, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                string? profile;
                MessageArchive archive;
                lock (_gate)
                {
                    profile = _profileId;
                    archive = new MessageArchive(
                        [.. _messages],
                        [.. _conversations.Select(c => new ConversationSettings(c.Key, c.Value.Via, c.Value.Retries))],
                        _lastMessageId
                    );
                    Volatile.Write(ref _saving, 0);
                }
                if (profile is not null)
                {
                    await _store.SaveAsync(profile, archive, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                // The next change saves again.
            }
        });
    }

    /// <summary>Saves now, for tests and for shutting down.</summary>
    internal async Task FlushAsync()
    {
        string? profile;
        MessageArchive archive;
        lock (_gate)
        {
            profile = _profileId;
            archive = new MessageArchive(
                [.. _messages],
                [.. _conversations.Select(c => new ConversationSettings(c.Key, c.Value.Via, c.Value.Retries))],
                _lastMessageId
            );
        }
        if (profile is not null)
        {
            await _store.SaveAsync(profile, archive, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void StopStation()
    {
        _pump?.Cancel();
        _pump?.Dispose();
        _pump = null;
        _station?.Dispose();
        _station = null;
        _inFlight.Clear();
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            StopStation();
        }
    }
}

/// <summary>A profile's kept conversations (docs/features/F2.4-messaging.md, behavior 5).</summary>
/// <param name="Messages">The messages, oldest first.</param>
/// <param name="Conversations">Each conversation's Send via and retries, where they aren't the defaults.</param>
/// <param name="LastMessageId">The last message ID used, so IDs don't repeat after a restart.</param>
public sealed record MessageArchive(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ConversationSettings> Conversations,
    string? LastMessageId
);

/// <summary>A conversation's Send via and retries.</summary>
public sealed record ConversationSettings(string Correspondent, SendVia SendVia, MessageRetries Retries);
