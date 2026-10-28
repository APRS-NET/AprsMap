// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Messaging;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// Posting bulletins and ANSRVR groups (docs/features/F2.5-bulletins.md, behaviors 5–10): bulletins repeat on APRS.NET's
/// schedule; joining, leaving, posting to and keeping alive a group go to ANSRVR as messages.
/// </summary>
public sealed partial class Engine
{
    /// <summary>How often the joined groups are looked at for a keep-alive.</summary>
    internal static readonly TimeSpan GroupCheck = TimeSpan.FromMinutes(1);

    private readonly OwnBulletins _ownBulletins;
    private readonly ITimer _groupTimer;

    /// <summary>When each profile's groups were last kept alive (a join, post or keep-alive), by profile id.</summary>
    private readonly Dictionary<string, DateTimeOffset> _groupsAlive = new(StringComparer.Ordinal);

    (string? Callsign, EngineError? Held, IReadOnlyList<string> Sources) IBulletinHost.BulletinState()
    {
        lock (_gate)
        {
            var settings = _settings ?? AppSettings.Empty;
            var held = !settings.EffectiveToggles.Transmit
                ? new EngineError(EngineErrorCodes.TransmitOff, "Transmit is off.")
                : ProfileBlock(settings);
            return (settings.GetActiveProfile()?.Callsign, held, [.. TransmitSources(settings).Select(s => s.Id)]);
        }
    }

    bool IBulletinHost.CanSendOn(string sourceId) => ((IMessengerHost)this).CanSendOn(sourceId);

    Task<bool> IBulletinHost.SendAsync(string sourceId, AprsPayload payload, CancellationToken cancellationToken) =>
        SendPayloadAsync(sourceId, payload, cancellationToken);

    /// <inheritdoc />
    public async Task<BulletinView> PostBulletinAsync(BulletinPost post, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        var (checkedPost, error) = OwnBulletins.Check(post);
        if (error is not null)
        {
            throw EngineErrors.ToException(error);
        }
        // What any send needs (behavior 7): refused now, with the reason, rather than held.
        var (_, profile, _) = await TargetsAsync(null, cancellationToken).ConfigureAwait(false);
        return _ownBulletins.Post(checkedPost!, profile.Callsign);
    }

    /// <inheritdoc />
    public Task StopBulletinAsync(string addressee, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addressee);
        _ownBulletins.Stop(addressee);
        return Task.CompletedTask;
    }

    /// <summary>Sends what's due now; for tests.</summary>
    internal Task TickBulletinsAsync() => _ownBulletins.TickAsync();

    /// <inheritdoc />
    IReadOnlyList<string> IMessengerHost.Groups()
    {
        lock (_gate)
        {
            return _settings?.GetActiveProfile()?.Groups ?? [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings.GetActiveProfile()?.Groups ?? [];
    }

    /// <inheritdoc />
    public async Task JoinGroupAsync(string group, CancellationToken cancellationToken = default)
    {
        var command = Ansrvr(() => AnsrvrCommand.Join(Group(group)));
        await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        _messenger.Send(AnsrvrCommand.Ansrvr.ToString(), command.Text);
        await SetGroupsAsync(
                groups => [.. groups.Append(command.Groups[0]).Distinct(StringComparer.Ordinal)],
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LeaveGroupAsync(string group, CancellationToken cancellationToken = default)
    {
        var command = Ansrvr(() => AnsrvrCommand.Leave(Group(group)));
        await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        _messenger.Send(AnsrvrCommand.Ansrvr.ToString(), command.Text);
        await SetGroupsAsync(groups => [.. groups.Where(g => g != command.Groups[0])], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> PostToGroupAsync(
        string group,
        string text,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(text);
        var command = Ansrvr(() => AnsrvrCommand.Post(Group(group), text.Trim()));
        var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var sent = _messenger.Send(AnsrvrCommand.Ansrvr.ToString(), command.Text, command.Groups[0]);
        if (settings.GetActiveProfile() is { } profile)
        {
            lock (_gate)
            {
                _groupsAlive[profile.Id] = _time.GetUtcNow(); // a post keeps its groups alive too
            }
        }
        return sent;
    }

    private static string Group(string group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Trim().ToUpperInvariant();
    }

    /// <summary>The ANSRVR command <paramref name="make"/> writes, or the reason it can't be sent.</summary>
    private static AnsrvrCommand Ansrvr(Func<AnsrvrCommand> make)
    {
        try
        {
            return make();
        }
        catch (ArgumentException ex)
        {
            throw EngineErrors.ToException(
                new EngineError(
                    EngineErrorCodes.BulletinGroupInvalid,
                    "An ANSRVR group has no spaces, and with its text fits in one message."
                ),
                ex
            );
        }
    }

    /// <summary>Changes the active profile's joined groups and saves them; a join counts as keeping them alive.</summary>
    private async Task SetGroupsAsync(
        Func<IReadOnlyList<string>, IReadOnlyList<string>> change,
        CancellationToken cancellationToken
    )
    {
        var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings.GetActiveProfile() is not { } profile)
        {
            return;
        }
        var groups = change(profile.Groups ?? []);
        await SaveAsync(
                settings with
                {
                    Profiles =
                    [
                        .. settings.Profiles.Select(p =>
                            p.Id == profile.Id ? p with { Groups = groups.Count == 0 ? null : groups } : p
                        ),
                    ],
                },
                trusted: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        lock (_gate)
        {
            _groupsAlive[profile.Id] = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// Sends a keep-alive for the active profile's groups when none went for <see cref="AnsrvrCommand.KeepAliveInterval"/>
    /// (behavior 10): ANSRVR drops a member after 12 hours. After a start, the first goes as soon as sending is allowed.
    /// </summary>
    internal void KeepGroupsAlive()
    {
        StationProfile? profile;
        lock (_gate)
        {
            profile = _settings?.GetActiveProfile();
        }
        if (profile is not { Groups.Count: > 0 } || ((IBulletinHost)this).BulletinState().Held is not null)
        {
            return;
        }
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_groupsAlive.TryGetValue(profile.Id, out var last) && now - last < AnsrvrCommand.KeepAliveInterval)
            {
                return;
            }
            _groupsAlive[profile.Id] = now;
        }
        try
        {
            foreach (var command in KeepAlives(profile.Groups))
            {
                _messenger.Send(AnsrvrCommand.Ansrvr.ToString(), command.Text);
            }
        }
        catch (Exception ex) when (ex is EngineOperationException or EngineArgumentException)
        {
            lock (_gate)
            {
                _groupsAlive.Remove(profile.Id); // tried again at the next check
            }
        }
    }

    /// <summary>Keep-alives for <paramref name="groups"/>, as many groups in each as fit in one message.</summary>
    internal static List<AnsrvrCommand> KeepAlives(IReadOnlyList<string> groups)
    {
        var commands = new List<AnsrvrCommand>();
        var batch = new List<string>();
        foreach (var group in groups)
        {
            if (batch.Count > 0 && ("K " + string.Join(' ', batch.Append(group))).Length > BulletinPost.MaxText)
            {
                commands.Add(AnsrvrCommand.KeepAlive(batch));
                batch = [];
            }
            batch.Add(group);
        }
        if (batch.Count > 0)
        {
            commands.Add(AnsrvrCommand.KeepAlive(batch));
        }
        return commands;
    }
}
