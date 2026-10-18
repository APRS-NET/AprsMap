// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>
/// The update check (docs/features/F4.15-updates.md, behaviors 1–7): a minute after start and every 24 hours while the
/// engine runs, and on Check now; the last check's time is kept with the settings, so a restart within a day doesn't
/// check again. A failed check is quiet: it's in the status for About, and the next one is at the usual time.
/// </summary>
public sealed partial class Engine
{
    /// <summary>How long after start the first check waits, so start-up isn't slowed (decision 3).</summary>
    internal static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromMinutes(1);

    /// <summary>How often the check runs.</summary>
    internal static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);

    private readonly List<Channel<UpdateStatus>> _updateWatchers = [];
    private readonly SemaphoreSlim _checking = new(1, 1);
    private UpdateChecker? _updateChecker;
    private UpdateStatus? _update;

    /// <summary>The HTTP client the check uses; tests give their own.</summary>
    internal HttpClient? UpdateHttp { get; set; }

    /// <summary>The running version, without build metadata; tests set their own.</summary>
    internal string RunningVersion { get; set; } = Brand.Version.Split('+')[0];

    /// <summary>The <see cref="IHttpClientFactory"/> client name for the update check.</summary>
    public const string UpdatesHttpClient = "updates";

    private readonly IHttpClientFactory? _httpClients;

    private UpdateChecker? Checker =>
        _updateChecker ??= (UpdateHttp ?? _httpClients?.CreateClient(UpdatesHttpClient)) is { } http
            ? new UpdateChecker(http, _options.Updates)
            : null;

    private UpdateChannel ChannelOf(AppSettings settings) =>
        settings.EffectiveUpdates.Channel
        ?? (
            SemVersion.TryParse(RunningVersion, out var running) && !running.IsPreRelease
                ? UpdateChannel.Stable
                : UpdateChannel.PreReleases
        );

    private UpdateStatus CurrentUpdate(AppSettings settings)
    {
        lock (_gate)
        {
            return (_update ?? new UpdateStatus(RunningVersion, ChannelOf(settings))) with
            {
                Channel = ChannelOf(settings),
                LastChecked = settings.EffectiveUpdates.LastChecked ?? _update?.LastChecked,
                CheckDisabled = !_options.Updates.Check,
            };
        }
    }

    /// <inheritdoc />
    public async Task<UpdateStatus> GetUpdateStatusAsync(CancellationToken cancellationToken = default) =>
        CurrentUpdate(await GetSettingsAsync(cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<UpdateStatus> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Updates.Check)
        {
            return await GetUpdateStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        await CheckUpdatesNowAsync(cancellationToken).ConfigureAwait(false);
        return await GetUpdateStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SkipUpdateAsync(string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        await SaveAsync(
                settings with
                {
                    Updates = settings.EffectiveUpdates with { SkippedVersion = version },
                },
                trusted: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        lock (_gate)
        {
            if (_update?.Update?.Version == version)
            {
                _update = _update with { Update = null }; // until a newer one comes out (behavior 9.3)
            }
        }
        PublishUpdate(await GetUpdateStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<UpdateStatus> WatchUpdateStatusAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var channel = Channel.CreateBounded<UpdateStatus>(
            new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
        );
        lock (_gate)
        {
            _updateWatchers.Add(channel);
        }
        try
        {
            yield return await GetUpdateStatusAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var status in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return status;
            }
        }
        finally
        {
            lock (_gate)
            {
                _updateWatchers.Remove(channel);
            }
        }
    }

    private void PublishUpdate(UpdateStatus status)
    {
        lock (_gate)
        {
            foreach (var watcher in _updateWatchers)
            {
                watcher.Writer.TryWrite(status);
            }
        }
    }

    /// <summary>Checks once now: the list, the newest release that counts, the time kept with the settings.</summary>
    internal async Task CheckUpdatesNowAsync(CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _update = CurrentUpdate(settings) with { Checking = true };
            }
            PublishUpdate(CurrentUpdate(settings) with { Checking = true });
            var (releases, error) = Checker is { } checker
                ? await checker.FetchAsync(cancellationToken).ConfigureAwait(false)
                : (null, new EngineError(EngineErrorCodes.UpdatesUnreachable, "There's no HTTP client to check with."));
            var now = _time.GetUtcNow();
            var found = releases is null
                ? null
                : UpdateChecker.Pick(
                    releases,
                    RunningVersion,
                    ChannelOf(settings),
                    settings.EffectiveUpdates.SkippedVersion,
                    _options.Updates
                );
            lock (_gate)
            {
                _update = new UpdateStatus(
                    RunningVersion,
                    ChannelOf(settings),
                    now,
                    error,
                    error is null ? found : _update?.Update, // a failed check keeps what was found before
                    !_options.Updates.Check
                );
            }
            var latest = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            await SaveAsync(
                    latest with
                    {
                        Updates = latest.EffectiveUpdates with { LastChecked = now },
                    },
                    trusted: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            PublishUpdate(CurrentUpdate(latest));
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>
    /// The schedule (behavior 1): a minute after start, or a day after the last check when that's later, then daily. With
    /// Check for updates off no request is made at all (behavior 6).
    /// </summary>
    internal async Task RunUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!_options.Updates.Check)
        {
            return;
        }
        try
        {
            var wait = FirstUpdateCheck;
            while (true)
            {
                var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (settings.EffectiveUpdates.LastChecked is { } last)
                {
                    var due = last + UpdateInterval - _time.GetUtcNow();
                    if (due > wait)
                    {
                        wait = due;
                    }
                }
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (settings.EffectiveUpdates.Check)
                {
                    await CheckUpdatesNowAsync(cancellationToken).ConfigureAwait(false);
                }
                wait = settings.EffectiveUpdates.Check ? UpdateInterval : TimeSpan.FromHours(1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
