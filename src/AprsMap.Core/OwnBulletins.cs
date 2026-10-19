// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Messaging;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>What <see cref="OwnBulletins"/> needs from the engine.</summary>
internal interface IBulletinHost
{
    /// <summary>
    /// The active profile's callsign, why sends must wait (Transmit off, a profile that may not transmit), and the
    /// profile's sources that aren't RX only.
    /// </summary>
    (string? Callsign, EngineError? Held, IReadOnlyList<string> Sources) BulletinState();

    /// <summary>Whether a packet can go out on <paramref name="sourceId"/> now.</summary>
    bool CanSendOn(string sourceId);

    /// <summary>Sends <paramref name="payload"/> on <paramref name="sourceId"/>, listed as Sent; false when it failed.</summary>
    Task<bool> SendAsync(string sourceId, AprsPayload payload, CancellationToken cancellationToken);
}

/// <summary>
/// This station's bulletins and announcements (docs/features/F2.5-bulletins.md, behaviors 5–7), each repeated on
/// APRS.NET's <see cref="BulletinSchedule"/> until it finishes or is stopped, and nothing sent while an F2.1 layer says
/// no. A send that can't go out stays due and is tried again shortly.
/// </summary>
internal sealed class OwnBulletins : IDisposable
{
    /// <summary>How often a bulletin that's due but can't go out yet is tried again.</summary>
    public static readonly TimeSpan RetryWait = TimeSpan.FromSeconds(5);

    private readonly IBulletinHost _host;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Own> _own = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _ticking = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _timer;
    private int _disposed;

    private sealed class Own(BulletinPost post, string callsign, BulletinSchedule schedule, DateTimeOffset posted)
    {
        public BulletinPost Post { get; set; } = post;

        public string Callsign { get; set; } = callsign;

        public BulletinSchedule Schedule { get; } = schedule;

        public DateTimeOffset Posted { get; set; } = posted;
    }

