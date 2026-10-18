// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Updates;

/// <summary>
/// Updates in the app (docs/features/F4.15-updates.md, behaviors 8–10, 15, 16): the engine's update status followed, a
/// quiet notice in the status bar when there's an update, its dialog, and Check now. On a gateway only the operator
/// follows it; a viewer's refused call leaves nothing to show.
/// </summary>
public sealed partial class UpdatesViewModel(IEngineService engine, Localizer text, Func<Uri, Task> openUri)
    : ObservableObject
{
    /// <summary>How many lines of the notes the dialog shows before Release notes (behavior 9.1).</summary>
    public const int NotesLines = 40;

    private string? _later;

    /// <summary>Where the check stands; null before the first answer, or for a gateway viewer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeText))]
    public partial UpdateStatus? Status { get; private set; }

    /// <summary>Whether this is a gateway's browser: the dialog says how to pull the image instead (behavior 16).</summary>
    public bool Gateway { get; init; }

    /// <summary>"Update available: 0.5.0-alpha.1" in the status bar (behavior 8); null with none, or after Later.</summary>
    public string? NoticeText =>
        Status?.Update is { } update && update.Version != _later
            ? text.Format("Updates.Notice", new Dictionary<string, object?> { ["version"] = update.Version })
            : null;

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

    /// <summary>Opens the update's dialog (behavior 9).</summary>
    [RelayCommand]
    private void Open()
    {
        if (Status?.Update is { } update)
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
        OnPropertyChanged(nameof(NoticeText));
    }

    /// <summary>Download: the release page in the browser (behavior 9.2).</summary>
    internal Task DownloadAsync(UpdateInfo update)
    {
        Dialog = null;
        return openUri(update.ReleasePage);
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

    /// <summary>Whether the notes run on past what's shown, so Release notes is worth a look.</summary>
    public bool MoreNotes { get; } = update.Notes.Split('\n').Length > UpdatesViewModel.NotesLines;

    /// <summary>Download on the desktop; on a gateway, the image and how to pull it instead.</summary>
    public bool CanDownload { get; } = !gateway;

    /// <summary>On a gateway: "docker compose pull" and the tag (behavior 16); null on the desktop.</summary>
    public string? PullText { get; } =
        gateway && update.ImageTag is { } tag
            ? text.Format("Updates.Pull", new Dictionary<string, object?> { ["image"] = tag })
            : null;

    /// <summary>Opens the release page.</summary>
    [RelayCommand]
    private Task DownloadAsync() => owner.DownloadAsync(update);

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
