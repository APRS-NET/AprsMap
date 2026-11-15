// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>
/// Install updates automatically on the Linux service gateway (docs/features/F1.40-gateway-service-updates.md,
/// behavior 8): with the operator's switch on, the engine makes the Update and restart request itself, a day after the
/// release and only in the night's window, and never again for a version whose install failed.
/// </summary>
public sealed partial class Engine
{
    /// <summary>How long a release is out before it's installed automatically (decision 9).</summary>
    internal static readonly TimeSpan AutoInstallDelay = TimeSpan.FromHours(24);

    /// <summary>Where the night's window starts, in the gateway's local time (decision 10).</summary>
    internal static readonly TimeSpan AutoInstallFrom = TimeSpan.FromHours(2);

    /// <summary>Where it ends.</summary>
    internal static readonly TimeSpan AutoInstallUntil = TimeSpan.FromHours(5);

    /// <summary>How often the engine looks whether an automatic install is due.</summary>
    internal static readonly TimeSpan AutoInstallPoll = TimeSpan.FromMinutes(10);

    /// <summary>Whether this engine may install by itself with these settings: the service, the switch on, checking on.</summary>
    private bool AutoInstalls(AppSettings settings) =>
        _options.Updates.Kind == InstallKind.Service
        && _options.Updates.Check
        && settings.EffectiveUpdates is { AutoInstall: true, Check: true };

    /// <summary>
    /// When <paramref name="update"/> will be installed automatically: the first moment from <paramref name="now"/> on
    /// that's a day after its release and inside the night's window; null when it won't be.
    /// </summary>
    private DateTimeOffset? AutoInstallAt(AppSettings settings, UpdateInfo? update, DateTimeOffset now)
    {
        if (update is null || !AutoInstalls(settings) || update.Version == settings.EffectiveUpdates.AutoInstallFailed)
        {
            return null;
        }
        var due = update.Published + AutoInstallDelay;
        return NextWindow(due > now ? due : now);
    }

    /// <summary><paramref name="at"/> if it's inside the night's window, otherwise the window's next start.</summary>
    private DateTimeOffset NextWindow(DateTimeOffset at)
    {
        var zone = _time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        if (local.TimeOfDay >= AutoInstallFrom && local.TimeOfDay < AutoInstallUntil)
        {
            return at;
        }
        var day = local.TimeOfDay < AutoInstallFrom ? local.Date : local.Date.AddDays(1);
        var start = DateTime.SpecifyKind(day + AutoInstallFrom, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(start))
        {
            start = start.AddHours(1); // the clocks skip 02:00 tonight
        }
        return new DateTimeOffset(start, zone.GetUtcOffset(start));
    }

    /// <summary>
    /// Records a failed install as never to be installed automatically again, then makes the request if one is due
    /// (behavior 8); true when it did.
    /// </summary>
    internal async Task<bool> AutoInstallIfDueAsync(CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!AutoInstalls(settings))
        {
            return false;
        }
        var status = await GetUpdateStatusAsync(cancellationToken).ConfigureAwait(false);
        if (
            status.Install is { Step: ServiceInstallStep.Failed } failed
            && settings.EffectiveUpdates.AutoInstallFailed != failed.Version
        )
        {
            await SaveAsync(
                    settings with
                    {
                        Updates = settings.EffectiveUpdates with { AutoInstallFailed = failed.Version },
                    },
                    trusted: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            LogAutoInstallSkipped(_logger, failed.Version);
            settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            status = await GetUpdateStatusAsync(cancellationToken).ConfigureAwait(false);
            PublishUpdate(status);
        }
        if (
            status.Install is { Running: true }
            || status.Update is not { } update
            || AutoInstallAt(settings, update, _time.GetUtcNow()) is not { } at
            || at > _time.GetUtcNow()
        )
        {
            return false;
        }
        LogAutoInstall(_logger, update.Version);
        await InstallUpdateAsync(update.Version, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Looks every ten minutes whether an automatic install is due, on the service gateway only.</summary>
    internal async Task RunAutoInstallAsync(CancellationToken cancellationToken)
    {
        if (_options.Updates.Kind != InstallKind.Service || !_options.Updates.Check)
        {
            return;
        }
        try
        {
            while (true)
            {
                await Task.Delay(AutoInstallPoll, _time, cancellationToken).ConfigureAwait(false);
                try
                {
                    await AutoInstallIfDueAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is EngineOperationException or IOException or UnauthorizedAccessException)
                {
                    LogAutoInstallFailed(_logger, ex.Message); // tried again at the next look
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing {Version} automatically")]
    private static partial void LogAutoInstall(ILogger logger, string version);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The update to {Version} failed, so it won't be installed automatically; a newer one will be"
    )]
    private static partial void LogAutoInstallSkipped(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't ask for an automatic update: {Reason}")]
    private static partial void LogAutoInstallFailed(ILogger logger, string reason);
}
