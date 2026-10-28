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
    /// <summary>The ANSRVR groups the active profile has joined (docs/features/F2.5-bulletins.md, behavior 8).</summary>
    IReadOnlyList<string> Groups();

    /// <summary>Why the active profile may not transmit at all (F2.1 behavior 4), or null.</summary>
    EngineError? ProfileBlock();

    /// <summary>Whether a packet can go out on <paramref name="sourceId"/> now: Transmit on and every F2.1 layer.</summary>
    bool CanSendOn(string sourceId);

    /// <summary>The active profile's ping responder (docs/features/F2.13-ping.md).</summary>
    PingSettings Ping();

    /// <summary>The active profile's away message (docs/features/F2.10-auto-reply.md).</summary>
    AutoReplySettings AutoReply();

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
    private readonly Dictionary<string, Route> _fixedRoutes = new(StringComparer.Ordinal); // pongs: the way the ping came
    private DateTimeOffset? _lastPong;
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.Ordinal); // auto-replies' delay
    private readonly Dictionary<string, DateTimeOffset> _lastAutoReply = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastManualSend;
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
            _fixedRoutes.Clear();
            _notBefore.Clear();
            _lastAutoReply.Clear();
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

    /// <summary>
    /// The conversation a message is in: its correspondent's, or its group's (<c>#NEDR</c>, F2.5 behavior 8), whoever
    /// sent it there.
    /// </summary>
    internal static string KeyOf(ChatMessage message) =>
        message.Group is { } group ? Conversation.ForGroup(group) : message.Correspondent;

    /// <summary>
    /// The conversations, newest activity first (behavior 1). A joined group has one from the start, as a conversation
    /// started by hand does (F2.5 behavior 8); its messages go to ANSRVR, so it shows ANSRVR's route.
    /// </summary>
    public IReadOnlyList<Conversation> Conversations()
    {
        var groups = _host.Groups();
        lock (_gate)
        {
            Conversation Make(string key, int unread, ChatMessage? last)
            {
                var group = Conversation.GroupOf(key);
                var sendTo = group is null ? key : AnsrvrCommand.Ansrvr.Value; // a group's posts go to ANSRVR
                var (via, retries) = Settings(key);
                var route = PlanRoute(sendTo, via);
                return new Conversation(
                    key,
                    unread,
                    last,
                    via,
                    retries,
                    route is null ? null : string.Join(", ", route.SourceIds.Select(NameOf)),
                    route is null ? WhyWaiting(via) : null,
                    group
                );
            }
            var withMessages = _messages
                .GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase)
                .Select(g => Make(g.Key, g.Count(m => !m.Outgoing && !m.Read), g.MaxBy(m => m.At)))
                .OrderByDescending(c => c.Last?.At)
                .ToList();
            bool Has(string key) =>
                withMessages.Any(c => string.Equals(c.Correspondent, key, StringComparison.OrdinalIgnoreCase));
            return
            [
                .. withMessages,
                // Started, with Send via or retries chosen, before the first message; and each joined group.
                .. _conversations
                    .Keys.Concat(groups.Select(Conversation.ForGroup))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(k => !Has(k))
                    .Select(k => Make(k, 0, null)),
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
    public IReadOnlyList<ChatMessage> Send(string to, string text, string? group = null)
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
                ChatMessageState.Queued,
                Group: group // a post shows in its group's conversation (F2.5 behavior 9)
            ))
            .ToList();
        lock (_gate)
        {
            _lastManualSend = now; // the operator is here (F2.10 behavior 4)
            CancelAutoReplies(correspondent);
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
                        && string.Equals(KeyOf(m), correspondent, StringComparison.OrdinalIgnoreCase)
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
                if (_notBefore.TryGetValue(id, out var due))
                {
                    if (now < due)
                    {
                        continue; // an auto-reply's delay (F2.10 behavior 6)
                    }
                    _notBefore.Remove(id);
                }
                var (via, retries) = Settings(message.Correspondent);
                var route = _fixedRoutes.TryGetValue(id, out var fixedRoute)
                    ? _host.CanSendOn(fixedRoute.SourceIds[0])
                        ? fixedRoute
                        : null // a pong goes back only the way its ping came (F2.13 behavior 9)
                    : PlanRoute(message.Correspondent, via);
                if (route is null)
                {
                    continue; // nothing can send it yet: it waits (behavior 10)
                }
                if (fixedRoute is not null)
                {
                    retries = MessageRetries.Automatic; // the schedule for that source (F2.13 behavior 10)
                    _fixedRoutes.Remove(id);
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
        // A joined group's traffic goes to its conversation (F2.5 behavior 8): never a ping, nor answered by the away
        // reply. APRS.NET tells a member's relayed post from ANSRVR's notice (G34, APRS.NET#281).
        var group =
            AnsrvrMessage.TryParse(message, out var ansrvr)
            && _host.Groups().Contains(ansrvr.Group, StringComparer.OrdinalIgnoreCase)
                ? (Group: ansrvr.Group.ToUpperInvariant(), Text: ansrvr.Text.Trim())
                : ((string Group, string Text)?)null;
        var ping = group is null ? PingResultFor(message.From.Value, message.Text, source) : null;
        var received = new ChatMessage(
            Guid.NewGuid().ToString("N"),
            message.From.Value.ToUpperInvariant(),
            Outgoing: false,
            group?.Text ?? message.Text,
            message.ReceivedAt,
            ChatMessageState.Received,
            message.MessageId,
            SourceIds: source is null ? null : [source],
            HeardDirect: isRf ? route.UsedHops.Count == 0 : null,
            // No ack goes back while this station can't send there (behavior 13).
            NotAcknowledged: message.MessageId is not null && (source is null || !_host.CanSendOn(source)),
            Read: ping is not null, // a ping isn't unread (F2.13 behavior 11)
            Ping: ping,
            Group: group?.Group
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
        if (ping is { } result)
        {
            LogPing(_log, received.Correspondent, source ?? "APRS-IS", result);
            if (result == PingResult.Answered)
            {
                QueuePong(received.Correspondent, source!, isRf);
            }
        }
        else if (group is null)
        {
            MaybeAutoReply(received.Correspondent, message.Text, source, isRf);
        }
        SaveLater();
    }

    /// <summary>
    /// What happens to a message from <paramref name="from"/> that may be a ping (docs/features/F2.13-ping.md, behaviors
    /// 4–9): null when it isn't one; otherwise answered, or why not. Answering takes the interval's turn.
    /// </summary>
    private PingResult? PingResultFor(string from, string text, string? source)
    {
        var settings = _host.Ping();
        // Starts with the phrase, ignoring case and spaces at either end (behavior 4).
        if (!text.Trim().StartsWith(settings.EffectivePing, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!settings.On)
        {
            return PingResult.Off;
        }
        if (!MayPing(settings, from))
        {
            return PingResult.NotAllowed;
        }
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_lastPong is { } last && now - last < TimeSpan.FromSeconds(settings.IntervalSeconds))
            {
                return PingResult.TooSoon; // one pong in the interval, for the whole station (behavior 7)
            }
            if (source is null || !_host.CanSendOn(source))
            {
                return PingResult.CantSend; // every rule a message follows (behavior 2)
            }
            _lastPong = now;
            return PingResult.Answered;
        }
    }

    /// <summary>Whether <paramref name="from"/> may ping (behavior 6).</summary>
    private bool MayPing(PingSettings settings, string from)
    {
        var sender = from.Trim().ToUpperInvariant();
        return settings.Who switch
        {
            PingWho.Anyone => true,
            PingWho.MyCallsign => _callsign is { } own && BaseCall(sender) == BaseCall(own.ToUpperInvariant()),
            _ => settings.CallsignList.Any(entry => Matches(entry.Trim().ToUpperInvariant(), sender)),
        };

        static string BaseCall(string call) => call.Split('-')[0];

        // WA0RC* any suffix; WA0RC any SSID of it; WA0RC-7 just that one.
        static bool Matches(string entry, string sender) =>
            entry.EndsWith('*') ? sender.StartsWith(entry[..^1], StringComparison.Ordinal)
            : entry.Contains('-', StringComparison.Ordinal) ? sender == entry
            : BaseCall(sender) == entry;
    }

    /// <summary>
    /// Queues the pong to <paramref name="to"/>: the pong phrase and the UTC time with the date (behavior 8), going back
    /// on <paramref name="source"/> alone (behavior 9), marked automatic (behavior 11). It's sent by the next pump, not
    /// here, which is inside the station's handling of the ping.
    /// </summary>
    private void QueuePong(string to, string source, bool isRf)
    {
        var now = _time.GetUtcNow();
        var text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{_host.Ping().EffectivePong} {now.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z"
        );
        var pong = new ChatMessage(
            Guid.NewGuid().ToString("N"),
            to,
            Outgoing: true,
            text,
            now,
            ChatMessageState.Queued,
            Automatic: true
        );
        lock (_gate)
        {
            _messages.Add(pong);
            _pending.Add(pong.Id);
            _fixedRoutes[pong.Id] = new Route([source], Automatic: false, OverRf: isRf);
            Publish(pong);
        }
        _ = Task.Run(Pump);
    }

    /// <summary>Why an auto-reply wasn't sent (F2.10 behavior 10).</summary>
    private enum AutoReplyHold
    {
        NotAway,
        OncePer,
        Service,
        AutomaticMessage,
        CantSend,
    }

    /// <summary>The services that answer by themselves, any SSID (F2.10 behavior 7.4).</summary>
    private static readonly HashSet<string> Services = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANSRVR",
        "CQSRVR",
        "EMAIL",
        "WLNK",
        "SMSGTE",
        "WXBOT",
        "WXSVR",
        "REPEAT",
        "FINDU",
        "IGATE",
        "QRU",
    };

    /// <summary>
    /// Queues the away message to <paramref name="from"/> when it's on and every rule lets it go (docs/features/
    /// F2.10-auto-reply.md, behaviors 4–8), or logs why not. MessagingStation raises only messages addressed to the
    /// station, so bulletins and groups never get here (behavior 7.3).
    /// </summary>
    private void MaybeAutoReply(string from, string text, string? source, bool isRf)
    {
        var settings = _host.AutoReply();
        if (!settings.On)
        {
            return;
        }
        var now = _time.GetUtcNow();
        AutoReplyHold? hold;
        lock (_gate)
        {
            var trimmed = text.Trim();
            hold =
                Services.Contains(from.Split('-')[0]) ? AutoReplyHold.Service
                : trimmed.StartsWith('?') // a query (F2.8 answers it), never an away message
                || trimmed.StartsWith(settings.EffectiveText, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(_host.Ping().EffectivePong, StringComparison.OrdinalIgnoreCase)
                    ? AutoReplyHold.AutomaticMessage
                : settings.AwayMinutes > 0
                && _lastManualSend is { } manual
                && now - manual < TimeSpan.FromMinutes(settings.AwayMinutes)
                    ? AutoReplyHold.NotAway
                : _lastAutoReply.TryGetValue(from, out var last)
                && now - last < TimeSpan.FromMinutes(settings.OncePerMinutes)
                    ? AutoReplyHold.OncePer
                : source is null || !_host.CanSendOn(source) ? AutoReplyHold.CantSend
                : null;
            if (hold is null)
            {
                _lastAutoReply[from] = now;
                var reply = new ChatMessage(
                    Guid.NewGuid().ToString("N"),
                    from,
                    Outgoing: true,
                    settings.EffectiveText,
                    now,
                    ChatMessageState.Queued,
                    Automatic: true
                );
                _messages.Add(reply);
                _pending.Add(reply.Id);
                _fixedRoutes[reply.Id] = new Route([source!], Automatic: false, OverRf: isRf); // the way it came (8)
                if (settings.DelaySeconds > 0)
                {
                    _notBefore[reply.Id] = now + TimeSpan.FromSeconds(settings.DelaySeconds);
                }
                Publish(reply);
            }
        }
        if (hold is { } why)
        {
            LogAutoReplyHeld(_log, from, why);
            return;
        }
        LogAutoReplyQueued(_log, from, source!, settings.DelaySeconds);
        _ = Task.Run(Pump);
    }

    /// <summary>Cancels the auto-replies still waiting to go to <paramref name="correspondent"/> (behavior 6).</summary>
    private void CancelAutoReplies(string correspondent)
    {
        foreach (var id in _notBefore.Keys.ToList())
        {
            if (
                _messages.FirstOrDefault(m => m.Id == id) is { } waiting
                && string.Equals(waiting.Correspondent, correspondent, StringComparison.OrdinalIgnoreCase)
                && _pending.Remove(id)
            )
            {
                _notBefore.Remove(id);
                _fixedRoutes.Remove(id);
                Update(id, m => m with { State = ChatMessageState.Cancelled, NextTry = null });
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-reply to {To} on {Source} in {Delay} s")]
    private static partial void LogAutoReplyQueued(ILogger logger, string to, string source, int delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "No auto-reply to {To}: {Reason}")]
    private static partial void LogAutoReplyHeld(ILogger logger, string to, AutoReplyHold reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ping from {From} on {Source}: {Result}")]
    private static partial void LogPing(ILogger logger, string from, string source, PingResult result);

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

    /// <summary>How long a save waits after a change, so a burst of them (a message's acks, a busy conversation) is one write.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Saves the history in the background <see cref="SaveDelay"/> after a change, one save at a time; a change during a
    /// save saves again after. The engine flushes what's waiting when it stops (<see cref="FlushAsync"/>).
    /// </summary>
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
                await Task.Delay(SaveDelay, _time).ConfigureAwait(false);
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
    /// <summary>Saves now if a save is waiting, as the engine stops; nothing is written when nothing changed.</summary>
    internal Task FlushPendingAsync() => Volatile.Read(ref _saving) == 1 ? FlushAsync() : Task.CompletedTask;

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
