// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;

namespace AprsMap.Server;

/// <summary>
/// Signed-in operators' sessions (docs/features/F2.9-operator-sign-in.md, behaviors 3, 5, 6, 13), kept on the gateway so
/// they can end at once: 12 hours from the last use and 7 days at most (decision 3); ended by sign-out, or when the
/// operator's password changes or the operator is removed. A session's hub connections close when it ends. Sessions
/// are kept in memory, so a restart signs everyone out.
/// </summary>
internal sealed class OperatorSessions : IDisposable
{
    /// <summary>How long a session lasts unused.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(12);

    /// <summary>The longest a session lasts, used or not.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly OperatorStore _operators;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string SessionId, HubCallerContext Context)> _connections = new(
        StringComparer.Ordinal
    );
    private readonly ITimer _sweep;

    public OperatorSessions(OperatorStore operators, TimeProvider time)
    {
        _operators = operators;
        _time = time;
        _sweep = time.CreateTimer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>One operator's session.</summary>
    public sealed class Session(string id, string name, string stamp, DateTimeOffset started)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        /// <summary>The operator's password stamp when it started.</summary>
        public string Stamp { get; } = stamp;

        public DateTimeOffset Started { get; } = started;

        public DateTimeOffset LastUsed { get; set; } = started;

        /// <summary>When it ends unless it's used again.</summary>
        public DateTimeOffset ExpiresAt =>
            LastUsed + IdleTimeout < Started + MaxAge ? LastUsed + IdleTimeout : Started + MaxAge;
    }

    /// <summary>Starts a session for <paramref name="op"/>; its id goes in the cookie.</summary>
    public Session Start(OperatorRecord op)
    {
        var session = new Session(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            op.Name,
            PasswordHashing.Stamp(op.PasswordHash),
            _time.GetUtcNow()
        );
        lock (_gate)
        {
            _sessions[session.Id] = session;
        }
        return session;
    }

    /// <summary>
    /// The session <paramref name="id"/>, marked used now, or null when it has ended: signed out, unused too long, too
    /// old, or its operator's password changed (behavior 3).
    /// </summary>
    public Session? Use(string id)
    {
        Session? session;
        lock (_gate)
        {
            session = _sessions.GetValueOrDefault(id);
        }
        if (session is null)
        {
            return null;
        }
        if (!IsValid(session, _time.GetUtcNow()))
        {
            End(id);
            return null;
        }
        lock (_gate)
        {
            session.LastUsed = _time.GetUtcNow();
        }
        return session;
    }

    private bool IsValid(Session session, DateTimeOffset now) =>
        now < session.ExpiresAt
        && _operators.Find(session.Name) is { } op
        && PasswordHashing.Stamp(op.PasswordHash) == session.Stamp;

    /// <summary>Ends a session (sign-out, behavior 6), and closes its hub connections.</summary>
    public void End(string id)
    {
        List<HubCallerContext> close;
        lock (_gate)
        {
            _sessions.Remove(id);
            close = [.. _connections.Values.Where(c => c.SessionId == id).Select(c => c.Context)];
        }
        foreach (var context in close)
        {
            context.Abort();
        }
    }

    /// <summary>A hub connection opened with session <paramref name="id"/>: it closes when the session ends.</summary>
    public void Attach(string id, HubCallerContext context)
    {
        lock (_gate)
        {
            _connections[context.ConnectionId] = (id, context);
        }
    }

    /// <summary>A hub connection closed.</summary>
    public void Detach(string connectionId)
    {
        lock (_gate)
        {
            _connections.Remove(connectionId);
        }
    }

    /// <summary>Ends the sessions that have expired or whose password changed, closing their connections (behavior 13).</summary>
    internal void Sweep()
    {
        List<string> ended;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            ended = [.. _sessions.Values.Where(s => !IsValid(s, now)).Select(s => s.Id)];
        }
        foreach (var id in ended)
        {
            End(id);
        }
    }

    public void Dispose() => _sweep.Dispose();
}

