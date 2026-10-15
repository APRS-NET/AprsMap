// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using AprsMap.UI.Themes.Palettes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AprsMap.UI.Appearance;

/// <summary>
/// Applies <see cref="AppearanceSettings"/> (docs/features/F0.6-appearance.md) by swapping the palette dictionary,
/// rewriting the size, shape, density and motion tokens, and setting the theme variant, all at runtime. Views use
/// DynamicResource, so every change shows immediately without reloading anything.
/// </summary>
public sealed class AppearanceService
{
    private Application? _app;
    private IPlatformSettings? _platform;
    private Palette? _appliedPalette;
    private bool _systemReducedMotion;

    /// <summary>The settings currently applied.</summary>
    public AppearanceSettings Current { get; private set; } = new();

    /// <summary>The palette in use: the chosen one, or the one the OS contrast preference picks.</summary>
    public Palette EffectivePalette =>
        Current.Palette ?? (SystemPrefersHighContrast ? Palette.HighContrast : Palette.Standard);

    /// <summary>Whether transitions are off: chosen, or asked for by the browser (<c>prefers-reduced-motion</c>).</summary>
    public bool ReducedMotion => Current.Motion == MotionPreference.Reduced || _systemReducedMotion;

    /// <summary>Whether the OS asks for more contrast, as far as the platform says.</summary>
    public bool SystemPrefersHighContrast =>
        _platform?.GetColorValues().ContrastPreference == ColorContrastPreference.High;

    /// <summary>Raised after new settings are applied.</summary>
    public event EventHandler<AppearanceSettings>? Changed;

    /// <summary>Binds the service to an application, follows the OS contrast preference, and applies the settings.</summary>
    public void Attach(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
        _platform = app.PlatformSettings;
        if (_platform is not null)
        {
            _platform.ColorValuesChanged += (_, _) => Apply(Current);
        }
        Apply(Current);
    }

    /// <summary>
    /// Tells the service the platform's reduced-motion preference (the browser's <c>prefers-reduced-motion</c>; the
    /// desktop doesn't report one). Used while <see cref="AppearanceSettings.Motion"/> is Follow system.
    /// </summary>
    public void SetSystemReducedMotion(bool reduced)
    {
        _systemReducedMotion = reduced;
        Apply(Current);
    }

    /// <summary>Applies new settings (out-of-range scales are clamped).</summary>
    public void Apply(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings with
        {
            TextScale = Math.Clamp(
                settings.TextScale,
                AppearanceSettings.MinTextScale,
                AppearanceSettings.MaxTextScale
            ),
            MapSymbolScale = Math.Clamp(
                settings.MapSymbolScale,
                AppearanceSettings.MinMapSymbolScale,
                AppearanceSettings.MaxMapSymbolScale
            ),
        };

        if (_app is not null)
        {
            _app.RequestedThemeVariant = Current.Theme switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            ApplyPalette(_app, EffectivePalette);
            foreach (var (key, value) in Tokens(Current, ReducedMotion))
            {
                _app.Resources[key] = value;
            }
            if (_app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
            {
                fluent.DensityStyle = Current.Density == Density.Compact ? DensityStyle.Compact : DensityStyle.Normal;
            }
        }

        Localizer.Instance.SetLanguage(Current.Language);

        Changed?.Invoke(this, Current);
    }

    /// <summary>A palette's resources: compiled classes, so swapping them is safe in the trimmed browser build.</summary>
    internal static ResourceDictionary CreatePalette(Palette palette) =>
        palette switch
        {
            Palette.HighContrast => new HighContrastPalette(),
            Palette.ColourVisionSafe => new ColourVisionSafePalette(),
            _ => new StandardPalette(),
        };

    /// <summary>Swaps the palette dictionary (index 0 of the app's merged resources) and maps it onto Fluent's controls.</summary>
    private void ApplyPalette(Application app, Palette palette)
    {
        if (_appliedPalette == palette)
        {
            return;
        }
        var merged = app.Resources.MergedDictionaries;
        var include = CreatePalette(palette);
        if (merged.Count > 0)
        {
            merged[0] = include;
        }
        else
        {
            merged.Add(include);
        }
        _appliedPalette = palette;

        // Fluent's own controls (buttons, inputs, selection) take the palette's accent, text and ground, so a high
        // contrast or colour-vision-safe palette reaches them too.
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                if (
                    include.TryGetResource("Color.Accent", variant, out var accent)
                    && include.TryGetResource("Color.Text", variant, out var text)
                    && include.TryGetResource("Color.Ground", variant, out var ground)
                    && include.TryGetResource("Color.Warn", variant, out var warn)
                    && accent is Color a
                    && text is Color t
                    && ground is Color g
                    && warn is Color w
                )
                {
                    fluent.Palettes[variant] = new ColorPaletteResources
                    {
                        Accent = a,
                        BaseHigh = t,
                        RegionColor = g,
                        ErrorText = w,
                    };
                }
            }
        }
    }

    /// <summary>The size, shape, density and motion tokens for <paramref name="s"/>.</summary>
    internal static IEnumerable<(string Key, object Value)> Tokens(AppearanceSettings s, bool reducedMotion = false)
    {
        yield return ("Text.Body", 14 * s.TextScale);
        yield return ("Text.Small", 12.5 * s.TextScale);
        yield return ("Text.Title", 17 * s.TextScale);
        // Docking chrome (tabs, panel headers, floating window titles) follows the text size like everything else.
        yield return ("DockFontSizeNormal", 12.5 * s.TextScale);
        yield return ("DockTabHeight", Math.Round(30 * s.TextScale));
        yield return ("DockHeaderHeight", Math.Round(24 * s.TextScale));
        yield return ("DockTabItemMinHeight", Math.Round(24 * s.TextScale));
        yield return ("DockToolTabItemMinHeight", Math.Round(24 * s.TextScale));
        yield return ("DockChromeButtonWidth", Math.Round(18 * s.TextScale));
        yield return ("DockChromeButtonHeight", Math.Round(16 * s.TextScale));
        yield return ("DockIconSizeSmall", Math.Round(12 * s.TextScale));
        yield return ("DockIconSizeNormal", Math.Round(14 * s.TextScale));
        yield return ("DockCloseButtonSize", Math.Round(14 * s.TextScale));
        var icon = s.Icons switch
        {
            IconSize.Small => 16d,
            IconSize.Large => 24d,
            _ => 20d,
        };
        yield return ("Icon.Size", s.IconsFollowText ? Math.Round(icon * s.TextScale) : icon);
        var rounded = s.Corners == CornerStyle.Rounded;
        yield return ("Radius.Control", new CornerRadius(rounded ? 8 : 2));
        yield return ("Radius.Panel", new CornerRadius(rounded ? 12 : 0));
        var compact = s.Density == Density.Compact;
        yield return ("Space.ListItem", compact ? new Thickness(10, 3) : new Thickness(12, 8));
        yield return ("Size.ListItemMinHeight", compact ? 28d : 36d);
        yield return ("Motion.Duration", reducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(150));
    }
}
