// SPDX-License-Identifier: AGPL-3.0-only
namespace AprsMap.UI.Platform;

/// <summary>What viewers of the desktop's web viewer are shown of the station's own location (F1.35 behavior 5).</summary>
public enum ViewerLocation
{
    /// <summary>Where it is.</summary>
    Exact,

    /// <summary>Only its grid square, the house at the square's centre.</summary>
    Grid,

    /// <summary>Not shown.</summary>
    Hidden,
}

/// <summary>The desktop's web viewer settings (docs/features/F1.35-desktop-web-viewer.md, behaviors 2–5).</summary>
/// <param name="On">Serving browsers; off by default.</param>
/// <param name="Port">The TCP port; 8090 by default, beside a gateway's usual 8080.</param>
/// <param name="LocalNetwork">Other devices on the network may connect; otherwise this computer only.</param>
/// <param name="Location">What viewers are shown of the station's location.</param>
public sealed record WebViewerSettings(
    bool On = false,
    int Port = WebViewerSettings.DefaultPort,
    bool LocalNetwork = false,
    ViewerLocation Location = ViewerLocation.Exact
)
{
    /// <summary>The port it offers first.</summary>
    public const int DefaultPort = 8090;
}

/// <summary>Why the web viewer isn't serving although it's on.</summary>
public enum WebViewerProblem
{
    /// <summary>Nothing wrong.</summary>
    None,

    /// <summary>Another program has the port.</summary>
    PortInUse,

    /// <summary>The system wouldn't let it listen (permissions, or no such network).</summary>
    CantListen,
}

/// <summary>Where the web viewer stands.</summary>
/// <param name="Running">Serving now.</param>
/// <param name="Addresses">The addresses to open: this computer's, then each network address with the local network.</param>
/// <param name="Viewers">How many browsers are connected.</param>
/// <param name="Problem">Why it isn't serving although it's on.</param>
/// <param name="Detail">The system's words for the problem, for the log and the tooltip.</param>
public sealed record WebViewerState(
    bool Running,
    IReadOnlyList<Uri> Addresses,
    int Viewers,
    WebViewerProblem Problem = WebViewerProblem.None,
    string? Detail = null
)
{
    /// <summary>Off, nothing to show.</summary>
    public static WebViewerState Off { get; } = new(false, [], 0);
}

/// <summary>
/// The desktop app's web viewer (docs/features/F1.35-desktop-web-viewer.md): its own station served to browsers, view
/// only. Supplied by the desktop app; the browser has none.
/// </summary>
public interface IWebViewerHost
{
    /// <summary>Whether this build carries the browser app; without it the viewer can't be turned on (behavior 1).</summary>
    bool Available { get; }

    /// <summary>The settings in force.</summary>
    WebViewerSettings Settings { get; }

    /// <summary>Where it stands now; <see cref="WebViewerState.Viewers"/> is read fresh each time.</summary>
    WebViewerState State { get; }

    /// <summary>Raised when it starts, stops or fails; on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Saves <paramref name="settings"/> and starts, restarts or stops the viewer to match.</summary>
    Task ApplyAsync(WebViewerSettings settings, CancellationToken cancellationToken = default);
}
