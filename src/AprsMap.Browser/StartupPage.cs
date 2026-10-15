// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices.JavaScript;

namespace AprsMap.Browser;

/// <summary>The loading page in index.html, which the app replaces once it starts (functions in main.js).</summary>
internal static partial class StartupPage
{
    /// <summary>Replaces "Loading…" with a message and a button that reloads the page.</summary>
    [JSImport("showStartupError", "main.js")]
    internal static partial void ShowError(string title, string detail, string retry);

    /// <summary>Tells the page the app is running, so it stops its "still loading" timer.</summary>
    [JSImport("appStarted", "main.js")]
    internal static partial void AppStarted();
}
