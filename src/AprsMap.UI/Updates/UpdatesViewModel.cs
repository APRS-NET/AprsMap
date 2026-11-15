// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Updates;

/// <summary>
/// Updates in the app (docs/features/F4.15-updates.md, behaviors 8–10, 15, 16): the engine's update status followed, a
/// quiet notice in the status bar when there's an update, its dialog, and Check now. On a gateway only the operator
/// follows it; a viewer's refused call leaves nothing to show. An installed desktop build also downloads and installs
/// the update (behavior 11).
/// </summary>
public sealed partial class UpdatesViewModel(
    IEngineService engine,
    Localizer text,
    Func<Uri, Task> openUri,
    IAppInstaller? installer = null,
    TimeProvider? time = null
) : ObservableObject
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ITimer? _autoTimer;
    private ITimer? _restartTimer;
    private SynchronizationContext? _context;
    private DateTimeOffset? _autoFailedCheck; // the check after which an automatic download failed (F4.15 11a)
    private bool _whatsNewShown;

    /// <summary>
    /// Whether this is the desktop app as a gateway's client (F1.39), whose automatic updates are never newer than the
    /// gateway (F4.15 behavior 11a.2).
    /// </summary>
    public bool Client { get; init; }

    /// <summary>The gateway's version, for a gateway's client; null until it's known.</summary>
    [ObservableProperty]
    public partial string? GatewayVersion { get; set; }

    partial void OnGatewayVersionChanged(string? value) => ConsiderAutoInstall();

    partial void OnStatusChanged(UpdateStatus? value)
    {
        _context ??= SynchronizationContext.Current;
        ConsiderAutoInstall();
        ShowWhatsNew();
    }

    /// <summary>When a downloaded update restarts the app by itself (F4.15 behavior 11b); null when it doesn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeText))]
    public partial DateTimeOffset? RestartAt { get; private set; }

    /// <summary>
    /// Install updates automatically (F4.15 behavior 11a), in an installed desktop build: downloads the offered update
    /// once it's due (the engine's <see cref="UpdateStatus.AutoInstallAt"/>), and as a gateway's client only when it's
    /// no newer than the gateway; looks again every ten minutes while one is due later.
    /// </summary>
    private void ConsiderAutoInstall()
    {
        if (
            Gateway
            || installer?.CanInstall != true
            || Status is not { Update: { } update, AutoInstallAt: { } at } status
            || Install is InstallState.Downloading or InstallState.Ready
            || Install == InstallState.Failed && _autoFailedCheck == status.LastChecked
        )
        {
            return;
        }
        if (Client && !NoNewerThanGateway(update.Version))
        {
            return;
        }
        if (at > _time.GetUtcNow())
        {
            _autoTimer ??= _time.CreateTimer(
                _ => Post(ConsiderAutoInstall),
                null,
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(10)
            );
            return;
        }
        _ = InstallAutomaticallyAsync(update, status.LastChecked);
    }

    private bool NoNewerThanGateway(string version) =>
        SemVersion.TryParse(GatewayVersion, out var gateway)
        && SemVersion.TryParse(version, out var offered)
        && offered <= gateway;

    private async Task InstallAutomaticallyAsync(UpdateInfo update, DateTimeOffset? check)
    {
        await InstallAsync(update);
        if (Install == InstallState.Failed)
        {
            _autoFailedCheck = check; // tried again at the next check
        }
    }

    /// <summary>Restart automatically (F4.15 behavior 11b): once a download is ready, at the chosen time.</summary>
    private async Task ScheduleRestartAsync()
    {
        AppSettings settings;
        try
        {
            settings = await engine.GetSettingsAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            return;
        }
        if (!settings.EffectiveUpdates.AutoRestart || !IsReady)
        {
            return;
        }
        var at = NextLocal(settings.EffectiveUpdates.EffectiveRestartTime);
        RestartAt = at;
        _restartTimer?.Dispose();
        _restartTimer = _time.CreateTimer(
            _ => Post(RestartNow),
            null,
            at - _time.GetUtcNow(),
            Timeout.InfiniteTimeSpan
        );
    }

    /// <summary>The next moment, from now, when the computer's clock says <paramref name="time"/>.</summary>
    private DateTimeOffset NextLocal(TimeOnly time)
    {
        var zone = _time.LocalTimeZone;
        var now = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone);
        var day = now.TimeOfDay < time.ToTimeSpan() ? now.Date : now.Date.AddDays(1);
        var local = DateTime.SpecifyKind(day + time.ToTimeSpan(), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    /// <summary>What's new after an update (F4.15 behavior 11c), while it's open.</summary>
    [ObservableProperty]
    public partial WhatsNewViewModel? WhatsNew { get; private set; }

    private void ShowWhatsNew()
    {
        if (!_whatsNewShown && Status?.WhatsNew is { Count: > 0 } news)
        {
            _whatsNewShown = true;
            WhatsNew = new WhatsNewViewModel(news, text, this);
        }
    }

    /// <summary>OK, or closing the dialog: What's new isn't shown again for these versions.</summary>
    internal async Task DismissWhatsNewAsync()
    {
        WhatsNew = null;
        try
        {
            await engine.DismissWhatsNewAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            // Shown again at the next start; nothing else to do.
        }
    }

    /// <summary>Opens a release's page, from What's new.</summary>
    internal Task OpenReleaseAsync(UpdateInfo release) => openUri(release.ReleasePage);

    /// <summary>How many lines of the notes the dialog shows before Release notes (behavior 9.1).</summary>
    public const int NotesLines = 40;

    private string? _later;
    private DateTimeOffset? _clearedFailure; // the failed check whose warning was cleared (behavior 7)
    private Action? _cancel;

    /// <summary>Where the check stands; null before the first answer, or for a gateway viewer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeText), nameof(IsWarning), nameof(NoticeTip), nameof(CanClear))]
    public partial UpdateStatus? Status { get; private set; }

    /// <summary>
    /// Whether the notice is the warning that the last check failed (behavior 7): shown only with no update to offer,
    /// until it's cleared or a check works.
    /// </summary>
    public bool IsWarning =>
        Install == InstallState.None
        && OfferedUpdate is null
        && Status is { LastError: not null, LastChecked: var at }
        && at != _clearedFailure;

    /// <summary>The update the notice offers: found, not put off with Later; null otherwise.</summary>
    private UpdateInfo? OfferedUpdate => Status?.Update is { } update && update.Version != _later ? update : null;

    /// <summary>The notice's tooltip: why the check failed, and that clicking tries again; null otherwise.</summary>
    public string? NoticeTip =>
        IsWarning && Status?.LastError is { } error
            ? text.Format("Updates.CheckFailedTip", new Dictionary<string, object?> { ["reason"] = text.Format(error) })
            : null;

    /// <summary>Whether the notice can be cleared (×): an update offered, or a failed check; not while installing.</summary>
    public bool CanClear =>
        FinishedOnService is not null
        || Status?.Install is not { Running: true }
            && Install is InstallState.None or InstallState.Failed
            && (OfferedUpdate is not null || IsWarning);

    // The service update's result cleared with × (F1.40 behavior 5), so it isn't shown again.
    private ServiceInstall? _clearedInstall;

    /// <summary>The service gateway's finished update, done or failed, while it's shown.</summary>
    private ServiceInstall? FinishedOnService =>
        Gateway && Status?.Install is { Running: false } install && install != _clearedInstall ? install : null;

    /// <summary>
    /// Clears the notice (×): an update until the next start, as Later does (behavior 9.4); a failed check's warning
    /// until a later check fails again.
    /// </summary>
    [RelayCommand]
    private void Clear()
    {
        if (FinishedOnService is { } finished)
        {
            _clearedInstall = finished;
        }
        else if (IsWarning)
        {
            _clearedFailure = Status?.LastChecked;
        }
        else if (OfferedUpdate is { } update)
        {
            _later = update.Version;
        }
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(NoticeTip));
        OnPropertyChanged(nameof(CanClear));
    }

    /// <summary>Whether this is a gateway's browser: the dialog says how to pull the image instead (behavior 16).</summary>
    public bool Gateway { get; init; }

    /// <summary>
    /// The status bar's notice (behaviors 8, 11): "Update available: 0.5.0-alpha.1", the download's progress, or that
    /// it's ready; null with none, or after Later.
    /// </summary>
    public string? NoticeText =>
        ServiceNotice()
        ?? Install switch
        {
            InstallState.Downloading => text.Format(
                "Updates.Downloading",
                new Dictionary<string, object?>
                {
                    ["version"] = _installing?.Version,
                    ["percent"] = Progress.ToString("P0", text.Culture),
                }
            ),
            InstallState.Ready when RestartAt is { } restart => text.Format(
                "Updates.ReadyRestartNotice",
                new Dictionary<string, object?>
                {
                    ["version"] = _installing?.Version,
                    ["time"] = TimeZoneInfo.ConvertTime(restart, _time.LocalTimeZone).ToString("t", text.Culture),
                }
            ),
            InstallState.Ready => text.Format(
                "Updates.ReadyNotice",
                new Dictionary<string, object?> { ["version"] = _installing?.Version }
            ),
            _ when OfferedUpdate is { } update => text.Format(
                "Updates.Notice",
                new Dictionary<string, object?> { ["version"] = update.Version }
            ),
            _ => IsWarning ? text["Updates.CheckFailedNotice"] : null,
        };

    /// <summary>
    /// The Linux service gateway's update (docs/features/F1.40-gateway-service-updates.md, behavior 5): its step while
    /// it runs, then Updated to, or why it failed; null with none.
    /// </summary>
    private string? ServiceNotice()
    {
        if (!Gateway || Status?.Install is not { } install || install == _clearedInstall)
        {
            return null;
        }
        var args = new Dictionary<string, object?>
        {
            ["version"] = install.Version,
            ["step"] = text[$"Updates.Service.{install.Step}"],
            ["reason"] = install.Reason ?? string.Empty,
            ["running"] = Status.Running,
        };
        return install.Step switch
        {
            ServiceInstallStep.Done => text.Format("Updates.Service.DoneNotice", args),
            ServiceInstallStep.Failed => text.Format("Updates.Service.FailedNotice", args),
            _ => text.Format("Updates.Service.Notice", args),
        };
    }

    /// <summary>
    /// Whether this build installs updates itself, rather than offering Download (behavior 11): the installed desktop,
    /// or the Linux service gateway for its operator (F1.40 behavior 4), when no update is under way.
    /// </summary>
    public bool CanInstall =>
        Gateway ? Status is { CanInstall: true, Install: not { Running: true } } : installer?.CanInstall == true;

    /// <summary>Where installing the update stands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(NoticeText),
        nameof(IsDownloading),
        nameof(IsReady),
        nameof(Failed),
        nameof(IsWarning),
        nameof(CanClear)
    )]
    public partial InstallState Install { get; private set; }

    /// <summary>How much is downloaded, from 0 to 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeText))]
    public partial double Progress { get; private set; }

    /// <summary>Whether the update is downloading, so the dialog shows its progress and Cancel.</summary>
    public bool IsDownloading => Install == InstallState.Downloading;

    /// <summary>Whether it's downloaded, so the dialog offers Restart now.</summary>
    public bool IsReady => Install == InstallState.Ready;

    /// <summary>Whether the last download failed, so the dialog says so and offers it again.</summary>
    public bool Failed => Install == InstallState.Failed;

    private UpdateInfo? _installing;

    /// <summary>The dialog while it's open.</summary>
    [ObservableProperty]
    public partial UpdateDialogViewModel? Dialog { get; private set; }

    /// <summary>
    /// Follows the engine's update status until cancelled. True when the engine refused it (behavior 15): not this
    /// viewer's, such as an operator whose sign-in ended, so the caller stops asking rather than asking again.
    /// </summary>
    internal async Task<bool> WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var status in engine.WatchUpdateStatusAsync(cancellationToken))
            {
                Status = status;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Status = null; // not this viewer's to know (behavior 15)
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return false; // the connection dropped: asked again once it's back
        }
    }

    /// <summary>Opens the update's dialog (behavior 9); on the failed-check warning, tries again (behavior 7).</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        if (IsWarning)
        {
            await CheckAsync();
            return;
        }
        Open();
    }

    private void Open()
    {
        if ((Install is InstallState.None or InstallState.Failed ? Status?.Update : _installing) is { } update)
        {
            Dialog = new UpdateDialogViewModel(update, text, Gateway, this);
        }
    }

    /// <summary>Check now (behavior 10): opens the dialog when it finds one.</summary>
    [RelayCommand]
    internal async Task CheckAsync()
    {
        try
        {
            Status = await engine.CheckForUpdateAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            return;
        }
        Open();
    }

    /// <summary>Checks again without opening the dialog: after a setting changed what counts (Settings › Data).</summary>
    internal async Task RefreshAsync()
    {
        try
        {
            Status = await engine.CheckForUpdateAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            // Not this viewer's, or the engine went away: the status poll carries on.
        }
    }

    /// <summary>Skip this version: hidden until a newer one comes out (behavior 9.3).</summary>
    internal async Task SkipAsync(string version)
    {
        Dialog = null;
        try
        {
            await engine.SkipUpdateAsync(version);
            Status = Status is { } status ? status with { Update = null } : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            // The notice stays; skipping can be tried again.
        }
    }

    /// <summary>Later: hidden until the next start (behavior 9.4).</summary>
    internal void Later(string version)
    {
        Dialog = null;
        _later = version;
        Notify();
    }

    /// <summary>Download: the release page in the browser (behavior 9.2).</summary>
    internal Task DownloadAsync(UpdateInfo update)
    {
        Dialog = null;
        return openUri(update.ReleasePage);
    }

    /// <summary>
    /// Update and restart (behavior 11): downloads in the background with progress in the notice; once it's ready the
    /// update goes in when the app closes, unless Restart now comes first.
    /// </summary>
    internal async Task InstallAsync(UpdateInfo update)
    {
        if (Gateway)
        {
            await InstallOnServiceAsync(update);
            return;
        }
        if (installer is null || IsDownloading)
        {
            return;
        }
        _installing = update;
        using var download = new CancellationTokenSource();
        _cancel = download.Cancel;
        Progress = 0;
        Install = InstallState.Downloading;
        var context = SynchronizationContext.Current;
        var progress = new Reporter(p =>
        {
            if (context is null)
            {
                Progress = p;
            }
            else
            {
                context.Post(_ => Progress = p, null);
            }
        });
        try
        {
            await installer.DownloadAsync(update, progress, download.Token);
            installer.ApplyOnExit();
            Install = InstallState.Ready;
            await ScheduleRestartAsync(); // F4.15 behavior 11b
        }
        catch (OperationCanceledException)
        {
            Install = InstallState.None;
        }
#pragma warning disable CA1031 // any failure leaves Download to fall back on
        catch (Exception)
#pragma warning restore CA1031
        {
            Install = InstallState.Failed;
        }
        finally
        {
            _cancel = null;
        }
    }

    /// <summary>
    /// Update and restart on the Linux service gateway (F1.40 behavior 4): its updater does the work; the notice follows
    /// the update status the gateway sends.
    /// </summary>
    private async Task InstallOnServiceAsync(UpdateInfo update)
    {
        try
        {
            await engine.InstallUpdateAsync(update.Version);
            Status = await engine.GetUpdateStatusAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Status = Status is null
                ? null
                : Status with
                {
                    Install = new ServiceInstall(update.Version, ServiceInstallStep.Failed, text.Describe(ex)),
                };
        }
    }

    /// <summary>Cancel: stops the download, and the notice goes back to the update.</summary>
    internal void CancelInstall() => _cancel?.Invoke();

    /// <summary>Restart now: closes as usual and reopens on the update (behavior 11.4).</summary>
    internal void RestartNow()
    {
        if (IsReady)
        {
            Dialog = null;
            installer?.RestartNow();
        }
    }

    private sealed class Reporter(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(Math.Clamp(value, 0, 1));
    }

    internal void Close() => Dialog = null;

    /// <summary>"Pre-releases · checked 14:02 · up to date" for About (behavior 10).</summary>
    internal string? Summary(TimeZoneInfo zone)
    {
        if (Status is not { } status)
        {
            return null;
        }
        var channel = text[$"Updates.Channel.{status.Channel}"];
        if (status.CheckDisabled)
        {
            return text.Format("Updates.Off", new Dictionary<string, object?> { ["channel"] = channel });
        }
        var when = status.LastChecked is { } at
            ? TimeZoneInfo.ConvertTime(at, zone).ToString("g", text.Culture)
            : text["Updates.Never"];
        var result =
            status.LastError is { } error
                ? text.Format("Updates.Failed", new Dictionary<string, object?> { ["reason"] = text.Format(error) })
            : status.Update is { } update
                ? text.Format("Updates.Notice", new Dictionary<string, object?> { ["version"] = update.Version })
            : text["Updates.UpToDate"];
        return text.Format(
            "Updates.Summary",
            new Dictionary<string, object?>
            {
                ["channel"] = channel,
                ["when"] = when,
                ["result"] = result,
            }
        );
    }
}