/// <summary>
/// Limits on guessing (behavior 9): after 5 failed sign-ins from one address in 15 minutes, that address waits 15
/// minutes; after 10 for one name in an hour, that name waits an hour, from every address. Nothing is checked while
/// waiting. Also counts the failures in the last day, for the operator (behavior 10).
/// </summary>
internal sealed class SignInLimiter(TimeProvider time)
{
    public const int AddressLimit = 5;
    public static readonly TimeSpan AddressWindow = TimeSpan.FromMinutes(15);
    public const int NameLimit = 10;
    public static readonly TimeSpan NameWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private const int MaxKeys = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Tries> _addresses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tries> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _failures = new();
    private readonly Dictionary<string, DateTimeOffset> _repeats = new(StringComparer.Ordinal);

    private sealed class Tries
    {
        public Queue<DateTimeOffset> Failed { get; } = new();

        public DateTimeOffset WaitUntil { get; set; }
    }

    /// <summary>How long <paramref name="address"/> or <paramref name="name"/> must wait before trying again; null for none.</summary>
    public TimeSpan? Wait(string address, string name)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var until = Max(
                _addresses.GetValueOrDefault(address)?.WaitUntil,
                _names.GetValueOrDefault(name)?.WaitUntil
            );
            return until > now ? until - now : null;
        }
    }

    /// <summary>How long <paramref name="address"/> must wait before trying again, whatever the name; null for none.</summary>
    public TimeSpan? Wait(string address)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var until = _addresses.GetValueOrDefault(address)?.WaitUntil ?? DateTimeOffset.MinValue;
            return until > now ? until - now : null;
        }
    }

    /// <summary>
    /// A failed sign-in from <paramref name="address"/> for <paramref name="name"/>; for a wrong API key, no name, so
    /// guessing from one address can't lock the key out for everyone (docs/features/F1.38-gateway-api-key.md,
    /// behavior 7).
    /// </summary>
    /// <param name="address">Where the try came from.</param>
    /// <param name="name">The operator name tried; null for an API key.</param>
    /// <param name="repeat">
    /// A fingerprint of what was tried, when trying the same thing again isn't guessing: an app with an old API key
    /// sends it on every connection, so the same wrong key from the same address counts once per window (#450).
    /// </param>
    public void Failed(string address, string? name, string? repeat = null)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (repeat is not null && Repeated(address + "\n" + repeat, now))
            {
                return;
            }
            Count(_addresses, address, now, AddressWindow, AddressLimit);
            if (name is not null)
            {
                Count(_names, name, now, NameWindow, NameLimit);
            }
            _failures.Enqueue(now);
            while (_failures.TryPeek(out var oldest) && now - oldest > Day)
            {
                _failures.Dequeue();
            }
        }
    }

    /// <summary>Failed sign-ins in the last day.</summary>
    public int FailedToday
    {
        get
        {
            var now = time.GetUtcNow();
            lock (_gate)
            {
                return _failures.Count(f => now - f <= Day);
            }
        }
    }

    private bool Repeated(string key, DateTimeOffset now)
    {
        if (_repeats.TryGetValue(key, out var first) && now - first < AddressWindow)
        {
            return true;
        }
        if (_repeats.Count >= MaxKeys)
        {
            foreach (var stale in _repeats.Where(r => now - r.Value >= AddressWindow).Select(r => r.Key).ToList())
            {
                _repeats.Remove(stale);
            }
        }
        _repeats[key] = now;
        return false;
    }

    private static void Count(Dictionary<string, Tries> all, string key, DateTimeOffset now, TimeSpan window, int limit)
    {
        if (all.Count >= MaxKeys)
        {
            // Bounded: forget keys with nothing recent and no wait.
            foreach (
                var stale in all.Where(t => t.Value.WaitUntil <= now && !t.Value.Failed.Any(f => now - f < window))
                    .Select(t => t.Key)
                    .ToList()
            )
            {
                all.Remove(stale);
            }
        }
        if (!all.TryGetValue(key, out var tries))
        {
            all[key] = tries = new Tries();
        }
        tries.Failed.Enqueue(now);
        while (tries.Failed.TryPeek(out var oldest) && now - oldest >= window)
        {
            tries.Failed.Dequeue();
        }
        if (tries.Failed.Count >= limit)
        {
            tries.WaitUntil = now + window;
            tries.Failed.Clear();
        }
    }

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset? b) =>
        (a ?? DateTimeOffset.MinValue) > (b ?? DateTimeOffset.MinValue) ? a!.Value : b ?? DateTimeOffset.MinValue;
}
