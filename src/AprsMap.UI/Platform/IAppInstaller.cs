// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.UI.Platform;

/// <summary>
/// Installs an update the engine found (docs/features/F4.15-updates.md, behavior 11): the desktop's installer, absent
/// in the browser and in archive builds, which only offer Download.
/// </summary>
public interface IAppInstaller
{
    /// <summary>Whether this build was installed (rather than unpacked from an archive), so it can update itself.</summary>
    bool CanInstall { get; }

    /// <summary>
    /// Downloads <paramref name="update"/>, a delta from the running version where there is one, and checks it against
    /// the release's feed. Reports progress from 0 to 1; throws when it can't.
    /// </summary>
    Task DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Puts the downloaded update in place the next time the app closes (behavior 11.3).</summary>
    void ApplyOnExit();

    /// <summary>Closes the app as usual, saving the layout and preferences, then reopens it on the update.</summary>
    void RestartNow();
}
