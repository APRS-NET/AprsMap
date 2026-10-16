// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices.JavaScript;

namespace AprsMap.Browser;

/// <summary>The loading page in index.html, which the app replaces once it starts (functions in main.js).</summary>
internal static partial class StartupPage
{
    /// <summary>
    /// Replaces "Loading…" with a message and a button that reloads the page; with <paramref name="retryAfterSeconds"/>
    /// above 0 the page also reloads by itself after that long.
    /// </summary>
    [JSImport("showStartupError", "main.js")]
    internal static partial void ShowError(string title, string detail, string retry, int retryAfterSeconds = 0);

    /// <summary>Tells the page the app is running, so it stops its "still loading" timer.</summary>
    /// <summary>The screen's size in device pixels (width × height × the device scale squared).</summary>
    [JSImport("screenPixels", "main.js")]
    internal static partial double ScreenPixels();

    [JSImport("appStarted", "main.js")]
    internal static partial void AppStarted();
}
