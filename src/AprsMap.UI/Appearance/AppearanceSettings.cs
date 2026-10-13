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

    /// <summary>Colour palette.</summary>
    public Palette Palette { get; init; } = Palette.Standard;

    /// <summary>Text scale, 0.85 to 2.0.</summary>
    public double TextScale { get; init; } = 1.0;

    /// <summary>UI icon size.</summary>
    public IconSize Icons { get; init; } = IconSize.Medium;

    /// <summary>Corner style.</summary>
    public CornerStyle Corners { get; init; } = CornerStyle.Rounded;
}
