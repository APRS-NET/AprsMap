// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Messages;

/// <summary>
/// The Messages panel (docs/features/F2.4-messaging.md, behaviors 1–11): the conversations, the open one's messages,
/// the compose box, and the conversation's Send via and retries. Everything comes from the engine's message stream.
/// </summary>
public sealed partial class MessagesViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly TimeProvider _time;
    private readonly Func<EngineStatus?> _status;
    private readonly Dictionary<string, ChatMessage> _messages = new(StringComparer.Ordinal);
    private readonly List<string> _started = []; // conversations opened from New message, before their first message

    internal MessagesViewModel(IEngineService engine, Localizer text, TimeProvider time, Func<EngineStatus?> status)
    {
        _engine = engine;
        _text = text;
        _time = time;
        _status = status;
        Retries =
        [
            .. Enum.GetValues<MessageRetries>().Select(r => new RetriesChoice(r, _text[$"Messages.Retries.{r}"])),
        ];
    }

    /// <summary>The conversations, newest activity first (behavior 1).</summary>
    public ObservableCollection<ConversationRow> Conversations { get; } = [];

    /// <summary>The open conversation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial ConversationRow? Selected { get; set; }

    /// <summary>
    /// Whether a conversation is open: it takes the panel's whole height, and the list waits behind
    /// <see cref="BackText"/> (behavior 7).
    /// </summary>
    public bool IsOpen => Selected is not null;

    /// <summary>"← Conversations", with the unread count of the others: "← Conversations (2 unread)".</summary>
    [ObservableProperty]
    public partial string BackText { get; private set; } = string.Empty;

    /// <summary>Back to the conversations.</summary>
    [RelayCommand]
    private void Back() => Selected = null;

    private void ShowBack()
    {
        var others = Conversations.Where(c => !ReferenceEquals(c, Selected)).Sum(c => c.Unread);
        BackText =
            others > 0
                ? _text.Format("Messages.BackUnread", new Dictionary<string, object?> { ["count"] = others })
                : _text["Messages.Back"];
    }

    /// <summary>The open conversation's messages, oldest first.</summary>
    public ObservableCollection<MessageRow> Thread { get; } = [];

    /// <summary>What's being written (behavior 6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposeCount))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Compose { get; set; } = string.Empty;

    /// <summary>"2 messages · 120 of 300": how many messages the text makes.</summary>
    public string ComposeCount =>
        Compose.Trim().Length == 0
            ? string.Empty
            : _text.Format(
                "Messages.ComposeCount",
                new Dictionary<string, object?>
                {
                    ["messages"] = Split(Compose.Trim()).Count,
                    ["length"] = Compose.Trim().Length,
                }
            );

    /// <summary>The callsign typed for New message (behavior 3).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string NewCallsign { get; set; } = string.Empty;

    /// <summary>Why something couldn't be done, or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>
    /// Why the panel can't show messages here, or null: a gateway's browser can't read them until operator sign-in
    /// (behavior 16).
    /// </summary>
    [ObservableProperty]
    public partial string? Unavailable { get; private set; }

    /// <summary>"via Direwolf" or "can't send now": which way the next message goes (behavior 8).</summary>
    [ObservableProperty]
    public partial string? RouteText { get; private set; }

    /// <summary>Automatic Send via for the open conversation.</summary>
    [ObservableProperty]
    public partial bool Automatic { get; set; } = true;

    /// <summary>The sources to tick when not automatic: the profile's TX-enabled ones.</summary>
    public ObservableCollection<SourceChoice> Sources { get; } = [];

    /// <summary>The retry choices (behavior 9).</summary>
    public IReadOnlyList<RetriesChoice> Retries { get; }

    /// <summary>The open conversation's retries.</summary>
    [ObservableProperty]
    public partial RetriesChoice? SelectedRetries { get; set; }

    /// <summary>Unread messages in every conversation (behavior 4).</summary>
    public int UnreadTotal => _messages.Values.Count(m => !m.Outgoing && !m.Read);

    private bool _showingSettings;

    /// <summary>Follows the engine's messages until cancelled; on a gateway's browser, says why it can't.</summary>
    internal async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            Unavailable = null;
            await foreach (var message in _engine.WatchMessagesAsync(cancellationToken))
            {
                Apply(message);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Unavailable = _text.Describe(ex);
            await Task.Delay(Timeout.InfiniteTimeSpan, _time, cancellationToken); // nothing to retry until sign-in
        }
    }

    /// <summary>Takes one message from the stream: new, or a new state of one already shown.</summary>
    internal void Apply(ChatMessage message)
    {
        _messages[message.Id] = message;
        _started.RemoveAll(c => string.Equals(c, message.Correspondent, StringComparison.OrdinalIgnoreCase));
        RebuildConversations();
        if (Selected is { } open && Same(open.Correspondent, message.Correspondent))
        {
            var row = Thread.FirstOrDefault(r => r.Message.Id == message.Id);
            if (row is null)
            {
                Thread.Add(new MessageRow(message, _text, this));
            }
            else
            {
                row.Show(message);
            }
            if (!message.Outgoing && !message.Read)
            {
                _ = MarkReadAsync(open.Correspondent); // the conversation is open: read (behavior 4)
            }
        }
        OnPropertyChanged(nameof(UnreadTotal));
    }

    partial void OnSelectedChanged(ConversationRow? value)
    {
        ShowBack();
        Thread.Clear();
        Error = null;
        if (value is null)
        {
            return;
        }
        foreach (
            var message in _messages.Values.Where(m => Same(m.Correspondent, value.Correspondent)).OrderBy(m => m.At)
        )
        {
            Thread.Add(new MessageRow(message, _text, this));
        }
        if (value.Unread > 0)
        {
            _ = MarkReadAsync(value.Correspondent);
        }
        _ = LoadSettingsAsync(value.Correspondent);
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Loads the conversation's Send via, retries and route (behaviors 8, 9).</summary>
    private async Task LoadSettingsAsync(string correspondent)
    {
        Sources.Clear();
        foreach (var source in (_status()?.Sources ?? []).Where(s => s.MaySend))
        {
            Sources.Add(new SourceChoice(source.Id, source.Name, this));
        }
        try
        {
            var conversation = (await _engine.GetConversationsAsync()).FirstOrDefault(c =>
                Same(c.Correspondent, correspondent)
            );
            _showingSettings = true;
            Automatic = conversation?.SendVia.Automatic ?? true;
            foreach (var source in Sources)
            {
                source.IsChecked = conversation?.SendVia.SourceIds?.Contains(source.Id) ?? false;
            }
            SelectedRetries = Retries.First(r => r.Value == (conversation?.Retries ?? MessageRetries.Automatic));
            _showingSettings = false;
            ShowRoute(conversation);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            _showingSettings = false;
            Error = _text.Describe(ex);
        }
    }

    /// <summary>Says which way the next message goes, or why it waits and what to change (behavior 8).</summary>
    private void ShowRoute(Conversation? conversation)
    {
        if (conversation is null)
        {
            ShowNewRoute();
            return;
        }
        RouteText =
            conversation.Route is { } route
                ? _text.Format("Messages.Route", new Dictionary<string, object?> { ["sources"] = route })
            : conversation.Waiting is { } waiting
                ? _text.Format(
                    "Messages.Waiting",
                    new Dictionary<string, object?> { ["reason"] = _text.Format(waiting) }
                )
            : _text["Messages.RouteNone"];
    }

    /// <summary>
    /// A conversation the engine doesn't list yet (nothing written, nothing chosen) goes Automatic to a station not heard
    /// lately: APRS-IS when it can send, otherwise every TNC that can (behavior 8.1). Worked out from the status, so a new
    /// conversation doesn't say nothing can send when something can.
    /// </summary>
    private void ShowNewRoute()
    {
        var status = _status();
        var sources = (status?.Sources ?? []).Where(s => s.MaySend).ToList();
        var aprsIs = sources.FirstOrDefault(s => s.Kind == SourceKind.AprsIs && s.CanTransmit);
        List<string> route = aprsIs is not null
            ? [aprsIs.Name]
            : [.. sources.Where(s => s.Kind != SourceKind.AprsIs && s.CanTransmit).Select(s => s.Name)];
        var waiting = status?.Toggles is { Transmit: false }
            ? new EngineError(EngineErrorCodes.TransmitOff, "Transmit is off.")
            : status?.Transmit?.Blocked ?? sources.Select(s => s.TransmitBlocked).FirstOrDefault(e => e is not null);
        if (waiting?.Code == EngineErrorCodes.TransmitOff)
        {
            route.Clear(); // the master switch is over every source, whatever each one says
        }
        RouteText =
            route.Count > 0
                ? _text.Format(
                    "Messages.Route",
                    new Dictionary<string, object?> { ["sources"] = string.Join(", ", route) }
                )
            : waiting is not null
                ? _text.Format(
                    "Messages.Waiting",
                    new Dictionary<string, object?> { ["reason"] = _text.Format(waiting) }
                )
            : _text["Messages.RouteNone"];
    }

    /// <summary>Asks the engine which way the open conversation's next message goes, keeping what's shown ticked.</summary>
    private async Task RefreshRouteAsync(string correspondent)
    {
        try
        {
            ShowRoute(
                (await _engine.GetConversationsAsync()).FirstOrDefault(c => Same(c.Correspondent, correspondent))
            );
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
        }
    }

    partial void OnAutomaticChanged(bool value) => _ = SaveSettingsAsync();

    partial void OnSelectedRetriesChanged(RetriesChoice? value) => _ = SaveSettingsAsync();

    /// <summary>Saves the open conversation's Send via and retries.</summary>
    internal async Task SaveSettingsAsync()
    {
        if (_showingSettings || Selected is not { } open || SelectedRetries is not { } retries)
        {
            return;
        }
        var via = Automatic ? SendVia.Auto : new SendVia([.. Sources.Where(s => s.IsChecked).Select(s => s.Id)]);
        try
        {
            await _engine.SetConversationAsync(open.Correspondent, via, retries.Value);
            await RefreshRouteAsync(open.Correspondent);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
        }
    }

    private bool CanSend() => Selected is not null && Compose.Trim().Length > 0;

    /// <summary>Sends what's written to the open conversation (behavior 6).</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (Selected is not { } open)
        {
            return;
        }
        try
        {
            await _engine.SendMessageAsync(open.Correspondent, Compose.Trim());
            Compose = string.Empty;
            Error = null;
            await RefreshRouteAsync(open.Correspondent);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = _text.Describe(ex);
        }
    }

    private bool CanStart() => NewCallsign.Trim().Length > 0;

    /// <summary>Opens a conversation with the callsign typed, empty until the first message (behavior 3).</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        Open(NewCallsign);
        NewCallsign = string.Empty;
    }

    /// <summary>Opens the conversation with <paramref name="correspondent"/>, from a station's details too.</summary>
    internal void Open(string correspondent)
    {
        var call = correspondent.Trim().ToUpperInvariant();
        if (call.Length == 0)
        {
            return;
        }
        if (!Conversations.Any(c => Same(c.Correspondent, call)))
        {
            _started.Add(call);
            RebuildConversations();
        }
        Selected = Conversations.First(c => Same(c.Correspondent, call));
    }

    internal async Task CancelAsync(MessageRow row) => await Try(() => _engine.CancelMessageAsync(row.Message.Id));

    internal async Task ResendAsync(MessageRow row) => await Try(() => _engine.ResendMessageAsync(row.Message.Id));

    private async Task MarkReadAsync(string correspondent) => await Try(() => _engine.MarkReadAsync(correspondent));

    private async Task Try(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
        }
    }

    /// <summary>Rebuilds the conversation list from the messages, keeping the open one open.</summary>
    private void RebuildConversations()
    {
        var groups = _messages
            .Values.GroupBy(m => m.Correspondent, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Correspondent: g.Key, Last: g.MaxBy(m => m.At), Unread: g.Count(m => !m.Outgoing && !m.Read)))
            .OrderByDescending(g => g.Last?.At)
            .ToList();
        var wanted = groups
            .Select(g => g.Correspondent)
            .Concat(_started.Where(s => !groups.Any(g => Same(g.Correspondent, s))))
            .ToList();
        for (var i = Conversations.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(w => Same(w, Conversations[i].Correspondent)))
            {
                Conversations.RemoveAt(i);
            }
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var row = Conversations.FirstOrDefault(c => Same(c.Correspondent, wanted[i]));
            if (row is null)
            {
                row = new ConversationRow(wanted[i]);
                Conversations.Insert(Math.Min(i, Conversations.Count), row);
            }
            else if (Conversations.IndexOf(row) != i)
            {
                Conversations.Move(Conversations.IndexOf(row), i);
            }
            var group = groups.FirstOrDefault(g => Same(g.Correspondent, wanted[i]));
            row.Show(group.Last, group.Unread, _text);
        }
        ShowBack();
    }

    /// <summary>The parts <paramref name="text"/> is sent as: at most 67 characters, between words (behavior 6).</summary>
    internal static List<string> Split(string text)
    {
        var parts = new List<string>();
        var rest = text;
        while (rest.Length > 67)
        {
            var cut = rest.LastIndexOf(' ', 67);
            cut = cut <= 0 ? 67 : cut;
            parts.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0)
        {
            parts.Add(rest);
        }
        return parts;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A conversation in the list.</summary>
public sealed partial class ConversationRow(string correspondent) : ObservableObject
{
    /// <summary>The other station.</summary>
    public string Correspondent { get; } = correspondent;

    /// <summary>The newest message's text, or "No messages yet".</summary>
    [ObservableProperty]
    public partial string LastText { get; private set; } = string.Empty;

    /// <summary>Unread received messages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int Unread { get; private set; }

    /// <summary>Whether to show the unread count.</summary>
    public bool HasUnread => Unread > 0;

    /// <summary>"N1ABC, 2 unread: Hello", for screen readers.</summary>
    [ObservableProperty]
    public partial string Spoken { get; private set; } = string.Empty;

    internal void Show(ChatMessage? last, int unread, Localizer text)
    {
        LastText = last?.Text ?? text["Messages.NoMessagesYet"];
        Unread = unread;
        Spoken = text.Format(
            "Messages.ConversationSpoken",
            new Dictionary<string, object?>
            {
                ["station"] = Correspondent,
                ["unread"] = unread,
                ["last"] = LastText,
            }
        );
    }
}

/// <summary>One message in the open conversation (behavior 7).</summary>
public sealed partial class MessageRow : ObservableObject
{
    private readonly Localizer _text;

    internal MessageRow(ChatMessage message, Localizer text, MessagesViewModel owner)
    {
        _text = text;
        Message = message;
        CancelCommand = new AsyncRelayCommand(() => owner.CancelAsync(this), () => CanCancel);
        ResendCommand = new AsyncRelayCommand(() => owner.ResendAsync(this), () => CanResend);
        Show(message);
    }

    /// <summary>The message as last streamed.</summary>
    [ObservableProperty]
    public partial ChatMessage Message { get; private set; }

    /// <summary>Sent by this station: shown on the other side.</summary>
    public bool Outgoing => Message.Outgoing;

    /// <summary>The text.</summary>
    public string Text => Message.Text;

    /// <summary>"Delivered", "Retrying, try 2 of 6", "Not acknowledged: transmitting is off", in words.</summary>
    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    /// <summary>Failed or rejected: the warning colour, said in words too.</summary>
    [ObservableProperty]
    public partial bool IsProblem { get; private set; }

    /// <summary>Whether Cancel is offered: queued or retrying.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool CanCancel { get; private set; }

    /// <summary>Whether Send again is offered: failed, rejected or cancelled.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResendCommand))]
    public partial bool CanResend { get; private set; }

    /// <summary>Stops a queued or retrying message.</summary>
    public IAsyncRelayCommand CancelCommand { get; }

    /// <summary>Sends a finished message again.</summary>
    public IAsyncRelayCommand ResendCommand { get; }

    internal void Show(ChatMessage message)
    {
        Message = message;
        OnPropertyChanged(nameof(Text));
        var time = message.At.ToLocalTime().ToString("t", _text.Culture);
        var state = message.State switch
        {
            ChatMessageState.Received when message.NotAcknowledged => _text["Messages.State.NotAcknowledged"],
            ChatMessageState.Retrying => _text.Format(
                "Messages.State.Retrying",
                new Dictionary<string, object?> { ["try"] = message.Attempts, ["of"] = message.MaxAttempts }
            ),
            _ => _text[$"Messages.State.{message.State}"],
        };
        StateText = time + " · " + state;
        IsProblem = message.State is ChatMessageState.Failed or ChatMessageState.Rejected || message.NotAcknowledged;
        CanCancel = message.State is ChatMessageState.Queued or ChatMessageState.Sent or ChatMessageState.Retrying;
        CanResend = message.State is ChatMessageState.Failed or ChatMessageState.Rejected or ChatMessageState.Cancelled;
    }
}

/// <summary>A source to tick in Send via (behavior 8.2).</summary>
public sealed partial class SourceChoice(string id, string name, MessagesViewModel owner) : ObservableObject
{
    /// <summary>The source's id.</summary>
    public string Id { get; } = id;

    /// <summary>Its name.</summary>
    public string Name { get; } = name;

    /// <summary>Ticked.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => _ = owner.SaveSettingsAsync();
}

/// <summary>A retry choice, with its name (behavior 9).</summary>
public sealed record RetriesChoice(MessageRetries Value, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}
