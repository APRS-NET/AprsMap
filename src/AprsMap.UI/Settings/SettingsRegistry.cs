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

    /// <summary>A language in its own name, capitalised as a menu item ("Deutsch", "English").</summary>
    internal static string NativeName(string tag)
    {
        var culture = CultureInfo.GetCultureInfo(tag);
        var name = culture.NativeName;
        return name.Length == 0 ? tag : char.ToUpper(name[0], culture) + name[1..];
    }
}
