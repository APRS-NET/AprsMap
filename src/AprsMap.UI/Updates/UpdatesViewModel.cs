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
    IAppInstaller? installer = null
) : ObservableObject
{
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
        Install is InstallState.None or InstallState.Failed && (OfferedUpdate is not null || IsWarning);

    /// <summary>
    /// Clears the notice (×): an update until the next start, as Later does (behavior 9.4); a failed check's warning
    /// until a later check fails again.
    /// </summary>
    [RelayCommand]
    private void Clear()
    {
        if (IsWarning)
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
        Install switch
        {
            InstallState.Downloading => text.Format(
                "Updates.Downloading",
                new Dictionary<string, object?>
                {
                    ["version"] = _installing?.Version,
                    ["percent"] = Progress.ToString("P0", text.Culture),
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

    /// <summary>Whether this build installs updates itself, rather than offering Download (behavior 11).</summary>
    public bool CanInstall => installer?.CanInstall == true && !Gateway;

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

    /// <summary>Follows the engine's update status until cancelled; a refusal (a gateway viewer) ends it quietly.</summary>
    internal async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var status in engine.WatchUpdateStatusAsync(cancellationToken))
            {
                Status = status;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            Status = null; // not this viewer's to know (behavior 15)
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
