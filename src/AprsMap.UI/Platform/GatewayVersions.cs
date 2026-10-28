// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;

namespace AprsMap.UI.Platform;

/// <summary>
/// The gateway's version as an app connected to it shows it (#453): "Gateway 0.8.0-alpha.2 (Linux service)", and
/// whether its major.minor differs from the app's, in #419's words.
/// </summary>
internal static class GatewayVersions
{
    /// <summary>"0.8" of "0.8.0-alpha.2+e86ba03".</summary>
    public static string MajorMinor(string version) =>
        string.Join('.', version.Split('+')[0].Split('-')[0].Split('.').Take(2));

    /// <summary>Whether <paramref name="gateway"/> and <paramref name="app"/> differ in major.minor.</summary>
    public static bool Differ(string gateway, string app) => MajorMinor(gateway) != MajorMinor(app);

    /// <summary>"Linux service", "container", or null where it's the desktop or not said.</summary>
    public static string? InstallText(Localizer text, EngineInstall? install) =>
        install switch
        {
            EngineInstall.Service => text["Gateway.Install.Service"],
            EngineInstall.Container => text["Gateway.Install.Container"],
            EngineInstall.Other => text["Gateway.Install.Other"],
            _ => null,
        };

    /// <summary>"Its version differs from this app's (0.7.3)…", or null when they match.</summary>
    public static string? DifferText(Localizer text, string gateway, string app) =>
        Differ(gateway, app)
            ? text.Format(
                "Connection.Test.VersionsDiffer",
                new Dictionary<string, object?> { ["app"] = app.Split('+')[0] }
            )
            : null;
}
