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
