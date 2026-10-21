// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Appearance;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// The map's contrast button (#345, docs/features/F1.4-map.md behavior 13): the tiles' fade, greyscale and a backing
/// behind symbols, the same viewer settings as Settings › Map, changed live from the map.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How far the tiles fade, 0 to 80 (%); the theme's own fade while the viewer hasn't chosen one.</summary>
    public double MapFadePercent
    {
        get => Math.Round(100 * (_appearance.Current.MapFade ?? ThemeFade()));
        set
        {
            var fade = Math.Clamp(Math.Round(value) / 100, 0, AppearanceSettings.MapFadeMax);
            if (_appearance.Current.MapFade != fade)
            {
                _appearance.Apply(_appearance.Current with { MapFade = fade });
            }
        }
    }

    /// <summary>"Map fade: 35 %", saying when it follows the theme.</summary>
    public string MapFadeText =>
        _text.Format(
            _appearance.Current.MapFade is null ? "Map.Contrast.FadeAuto" : "Map.Contrast.Fade",
            new Dictionary<string, object?> { ["percent"] = MapFadePercent }
        );

    /// <summary>Whether the tiles are drawn without colour.</summary>
    public bool MapGreyscale
    {
        get => _appearance.Current.MapGreyscale;
        set
        {
            if (value != MapGreyscale)
            {
                _appearance.Apply(_appearance.Current with { MapGreyscale = value });
            }
        }
    }

    /// <summary>Whether each symbol has a soft disc behind it.</summary>
    public bool SymbolBacking
    {
        get => _appearance.Current.SymbolBacking;
        set
        {
            if (value != SymbolBacking)
            {
                _appearance.Apply(_appearance.Current with { SymbolBacking = value });
            }
        }
    }

    /// <summary>Back to the theme's own fade, in colour, without backing.</summary>
    [RelayCommand]
    private void ResetMapContrast() =>
        _appearance.Apply(_appearance.Current with { MapFade = null, MapGreyscale = false, SymbolBacking = false });

    private void OnMapContrastChanged()
    {
        OnPropertyChanged(nameof(MapFadePercent));
        OnPropertyChanged(nameof(MapFadeText));
        OnPropertyChanged(nameof(MapGreyscale));
        OnPropertyChanged(nameof(SymbolBacking));
    }

    private static double ThemeFade() =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? AppearanceSettings.DarkThemeFade : 0;
}
