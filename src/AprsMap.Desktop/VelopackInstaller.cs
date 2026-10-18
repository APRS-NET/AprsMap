// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Platform;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;
using UpdateInfo = AprsMap.Contracts.UpdateInfo;

namespace AprsMap.Desktop;

/// <summary>
/// Installs updates in an installed build with Velopack (docs/features/F4.15-updates.md, behavior 11). It installs the
/// release the engine found and nothing else: the feed it reads is that release's own, attached to it on GitHub.
/// Velopack checks each package's hash against the feed, and on Windows asks for administrator approval to update a
/// Program Files install.
/// </summary>
internal sealed class VelopackInstaller : IAppInstaller
{
    private UpdateManager? _manager;
    private VelopackAsset? _downloaded;
    private bool _applyOnExit;
    private bool _restart;

    /// <summary>Whether this build was installed by Velopack, rather than unpacked from an archive.</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                return new UpdateManager(new SimpleWebSource("https://localhost/")).IsInstalled;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public bool CanInstall => true;

    public async Task DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(progress);
        // releases.<channel>.json, attached to the release (behavior 4.2); its folder holds the packages it lists.
        var feed =
            update.Files.FirstOrDefault(f =>
                f.Name.StartsWith("releases.", StringComparison.Ordinal)
                && f.Name.EndsWith(".json", StringComparison.Ordinal)
            ) ?? throw new InvalidOperationException("The release has no installer feed for this platform.");
        var channel = feed.Name["releases.".Length..^".json".Length];
        var manager = new UpdateManager(
            new SimpleWebSource(new Uri(feed.Url, ".")),
            new UpdateOptions { ExplicitChannel = channel }
        );
        var found =
            await manager.CheckForUpdatesAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The feed has nothing newer than this version.");
        if (found.TargetFullRelease.Version.ToString() != update.Version)
        {
            throw new InvalidOperationException(
                $"The feed offers {found.TargetFullRelease.Version}, not {update.Version}."
            );
        }
        await manager
            .DownloadUpdatesAsync(found, percent => progress.Report(percent / 100.0), cancellationToken)
            .ConfigureAwait(false);
        _manager = manager;
        _downloaded = found.TargetFullRelease;
    }

    public void ApplyOnExit() => _applyOnExit = true;

    public void RestartNow()
    {
        _restart = true;
        // The usual close, which saves the layout and preferences; Finish then hands over to the updater.
        Dispatcher.UIThread.Post(() =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()
        );
    }

    /// <summary>
    /// Called as the app exits: starts Velopack's updater, which waits for this process to end, puts the update in
    /// place and, after Restart now, opens it again.
    /// </summary>
    public void Finish()
    {
        if (_manager is not null && _downloaded is not null && (_applyOnExit || _restart))
        {
            _manager.WaitExitThenApplyUpdates(_downloaded, silent: !_restart, restart: _restart);
        }
    }
}