/// <summary>An update's dialog (behavior 9; on a gateway behavior 16).</summary>
public sealed partial class UpdateDialogViewModel(
    UpdateInfo update,
    Localizer text,
    bool gateway,
    UpdatesViewModel owner
) : ObservableObject
{
    /// <summary>"0.5.0-alpha.1 · 20 October 2026".</summary>
    public string Heading { get; } =
        $"{update.Version} · {update.Published.ToString("d", text.Culture ?? CultureInfo.InvariantCulture)}";

    /// <summary>The notes' first lines, as text.</summary>
    public string Notes { get; } =
        string.Join(
            '\n',
            update.Notes.Replace("\r", "", StringComparison.Ordinal).Split('\n').Take(UpdatesViewModel.NotesLines)
        );

    /// <summary>The notes shown, formatted: headings, lists, bold and links (behavior 9.1).</summary>
    public IReadOnlyList<NoteBlock> Blocks => _blocks ??= ReleaseNotes.Parse(Notes, update.Version);

    private IReadOnlyList<NoteBlock>? _blocks;

    /// <summary>Whether the notes run on past what's shown, so Release notes is worth a look.</summary>
    public bool MoreNotes { get; } = update.Notes.Split('\n').Length > UpdatesViewModel.NotesLines;

    /// <summary>Where the update stands, for the dialog's progress and buttons.</summary>
    public UpdatesViewModel Owner => owner;

    /// <summary>Update and restart, in an installed desktop build (behavior 11).</summary>
    public bool CanInstall { get; } = owner.CanInstall;

    /// <summary>Download, for an archive build or when installing failed; on a gateway the image instead.</summary>
    public bool CanDownload { get; } = !gateway && (!owner.CanInstall || owner.Failed);

    /// <summary>On a gateway: "docker compose pull" and the tag (behavior 16); null on the desktop.</summary>
    public string? PullText { get; } =
        gateway && update.ImageTag is { } tag
            ? text.Format("Updates.Pull", new Dictionary<string, object?> { ["image"] = tag })
            : null;

    /// <summary>Opens the release page.</summary>
    [RelayCommand]
    private Task DownloadAsync() => owner.DownloadAsync(update);

    /// <summary>Update and restart: closes the dialog, with the progress in the notice.</summary>
    [RelayCommand]
    private Task InstallAsync()
    {
        owner.Close();
        return owner.InstallAsync(update);
    }

    /// <summary>Stops the download.</summary>
    [RelayCommand]
    private void Cancel() => owner.CancelInstall();

    /// <summary>Restarts on the downloaded update.</summary>
    [RelayCommand]
    private void RestartNow() => owner.RestartNow();

    /// <summary>The full notes, on the release page.</summary>
    [RelayCommand]
    private Task ReleaseNotesAsync() => owner.DownloadAsync(update);

    /// <summary>Skip this version.</summary>
    [RelayCommand]
    private Task SkipAsync() => owner.SkipAsync(update.Version);

    /// <summary>Later: until the next start.</summary>
    [RelayCommand]
    private void Later() => owner.Later(update.Version);

    /// <summary>Closes the dialog.</summary>
    [RelayCommand]
    private void Close() => owner.Close();
}

