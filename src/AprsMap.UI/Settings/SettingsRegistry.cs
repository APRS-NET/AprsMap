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
    /// <summary>The map fade choice that follows the theme (a null <see cref="AppearanceSettings.MapFade"/>).</summary>
    private const double AutoFade = -1;

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
                "appearance.always-show-menu-bar",
                "Settings.Appearance.AlwaysShowMenuBar",
                null,
                text,
                () => appearance.Current.AlwaysShowMenuBar,
                value => Update(s => s with { AlwaysShowMenuBar = value })
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
        // Map contrast (#345): the same three as the map's own button.
        items.Add(
            new ChoiceSetting(
                "map.fade",
                "Settings.Map.Fade",
                "Settings.Map.Fade.Help",
                text,
                () =>
                    [
                        ((object?)AutoFade, text["Settings.Map.Fade.Auto"]),
                        .. AppearanceSettings.MapFadeSteps.Select(step =>
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
                    appearance.Current.MapFade is { } fade
                        ? AppearanceSettings.MapFadeSteps.MinBy(step => Math.Abs(step - fade))
                        : AutoFade,
                value => Update(s => s with { MapFade = value is double d and >= 0 ? d : null })
            )
        );
        items.Add(
            new ToggleSetting(
                "map.greyscale",
                "Settings.Map.Greyscale",
                "Settings.Map.Greyscale.Help",
                text,
                () => appearance.Current.MapGreyscale,
                value => Update(s => s with { MapGreyscale = value })
            )
        );
        items.Add(
            new ToggleSetting(
                "map.symbol-backing",
                "Settings.Map.SymbolBacking",
                "Settings.Map.SymbolBacking.Help",
                text,
                () => appearance.Current.SymbolBacking,
                value => Update(s => s with { SymbolBacking = value })
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
                "map.tracks",
                "Settings.Map.Tracks",
                null,
                text,
                () =>
                    [
                        (MapTracks.All, text["Settings.Map.Tracks.All"]),
                        (MapTracks.SelectedAndFollowed, text["Settings.Map.Tracks.SelectedAndFollowed"]),
                        (MapTracks.None, text["Settings.Map.Tracks.None"]),
                    ],
                () => appearance.Current.MapTracks,
                value => Update(s => s with { MapTracks = (MapTracks)value! })
            )
        );
        items.Add(
            new ChoiceSetting(
                "map.paths",
                "Settings.Map.Paths",
                "Settings.Map.Paths.Help",
                text,
                () =>
                    [
                        (MapPaths.SelectedStation, text["Settings.Map.Paths.SelectedStation"]),
                        (MapPaths.Off, text["Settings.Map.Paths.Off"]),
                    ],
                () => appearance.Current.MapPaths,
                value => Update(s => s with { MapPaths = (MapPaths)value! })
            )
        );
        items.Add(
            new ToggleSetting(
                "map.all-links",
                "Settings.Map.AllLinks",
                "Settings.Map.AllLinks.Help",
                text,
                () => appearance.Current.ShowAllLinks,
                value => Update(s => s with { ShowAllLinks = value })
            )
        );
        items.Add(
            new ChoiceSetting(
                "map.weather-labels",
                "Settings.Map.WeatherLabels",
                null,
                text,
                () =>
                    [
                        (MapWeatherLabels.Temperature, text["Settings.Map.WeatherLabels.Temperature"]),
                        (MapWeatherLabels.TemperatureAndWind, text["Settings.Map.WeatherLabels.TemperatureAndWind"]),
                        (MapWeatherLabels.Off, text["Settings.Map.WeatherLabels.Off"]),
                    ],
                () => appearance.Current.MapWeatherLabels,
                value => Update(s => s with { MapWeatherLabels = (MapWeatherLabels)value! })
            )
        );
        items.Add(
            new ChoiceSetting(
                "map.wind",
                "Settings.Map.Wind",
                "Settings.Map.Wind.Help",
                text,
                () => [(MapWind.Off, text["Settings.Map.Wind.Off"]), (MapWind.Barbs, text["Settings.Map.Wind.Barbs"])],
                () => appearance.Current.MapWind,
                value => Update(s => s with { MapWind = (MapWind)value! })
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
        items.Add(
            new ToggleSetting(
                "map.areas",
                "Settings.Map.Areas",
                "Settings.Map.Areas.Help",
                text,
                () => appearance.Current.ShowAreas,
                value => Update(s => s with { ShowAreas = value })
            )
        );
        items.Add(
            new ToggleSetting(
                "map.filter-circles",
                "Settings.Map.FilterCircles",
                "Settings.Map.FilterCircles.Help",
                text,
                () => appearance.Current.ShowFilterCircles,
                value => Update(s => s with { ShowFilterCircles = value })
            )
        );
        items.Add(
            new ToggleSetting(
                "map.dead-reckoning",
                "Settings.Map.DeadReckoning",
                "Settings.Map.DeadReckoning.Help",
                text,
                () => appearance.Current.DeadReckoning,
                value => Update(s => s with { DeadReckoning = value })
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
            // F1.5 behavior 1: the station's track limits, for every viewer.
            items.Add(
                new ChoiceSetting(
                    "map.track-points",
                    "Settings.Map.TrackPoints",
                    "Settings.Map.TrackPoints.Help",
                    text,
                    () =>
                        [
                            .. TrackPointSteps
                                .Union([current.TrackPoints])
                                .Order()
                                .Select(n =>
                                    (
                                        (object?)n,
                                        text.Format(
                                            "Settings.Map.Positions",
                                            new Dictionary<string, object?> { ["count"] = n }
                                        )
                                    )
                                ),
                        ],
                    () => current.TrackPoints,
                    value => Set(current with { TrackPoints = (int)value! })
                )
            );
            items.Add(
                new ChoiceSetting(
                    "map.track-age",
                    "Settings.Map.TrackAge",
                    "Settings.Map.TrackPoints.Help",
                    text,
                    () =>
                        [
                            .. TrackAgeSteps
                                .Union([current.TrackMinutes])
                                .Order()
                                .Select(m => ((object?)m, Minutes(text, m))),
                        ],
                    () => current.TrackMinutes,
                    value => Set(current with { TrackMinutes = (int)value! })
                )
            );
        }
        return items;
    }

    /// <summary>
    /// Settings › Units (docs/features/F1.10-weather.md, behavior 6): a unit for each quantity, the station's setting
    /// (<see cref="Contracts.AppSettings.Units"/>), saved through the engine.
    /// </summary>
    public static IReadOnlyList<SettingItem> Units(
        Localizer text,
        Func<Contracts.UnitSettings> get,
        Action<Contracts.UnitSettings> save
    )
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(save);
        ChoiceSetting Choice<T>(
            string id,
            string label,
            T[] values,
            Func<Contracts.UnitSettings, T> read,
            Func<Contracts.UnitSettings, T, Contracts.UnitSettings> write
        )
            where T : struct, Enum =>
            new(
                id,
                label,
                null,
                text,
                () => [.. values.Select(v => ((object?)v, text[$"Settings.Units.{typeof(T).Name}.{v}"]))],
                () => read(get()),
                value => save(write(get(), (T)value!))
            );
        return
        [
            Choice(
                "units.distance",
                "Settings.Units.Distance",
                Enum.GetValues<Contracts.DistanceUnit>(),
                u => u.Distance,
                (u, v) => u with { Distance = v }
            ),
            Choice(
                "units.altitude",
                "Settings.Units.Altitude",
                Enum.GetValues<Contracts.AltitudeUnit>(),
                u => u.Altitude,
                (u, v) => u with { Altitude = v }
            ),
            Choice(
                "units.temperature",
                "Settings.Units.Temperature",
                Enum.GetValues<Contracts.TemperatureUnit>(),
                u => u.Temperature,
                (u, v) => u with { Temperature = v }
            ),
            Choice(
                "units.wind",
                "Settings.Units.Wind",
                Enum.GetValues<Contracts.SpeedUnit>(),
                u => u.Wind,
                (u, v) => u with { Wind = v }
            ),
            Choice(
                "units.rain",
                "Settings.Units.Rain",
                Enum.GetValues<Contracts.RainUnit>(),
                u => u.Rain,
                (u, v) => u with { Rain = v }
            ),
            Choice(
                "units.pressure",
                "Settings.Units.Pressure",
                Enum.GetValues<Contracts.PressureUnit>(),
                u => u.Pressure,
                (u, v) => u with { Pressure = v }
            ),
        ];
    }

    private static readonly int[] StaleSteps = [10, 15, 30, 60, 120];
    private static readonly int[] RemoveSteps = [30, 60, 120, 360, 1440];
    private static readonly int[] TrackPointSteps = [100, 250, 500, 1000, 2000, 5000];
    private static readonly int[] TrackAgeSteps = [30, 60, 120, 360, 1440];

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
