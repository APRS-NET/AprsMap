// SPDX-License-Identifier: AGPL-3.0-only
namespace AprsMap.UI.Appearance;

/// <summary>Light, dark, or follow the operating system / browser.</summary>
public enum ThemeMode
{
    /// <summary>Follow the OS or browser setting, switching live.</summary>
    Auto,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>Colour palette. Each palette supplies both light and dark variants.</summary>
public enum Palette
{
    /// <summary>The default quiet palette.</summary>
    Standard,

    /// <summary>AAA-contrast text, heavier borders and focus rings.</summary>
    HighContrast,

    /// <summary>States told apart by lightness and blue/orange, never red/green alone.</summary>
    ColourVisionSafe,
}

/// <summary>Corner style for controls and panels.</summary>
public enum CornerStyle
{
    /// <summary>8 px controls, 12 px panels.</summary>
    Rounded,

    /// <summary>2 px controls, square panels.</summary>
    Sharp,
}

/// <summary>How tightly lists and panels are laid out (F0.6 behavior 11).</summary>
public enum Density
{
    /// <summary>The default spacing.</summary>
    Comfortable,

    /// <summary>Tighter rows and paddings; touch targets stay at least 44 px on touch screens.</summary>
    Compact,
}

/// <summary>Whether transitions run (F0.6 behavior 12).</summary>
public enum MotionPreference
{
    /// <summary>Follow the browser's <c>prefers-reduced-motion</c>; on desktop, normal motion.</summary>
    FollowSystem,

    /// <summary>No transitions.</summary>
    Reduced,
}

/// <summary>How bright the map's tiles are (F1.4 behavior 13).</summary>
public enum MapBrightness
{
    /// <summary>Normal in a light theme, dimmed in a dark one (F1.4 behavior 13).</summary>
    Auto,

    /// <summary>Tiles as the provider draws them.</summary>
    Normal,

    /// <summary>Tiles darkened a little, so stations stand out.</summary>
    Dimmed,

    /// <summary>Tiles darkened a lot.</summary>
    Dark,
}

/// <summary>Which station labels the map draws (F1.4 behavior 17).</summary>
public enum MapLabels
{
    /// <summary>Every callsign that fits without overlapping; the most recently heard wins.</summary>
    AllThatFit,

    /// <summary>Symbols only.</summary>
    None,
}

/// <summary>Icon size in the UI chrome (map symbols have their own scale).</summary>
public enum IconSize
{
    /// <summary>16 px base.</summary>
    Small,

    /// <summary>20 px base.</summary>
    Medium,

    /// <summary>24 px base.</summary>
    Large,
}

/// <summary>
/// One viewer's appearance choices. Per viewer, not per station: it lives in the UI layer and never crosses
/// <c>IEngineService</c> (docs/ux/README.md#appearance-and-accessibility).
/// </summary>
public sealed record AppearanceSettings
{
    /// <summary>The smallest allowed text scale.</summary>
    public const double MinTextScale = 0.85;

    /// <summary>The largest allowed text scale.</summary>
    public const double MaxTextScale = 2.0;

    /// <summary>Light, dark or automatic.</summary>
    public ThemeMode Theme { get; init; } = ThemeMode.Auto;

    /// <summary>
    /// Colour palette, or null until the user picks one: then the OS contrast preference decides (High contrast when
    /// it asks for more contrast, Standard otherwise; F0.6 behavior 7).
    /// </summary>
    public Palette? Palette { get; init; }

    /// <summary>Text scale, 0.85 to 2.0; Settings offers <see cref="TextScaleSteps"/>.</summary>
    public double TextScale { get; init; } = 1.0;

    /// <summary>The text sizes Settings offers (F0.6 behavior 8).</summary>
    public static IReadOnlyList<double> TextScaleSteps { get; } = [0.85, 1.0, 1.15, 1.3, 1.5, 1.75, 2.0];

    /// <summary>UI icon size.</summary>
    public IconSize Icons { get; init; } = IconSize.Medium;

    /// <summary>Whether the icon size also scales with <see cref="TextScale"/> (behavior 9).</summary>
    public bool IconsFollowText { get; init; } = true;

    /// <summary>List and panel spacing.</summary>
    public Density Density { get; init; } = Density.Comfortable;

    /// <summary>Transitions on or off.</summary>
    public MotionPreference Motion { get; init; } = MotionPreference.FollowSystem;

    /// <summary>The smallest allowed map symbol scale.</summary>
    public const double MinMapSymbolScale = 0.75;

    /// <summary>The largest allowed map symbol scale.</summary>
    public const double MaxMapSymbolScale = 2.0;

    /// <summary>Map symbol scale, 0.75 to 2.0, for symbols and their labels together (F1.4 behavior 16).</summary>
    public double MapSymbolScale { get; init; } = 1.0;

    /// <summary>Whether the map draws the APRS-IS filter's range parts as circles (F1.2 behavior 6).</summary>
    public bool ShowFilterCircles { get; init; }

    /// <summary>The map symbol sizes Settings › Map offers.</summary>
    public static IReadOnlyList<double> MapSymbolScaleSteps { get; } = [0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>How bright the map's tiles are (F1.4 behavior 13).</summary>
    public MapBrightness MapBrightness { get; init; } = MapBrightness.Auto;

    /// <summary>Which station labels the map draws (F1.4 behavior 17).</summary>
    public MapLabels MapLabels { get; init; } = MapLabels.AllThatFit;

    /// <summary>The tile provider's key, or null for the first one the engine offers (F1.4 behavior 11).</summary>
    public string? MapProvider { get; init; }

    /// <summary>Corner style.</summary>
    public CornerStyle Corners { get; init; } = CornerStyle.Rounded;

    /// <summary>
    /// The UI language as an IETF tag (for example <c>de</c>), or null to follow the OS or browser. Per viewer, like the
    /// rest of appearance: on a gateway each browser picks its own.
    /// </summary>
    public string? Language { get; init; }
}