/// <summary>
/// What's new (F4.15 behavior 11c): each version since the last one that ran, newest first, with its notes; OK
/// dismisses it for good.
/// </summary>
public sealed partial class WhatsNewViewModel(
    IReadOnlyList<UpdateInfo> releases,
    Localizer text,
    UpdatesViewModel owner
) : ObservableObject
{
    /// <summary>"Updated to 0.9.0".</summary>
    public string Heading { get; } =
        text.Format("Updates.WhatsNew.Heading", new Dictionary<string, object?> { ["version"] = releases[0].Version });

    /// <summary>Each version's heading and notes.</summary>
    public IReadOnlyList<WhatsNewRelease> Releases { get; } =
    [.. releases.Select(r => new WhatsNewRelease(r, text, owner))];

    /// <summary>OK: read.</summary>
    [RelayCommand]
    private Task OkAsync() => owner.DismissWhatsNewAsync();
}

/// <summary>One version in What's new.</summary>
public sealed partial class WhatsNewRelease(UpdateInfo release, Localizer text, UpdatesViewModel owner)
    : ObservableObject
{
    /// <summary>"0.9.0 · 20 October 2026".</summary>
    public string Heading { get; } =
        $"{release.Version} · {release.Published.ToString("d", text.Culture ?? CultureInfo.InvariantCulture)}";

    /// <summary>Its notes, formatted, the first lines only.</summary>
    public IReadOnlyList<NoteBlock> Blocks { get; } =
        ReleaseNotes.Parse(
            string.Join(
                '\n',
                release.Notes.Replace("\r", "", StringComparison.Ordinal).Split('\n').Take(UpdatesViewModel.NotesLines)
            ),
            release.Version
        );

    /// <summary>The full notes, on the release page.</summary>
    [RelayCommand]
    private Task ReleaseNotesAsync() => owner.OpenReleaseAsync(release);
}

/// <summary>Where installing an update stands (behavior 11).</summary>
public enum InstallState
{
    /// <summary>Not started, or cancelled.</summary>
    None,

    /// <summary>Downloading, with progress.</summary>
    Downloading,

    /// <summary>Downloaded: it goes in when the app closes, or on Restart now.</summary>
    Ready,

    /// <summary>The download or its check failed; it can be tried again, or Download used.</summary>
    Failed,
}
