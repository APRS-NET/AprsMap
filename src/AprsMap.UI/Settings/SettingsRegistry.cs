// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.UI.Appearance;
using AprsMap.UI.Localization;

namespace AprsMap.UI.Settings;

/// <summary>
/// Every setting, declared once (docs/features/F0.6-appearance.md, behavior 4): its section, label, help, kind and
/// where its value lives. The Settings page is built from it, and later the command palette and settings search.
/// </summary>
public static class SettingsRegistry
{
    /// <summary>The Appearance section's options, bound to <paramref name="appearance"/>.</summary>
    public static IReadOnlyList<SettingItem> Appearance(
        AppearanceService appearance,
        Localizer text,
        bool showPseudoLanguage
    )
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(text);

        void Update(Func<AppearanceSettings, AppearanceSettings> change) =>
            appearance.Apply(change(appearance.Current));

        // Written out key by key, so the localization guard sees every string.
        return
        [
            new ChoiceSetting(
                "appearance.theme",
                "Settings.Appearance.Theme",
                "Settings.Appearance.Theme.Help",
                text,
                () =>
                    [
                        (ThemeMode.Auto, text["Settings.Appearance.Theme.Auto"]),
                        (ThemeMode.Light, text["Settings.Appearance.Theme.Light"]),
                        (ThemeMode.Dark, text["Settings.Appearance.Theme.Dark"]),
                    ],
                () => appearance.Current.Theme,
                value => Update(s => s with { Theme = (ThemeMode)value! })
            ),
            new ChoiceSetting(
                "appearance.palette",
                "Settings.Appearance.Palette",
                "Settings.Appearance.Palette.Help",
                text,
                () =>
                    [
                        (Palette.Standard, text["Settings.Appearance.Palette.Standard"]),
                        (Palette.HighContrast, text["Settings.Appearance.Palette.HighContrast"]),
                        (Palette.ColourVisionSafe, text["Settings.Appearance.Palette.ColourVisionSafe"]),
                    ],
                // Until the user chooses, the OS contrast preference picks one; show that one.
                () => appearance.EffectivePalette,
                value => Update(s => s with { Palette = (Palette)value! })
            ),
            new ChoiceSetting(
                "appearance.text-size",
                "Settings.Appearance.TextSize",
                null,
                text,
                () =>
                    [
                        .. AppearanceSettings.TextScaleSteps.Select(step =>
                            (
                                (object?)step,
                                text.Format(
                                    "Settings.Appearance.Percent",
                                    new Dictionary<string, object?> { ["percent"] = Math.Round(step * 100) }
                                )
                            )
                        ),
                    ],
                () => AppearanceSettings.TextScaleSteps.MinBy(step => Math.Abs(step - appearance.Current.TextScale)),
                value => Update(s => s with { TextScale = (double)value! })
            ),
            new ChoiceSetting(
                "appearance.icon-size",
                "Settings.Appearance.IconSize",
                null,
                text,
                () =>
                    [
                        (IconSize.Small, text["Settings.Appearance.IconSize.Small"]),
                        (IconSize.Medium, text["Settings.Appearance.IconSize.Medium"]),
                        (IconSize.Large, text["Settings.Appearance.IconSize.Large"]),
                    ],
                () => appearance.Current.Icons,
                value => Update(s => s with { Icons = (IconSize)value! })
            ),
            new ToggleSetting(
                "appearance.icons-follow-text",
                "Settings.Appearance.IconsFollowText",
                null,
                text,
                () => appearance.Current.IconsFollowText,
                value => Update(s => s with { IconsFollowText = value })
            ),
            new ChoiceSetting(
                "appearance.corners",
                "Settings.Appearance.Corners",
                null,
                text,
                () =>
                    [
                        (CornerStyle.Rounded, text["Settings.Appearance.Corners.Rounded"]),
                        (CornerStyle.Sharp, text["Settings.Appearance.Corners.Sharp"]),
                    ],
                () => appearance.Current.Corners,
                value => Update(s => s with { Corners = (CornerStyle)value! })
            ),
            new ChoiceSetting(
                "appearance.density",
                "Settings.Appearance.Density",
                "Settings.Appearance.Density.Help",
                text,
                () =>
                    [
                        (Density.Comfortable, text["Settings.Appearance.Density.Comfortable"]),
                        (Density.Compact, text["Settings.Appearance.Density.Compact"]),
                    ],
                () => appearance.Current.Density,
                value => Update(s => s with { Density = (Density)value! })
            ),
            new ChoiceSetting(
                "appearance.motion",
                "Settings.Appearance.Motion",
                "Settings.Appearance.Motion.Help",
                text,
                () =>
                    [
                        (MotionPreference.FollowSystem, text["Settings.Appearance.Motion.FollowSystem"]),
                        (MotionPreference.Reduced, text["Settings.Appearance.Motion.Reduced"]),
                    ],
                () => appearance.Current.Motion,
                value => Update(s => s with { Motion = (MotionPreference)value! })
            ),
            new ChoiceSetting(
                "appearance.language",
                "Settings.Appearance.Language",
                "Settings.Appearance.Language.Help",
                text,
                () =>
                    [
                        (null, text["Settings.Appearance.Language.System"]),
                        .. Localizer
                            .Languages(showPseudoLanguage)
                            .Select(tag =>
                                (
                                    (object?)tag,
                                    tag == Localizer.PseudoLanguage
                                        ? text["Settings.Appearance.Language.Pseudo"]
                                        : NativeName(tag)
                                )
                            ),
                    ],
                () => appearance.Current.Language,
                value => Update(s => s with { Language = (string?)value })
            ),
        ];
    }

    /// <summary>
    /// The Map section's options (docs/features/F1.4-map.md, behavior 21): the viewer's own tile provider, brightness,
    /// labels and symbol size, and, where this viewer may change the station's settings, when stations fade and leave
    /// (F1.3 behavior 9).
    /// </summary>
    /// <param name="appearance">Where the viewer's map preferences live.</param>
    /// <param name="text">For the labels.</param>
    /// <param name="providers">The tile providers offered.</param>
    /// <param name="aging">The station's aging and how to save a change; null hides those options (a read-only gateway).</param>
    public static IReadOnlyList<SettingItem> Map(
        AppearanceService appearance,
        Localizer text,
        IReadOnlyList<Contracts.TileProviderInfo> providers,
        (Func<Contracts.StationAging> Get, Action<Contracts.StationAging> Save)? aging
    )
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(providers);

        void Update(Func<AppearanceSettings, AppearanceSettings> change) =>
            appearance.Apply(change(appearance.Current));

        var items = new List<SettingItem>();
        if (providers.Count > 1)
        {
            items.Add(
                new ChoiceSetting(
                    "map.provider",
                    "Settings.Map.Provider",
                    null,
                    text,
                    () => [.. providers.Select(p => ((object?)p.Key, p.Name))],
                    () => appearance.Current.MapProvider ?? providers[0].Key,
                    value => Update(s => s with { MapProvider = (string?)value })
                )
            );
        }
        items.Add(
            new ChoiceSetting(
                "map.brightness",
                "Settings.Map.Brightness",
                "Settings.Map.Brightness.Help",
                text,
                () =>
                    [
                        (MapBrightness.Auto, text["Settings.Map.Brightness.Auto"]),
                        (MapBrightness.Normal, text["Settings.Map.Brightness.Normal"]),
                        (MapBrightness.Dimmed, text["Settings.Map.Brightness.Dimmed"]),
                        (MapBrightness.Dark, text["Settings.Map.Brightness.Dark"]),
                    ],
                () => appearance.Current.MapBrightness,
                value => Update(s => s with { MapBrightness = (MapBrightness)value! })
            )
        );
        items.Add(
            new ChoiceSetting(
                "map.labels",
                "Settings.Map.Labels",
                null,
                text,
                () =>
                    [
                        (MapLabels.AllThatFit, text["Settings.Map.Labels.AllThatFit"]),
                        (MapLabels.None, text["Settings.Map.Labels.None"]),
                    ],
                () => appearance.Current.MapLabels,
                value => Update(s => s with { MapLabels = (MapLabels)value! })
            )
        );
        items.Add(
            new ChoiceSetting(
                "map.symbol-size",
                "Settings.Map.SymbolSize",
                "Settings.Map.SymbolSize.Help",
                text,
                () =>
                    [
                        .. AppearanceSettings.MapSymbolScaleSteps.Select(step =>
                            (
                                (object?)step,
                                text.Format(
                                    "Settings.Appearance.Percent",
                                    new Dictionary<string, object?> { ["percent"] = Math.Round(step * 100) }
                                )
                            )
                        ),
                    ],
                () =>
                    AppearanceSettings.MapSymbolScaleSteps.MinBy(step =>
                        Math.Abs(step - appearance.Current.MapSymbolScale)
                    ),
                value => Update(s => s with { MapSymbolScale = (double)value! })
            )
        );
        if (aging is var (get, save))
        {
            // Removal always after staleness, so each list offers only what fits the other's current value. The value is
            // kept here as chosen, so both lists follow at once while the engine saves it.
            var current = get();
            ChoiceSetting? stale = null;
            ChoiceSetting? remove = null;
            void Set(Contracts.StationAging value)
            {
                current = value;
                save(value);
                stale?.Retranslate();
                remove?.Retranslate();
            }
            stale = new ChoiceSetting(
                "map.stale-after",
                "Settings.Map.StaleAfter",
                "Settings.Map.StaleAfter.Help",
                text,
                () =>
                    [
                        .. StaleSteps
                            .Where(m => current.RemoveAfterMinutes is not { } after || m < after)
                            .Select(m => ((object?)m, Minutes(text, m))),
                    ],
                () => current.StaleAfterMinutes,
                value => Set(current with { StaleAfterMinutes = (int)value! })
            );
            remove = new ChoiceSetting(
                "map.remove-after",
                "Settings.Map.RemoveAfter",
                "Settings.Map.RemoveAfter.Help",
                text,
                () =>
                    [
                        .. RemoveSteps
                            .Where(m => m > current.StaleAfterMinutes)
                            .Select(m => ((object?)m, Minutes(text, m))),
                        (null, text["Settings.Map.RemoveAfter.Never"]),
                    ],
                () => current.RemoveAfterMinutes,
                value => Set(current with { RemoveAfterMinutes = (int?)value })
            );
            items.Add(stale);
            items.Add(remove);
        }
        return items;
    }

    private static readonly int[] StaleSteps = [10, 15, 30, 60, 120];
    private static readonly int[] RemoveSteps = [30, 60, 120, 360, 1440];

    private static string Minutes(Localizer text, int minutes) =>
        minutes % 60 == 0
            ? text.Format("Settings.Map.Hours", new Dictionary<string, object?> { ["hours"] = minutes / 60 })
            : text.Format("Settings.Map.Minutes", new Dictionary<string, object?> { ["minutes"] = minutes });

    /// <summary>A language in its own name, capitalised as a menu item ("Deutsch", "English").</summary>
    internal static string NativeName(string tag)
    {
        var culture = CultureInfo.GetCultureInfo(tag);
        var name = culture.NativeName;
        return name.Length == 0 ? tag : char.ToUpper(name[0], culture) + name[1..];
    }
}
