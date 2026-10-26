// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>Which releases count (docs/features/F4.15-updates.md, behavior 5).</summary>
public enum UpdateChannel
{
    /// <summary>Every release: alpha, beta, rc and stable.</summary>
    PreReleases,

    /// <summary>Versions without a pre-release part.</summary>
    Stable,
}

/// <summary>The update check's settings (behaviors 5, 6, 9.3), kept with the station's.</summary>
/// <param name="Check">Look for updates on start and every two hours (on by default, decision 2).</param>
/// <param name="Channel">Pre-releases or Stable; null for the running build's own (behavior 5).</param>
/// <param name="SkippedVersion">A version the user skipped: not offered again, only a newer one.</param>
/// <param name="LastChecked">When the last check ran, so a restart within a day doesn't check again. Set by the engine.</param>
public sealed record UpdateSettings(
    bool Check = true,
    UpdateChannel? Channel = null,
    string? SkippedVersion = null,
    DateTimeOffset? LastChecked = null
)
{
    /// <summary>Checking, on the build's own channel.</summary>
    public static UpdateSettings Default { get; } = new();
}

/// <summary>A file attached to a release.</summary>
/// <param name="Name">Its name, such as <c>aprsmap-v0.5.0-linux-x64.tar.gz</c>.</param>
/// <param name="Url">Where to download it.</param>
/// <param name="Size">Its size in bytes.</param>
public sealed record UpdateFile(string Name, Uri Url, long Size);

/// <summary>A newer release ready for this install (behaviors 3, 4).</summary>
/// <param name="Version">Its version, such as <c>0.5.0-alpha.1</c>.</param>
/// <param name="Published">When it was published.</param>
/// <param name="ReleasePage">Its page on GitHub, for Download and the full notes.</param>
/// <param name="Notes">Its release notes, as written (Markdown kept as text).</param>
/// <param name="ImageTag">For a gateway: the container image to pull (behavior 16); null otherwise.</param>
/// <param name="Files">The files attached for this install: its archive, or its installer packages and feed.</param>
public sealed record UpdateInfo(
    string Version,
    DateTimeOffset Published,
    Uri ReleasePage,
    string Notes,
    string? ImageTag,
    IReadOnlyList<UpdateFile> Files
);

/// <summary>Where the update check stands (behaviors 7, 10).</summary>
/// <param name="Running">The running version.</param>
/// <param name="Channel">The channel in force.</param>
/// <param name="LastChecked">When the last check ran; null before the first.</param>
/// <param name="LastError">Why the last check failed (<c>updates.*</c>); null when it worked.</param>
/// <param name="Update">The newest release ready for this install, not skipped; null for none.</param>
/// <param name="CheckDisabled">
/// Checking is off where the engine starts (<c>Engine:Updates:Check</c>, behavior 6): no request, and Check now hidden.
/// </param>
/// <param name="Checking">Whether a check is on its way now.</param>
/// <param name="CheckOff">
/// The user turned Check for updates off (behavior 6): no request on the schedule, nothing offered unless Check now.
/// </param>
/// <param name="CanInstall">
/// The engine installs updates itself, from here: the Linux service gateway (docs/features/F1.40-gateway-service-updates.md,
/// behavior 1). The installed desktop installs through its own installer instead.
/// </param>
/// <param name="Install">Where the service gateway's update stands (F1.40 behavior 5); null with none.</param>
public sealed record UpdateStatus(
    string Running,
    UpdateChannel Channel,
    DateTimeOffset? LastChecked = null,
    EngineError? LastError = null,
    UpdateInfo? Update = null,
    bool CheckDisabled = false,
    bool Checking = false,
    bool CheckOff = false,
    bool CanInstall = false,
    ServiceInstall? Install = null
);

/// <summary>A step of the service gateway's update (docs/features/F1.40-gateway-service-updates.md, behavior 4).</summary>
public enum ServiceInstallStep
{
    /// <summary>Asked for; waiting for the updater to pick it up.</summary>
    Requested,

    /// <summary>Downloading the release.</summary>
    Downloading,

    /// <summary>Checking the download against the release's checksum.</summary>
    Checking,

    /// <summary>Putting it in place.</summary>
    Installing,

    /// <summary>Restarting the service on it.</summary>
    Restarting,

    /// <summary>Running the new version.</summary>
    Done,

    /// <summary>Failed; the previous version is still running.</summary>
    Failed,
}

/// <summary>Where the service gateway's update stands (F1.40 behaviors 4, 5).</summary>
/// <param name="Version">The version being installed.</param>
/// <param name="Step">How far it has got.</param>
/// <param name="Reason">Why it failed, in the updater's words (not translated); null otherwise.</param>
public sealed record ServiceInstall(string Version, ServiceInstallStep Step, string? Reason = null)
{
    /// <summary>Whether it's still under way, so Update and Restart aren't offered (behavior 7).</summary>
    public bool Running => Step is not (ServiceInstallStep.Done or ServiceInstallStep.Failed);
}
