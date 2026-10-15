// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Appearance;

namespace AprsMap.UI.Platform;

/// <summary>What each app (desktop, browser) supplies to the shared UI besides the engine.</summary>
public sealed record PlatformServices
{
    /// <summary>The viewer's device location, where there is one (F0.5 behavior 9.4).</summary>
    public IDeviceLocation? DeviceLocation { get; init; }

    /// <summary>Where the viewer's own preferences are kept (F0.6 behavior 16); null keeps them for this session only.</summary>
    public IPreferenceStore? Preferences { get; init; }

    /// <summary>
    /// The viewer's appearance, loaded before the app starts so the first window already has the right theme and
    /// language (F0.6 behavior 19).
    /// </summary>
    public AppearanceSettings Appearance { get; init; } = new();

    /// <summary>Where map tiles come from (F1.4); without one the map shows its ground colour only.</summary>
    public ITileSource? Tiles { get; init; }

    /// <summary>
    /// The viewer's saved layout as stored (F1.0 behavior 8), read before the app starts so the main window opens where it
    /// was; null for the default layout (none saved, or <c>--reset-layout</c>).
    /// </summary>
    public string? SavedLayout { get; init; }

    /// <summary>
    /// Whether floating panels stay inside the app instead of opening OS windows (F1.0 behavior 5): the browser, which
    /// has no windows of its own.
    /// </summary>
    public bool ManagedWindows { get; init; }

    /// <summary>
    /// Whether the station's settings are a gateway's, shared with every viewer (the browser), so Reset the app (F0.10)
    /// deletes them only when asked.
    /// </summary>
    public bool SharedStation { get; init; }

    /// <summary>Starts the app afresh after Reset the app, where the platform can (the browser reloads the page).</summary>
    public Action? Restart { get; init; }

    /// <summary>Whether the platform asks for reduced motion (the browser's <c>prefers-reduced-motion</c>).</summary>
    public bool SystemReducedMotion { get; init; }

    /// <summary>
    /// Whether the primary pointer is coarse (a touch screen; the browser's <c>pointer: coarse</c>), so targets get a
    /// 44 px floor (F0.6 behavior 11). The desktop doesn't report it yet.
    /// </summary>
    public bool CoarsePointer { get; init; }

    /// <summary>Whether Settings offers the pseudo-language (F0.9 behavior 6): debug builds, or when configured.</summary>
    public bool ShowPseudoLanguage { get; init; }
}