    public OwnBulletins(IBulletinHost host, TimeProvider time)
    {
        _host = host;
        _time = time;
        _timer = time.CreateTimer(_ => Poke(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised with a bulletin of ours whenever it's posted, sent, stopped or finished.</summary>
    public event Action<BulletinView>? Changed;

    /// <summary>
    /// <paramref name="post"/> tidied (ids and groups in capitals, the text trimmed), or the reason it can't be sent
    /// (behavior 5).
    /// </summary>
    public static (BulletinPost? Post, EngineError? Error) Check(BulletinPost post)
    {
        var id = post.Id.Trim().ToUpperInvariant();
        var idOk = post.Kind switch
        {
            BulletinKind.Bulletin => id is [>= '0' and <= '9'],
            BulletinKind.Announcement => id is [>= 'A' and <= 'Z'],
            _ => false,
        };
        if (!idOk)
        {
            return (
                null,
                new EngineError(
                    EngineErrorCodes.BulletinIdInvalid,
                    "A bulletin's id is one digit, 0 to 9; an announcement's is one letter, A to Z."
                )
            );
        }
        var group = string.IsNullOrWhiteSpace(post.Group) ? null : post.Group.Trim().ToUpperInvariant();
        if (group is not null && (group.Length > 5 || !group.All(char.IsAsciiLetterOrDigit)))
        {
            return (
                null,
                new EngineError(
                    EngineErrorCodes.BulletinGroupInvalid,
                    "A bulletin's group is 1 to 5 letters and digits."
                )
            );
        }
        var text = post.Text.Trim();
        if (text.Length is 0 or > BulletinPost.MaxText || text.Any(c => c is < ' ' or > '~' or '|' or '{'))
        {
            return (
                null,
                new EngineError(
                    EngineErrorCodes.BulletinTextInvalid,
                    "A bulletin has 1 to 67 plain letters, digits and punctuation, without |, ~ or {."
                )
            );
        }
        return (post with { Id = id, Group = group, Text = text }, null);
    }

    /// <summary>
    /// Posts <paramref name="post"/>, already checked, from <paramref name="callsign"/>: due at once. One to an addressee
    /// already ours replaces its text and starts its schedule again (behavior 6).
    /// </summary>
    public BulletinView Post(BulletinPost post, string callsign)
    {
        BulletinView view;
        lock (_gate)
        {
            if (_own.TryGetValue(post.Addressee, out var own))
            {
                own.Post = post;
                own.Callsign = callsign;
                own.Posted = _time.GetUtcNow();
                own.Schedule.Restart();
            }
            else
            {
                own = new Own(
                    post,
                    callsign,
                    post.Kind == BulletinKind.Announcement
                        ? BulletinSchedule.ForAnnouncement(_time)
                        : BulletinSchedule.ForBulletin(_time),
                    _time.GetUtcNow()
                );
                _own[post.Addressee] = own;
            }
            view = ViewOf(own);
        }
        Changed?.Invoke(view);
        Poke();
        return view;
    }

    /// <summary>Stops one of ours (behavior 6); false when it isn't ours.</summary>
    public bool Stop(string addressee)
    {
        BulletinView view;
        lock (_gate)
        {
            if (!_own.Remove(addressee.Trim(), out var own))
            {
                return false;
            }
            own.Schedule.Stop();
            view = ViewOf(own) with { Removed = true };
        }
        Changed?.Invoke(view);
        Poke();
        return true;
    }

    /// <summary>Ours now, for a new watcher.</summary>
    public IReadOnlyList<BulletinView> Views()
    {
        lock (_gate)
        {
            return [.. _own.Values.Select(ViewOf)];
        }
    }

    /// <summary>
    /// <paramref name="heard"/> marked as ours with its next send when it's a copy of one of ours heard back, so a copy
    /// coming back doesn't hide that it's ours.
    /// </summary>
    public BulletinView Decorate(BulletinView heard)
    {
        lock (_gate)
        {
            return
                _own.TryGetValue(heard.Addressee, out var own)
                && string.Equals(own.Callsign, heard.From, StringComparison.OrdinalIgnoreCase)
                ? heard with
                {
                    Mine = true,
                    NextSend = own.Schedule.Next,
                }
                : heard;
        }
    }

    /// <summary>Looks again now: posted, stopped, or the Transmit switch or a source changed.</summary>
    public void Poke() => _ = TickAsync();

    /// <summary>Sends what's due and sets the timer for the next one. Ticks run one at a time.</summary>
    internal async Task TickAsync()
    {
        try
        {
            await _ticking.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        try
        {
            var (_, held, sources) = _host.BulletinState();
            List<Own> due;
            var finished = new List<BulletinView>();
            lock (_gate)
            {
                foreach (var (addressee, own) in _own.Where(o => o.Value.Schedule.IsFinished).ToList())
                {
                    _own.Remove(addressee);
                    finished.Add(ViewOf(own) with { Removed = true });
                }
                due = held is null ? [.. _own.Values.Where(o => o.Schedule.IsDue)] : [];
            }
            foreach (var view in finished)
            {
                Changed?.Invoke(view);
            }
            var stillDue = false;
            foreach (var own in due)
            {
                var sent = false;
                foreach (var id in own.Post.SourceIds is { Count: > 0 } ticked ? ticked.Intersect(sources) : sources)
                {
                    if (
                        _host.CanSendOn(id)
                        && await _host.SendAsync(id, PayloadOf(own.Post), _stop.Token).ConfigureAwait(false)
                    )
                    {
                        sent = true;
                    }
                }
                BulletinView view;
                lock (_gate)
                {
                    if (sent)
                    {
                        own.Schedule.Sent();
                    }
                    else
                    {
                        stillDue = true; // tried again shortly
                    }
                    view = ViewOf(own);
                }
                if (sent)
                {
                    Changed?.Invoke(view);
                }
            }
            lock (_gate)
            {
                SetTimer(held is not null || stillDue);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _ticking.Release();
        }
    }

    /// <summary>The message a bulletin goes out as: to its addressee, without a message id.</summary>
    internal static MessagePayload PayloadOf(BulletinPost post) =>
        new()
        {
            Kind = post.Kind == BulletinKind.Announcement ? MessageKind.Announcement : MessageKind.Bulletin,
            Addressee = post.Addressee,
            Text = post.Text,
        };

    /// <summary>Wakes for the next send, or sooner when one is waiting on the station or a source. Call under <see cref="_gate"/>.</summary>
    private void SetTimer(bool waiting)
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }
        var now = _time.GetUtcNow();
        var next = _own
            .Values.Select(o => o.Schedule.Next)
            .OfType<DateTimeOffset>()
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        var wake =
            next == DateTimeOffset.MaxValue ? Timeout.InfiniteTimeSpan
            : waiting || next <= now ? RetryWait
            : next - now;
        _timer.Change(wake, Timeout.InfiniteTimeSpan);
    }

    private static BulletinView ViewOf(Own own) =>
        new(
            $"{own.Callsign}|{(own.Post.Kind == BulletinKind.Announcement ? MessageKind.Announcement : MessageKind.Bulletin)}|{own.Post.Addressee}|{own.Post.Id}",
            own.Post.Kind,
            own.Callsign,
            own.Post.Addressee,
            own.Post.Id,
            own.Post.Group,
            own.Post.Text,
            own.Posted,
            own.Schedule.LastSent ?? own.Posted,
            null,
            Mine: true,
            NextSend: own.Schedule.Next
        );

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // the engine can be disposed twice, as two services
        }
        _stop.Cancel();
        _timer.Dispose();
        _stop.Dispose();
    }
}
