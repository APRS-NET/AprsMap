// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class SettingsViewModelTests
{
    private static (
        SettingsViewModel Settings,
        AppearanceService Appearance,
        FakeTimeProvider Time,
        Localizer Text
    ) Create(bool showPseudo = false)
    {
        var appearance = new AppearanceService();
        var time = new FakeTimeProvider();
        var text = new Localizer(CultureInfo.GetCultureInfo("en-US"));
        return (new SettingsViewModel(appearance, time, text, showPseudo), appearance, time, text);
    }

    private static T Item<T>(SettingsViewModel settings, string id)
        where T : SettingItem => Assert.IsType<T>(settings.AppearanceItems.Single(i => i.Id == id));

    private static void Choose(ChoiceSetting setting, string label) =>
        setting.Selected = setting.Options.Single(o => o.Label == label);

    [Fact]
    public void ItOpensOnAppearanceAndAboutIsNoLongerASection()
    {
        var (settings, _, _, _) = Create();

        Assert.Equal("Settings", settings.Title);
        Assert.Equal(["Appearance"], settings.Sections.Select(s => s.Title)); // About is Help › About (F1.29)
        Assert.True(settings.IsAppearanceSection);
        Assert.Equal("Appearance", settings.SelectedSection!.ToString());
    }

    [Fact]
    public void EveryOptionAppliesAtOnce()
    {
        var (settings, appearance, _, _) = Create();

        Choose(Item<ChoiceSetting>(settings, "appearance.theme"), "Dark");
        Choose(Item<ChoiceSetting>(settings, "appearance.palette"), "High contrast");
        Choose(Item<ChoiceSetting>(settings, "appearance.text-size"), "150%");
        Choose(Item<ChoiceSetting>(settings, "appearance.icon-size"), "Large");
        Item<ToggleSetting>(settings, "appearance.icons-follow-text").IsOn = false;
        Choose(Item<ChoiceSetting>(settings, "appearance.corners"), "Sharp");
        Choose(Item<ChoiceSetting>(settings, "appearance.density"), "Compact");
        Choose(Item<ChoiceSetting>(settings, "appearance.motion"), "Reduced");

        Assert.Equal(
            new AppearanceSettings
            {
                Theme = ThemeMode.Dark,
                Palette = Palette.HighContrast,
                TextScale = 1.5,
                Icons = IconSize.Large,
                IconsFollowText = false,
                Corners = CornerStyle.Sharp,
                Density = Density.Compact,
                Motion = MotionPreference.Reduced,
            },
            appearance.Current
        );
    }

    [Fact]
    public void TheOptionsShowTheCurrentValuesAndFollowChangesMadeElsewhere()
    {
        var (settings, appearance, _, _) = Create();
        var theme = Item<ChoiceSetting>(settings, "appearance.theme");
        var palette = Item<ChoiceSetting>(settings, "appearance.palette");
        var size = Item<ChoiceSetting>(settings, "appearance.text-size");

        Assert.Equal("Auto", theme.Selected?.Label);
        Assert.Equal("Standard", palette.Selected?.Label); // no choice yet: what the system picks
        Assert.Equal("100%", size.Selected?.Label);
        Assert.Equal(7, size.Options.Count);

        appearance.Apply(new AppearanceSettings { Theme = ThemeMode.Light, TextScale = 1.2 });

        Assert.Equal("Light", theme.Selected?.Label);
        Assert.Equal("115%", size.Selected?.Label); // the nearest step
        Assert.Equal("Theme", theme.Label);
        Assert.Equal("Auto follows your system's light or dark setting.", theme.Help);
        Assert.Null(size.Help);
    }

    [Fact]
    public void ThePseudoLanguageIsOfferedOnlyWhenAsked()
    {
        string[] Languages(SettingsViewModel s) =>
            [.. Item<ChoiceSetting>(s, "appearance.language").Options.Select(o => o.Label)];

        Assert.Equal(["System default", "English"], Languages(Create().Settings));
        Assert.Equal(
            ["System default", "English", "Pseudo-language (for testing)"],
            Languages(Create(showPseudo: true).Settings)
        );
        Assert.Equal("Deutsch", SettingsRegistry.NativeName("de"));
    }

    [Fact]
    public void ResetCanBeUndoneForTenSeconds()
    {
        var (settings, appearance, time, _) = Create();
        var custom = new AppearanceSettings { Theme = ThemeMode.Dark, Corners = CornerStyle.Sharp };
        appearance.Apply(custom);

        settings.ResetAppearanceCommand.Execute(null);
        Assert.Equal(new AppearanceSettings(), appearance.Current);
        Assert.True(settings.CanUndoReset);
        settings.UndoResetCommand.Execute(null);
        Assert.Equal(custom, appearance.Current);
        Assert.False(settings.CanUndoReset);

        settings.ResetAppearanceCommand.Execute(null);
        time.Advance(SettingsViewModel.UndoWindow);
        Assert.False(settings.CanUndoReset);
        settings.UndoResetCommand.Execute(null); // too late: nothing to undo
        Assert.Equal(new AppearanceSettings(), appearance.Current);
    }

    [Fact]
    public void AboutShowsTheVersionsAndCreditsLookupOnlyWhenItIsOn()
    {
        var (_, _, _, text) = Create();
        var about = new AboutViewModel(text); // a dialog from Help › About (F1.29)
        about.Show(null, null);
        Assert.Equal("Version " + AboutViewModel.ShortVersion(Brand.Version), about.VersionText);
        Assert.Equal("0.1.0-alpha.10.3 (e86ba03)", AboutViewModel.ShortVersion("0.1.0-alpha.10.3+e86ba03a4f5f642f"));
        Assert.Equal("1.2.3", AboutViewModel.ShortVersion("1.2.3"));
        Assert.Null(about.EngineText);
        Assert.Contains("AGPL-3.0", about.LicenceText, StringComparison.Ordinal);
        Assert.Equal(Brand.DisplayName, AboutViewModel.ProductName);
        Assert.Equal(new Uri(Brand.SourceUrl), AboutViewModel.SourceUri);

        about.Show(
            new EngineStatus(ConnectionState.Connected, "T2TEST", 1, "1.2.3", AprsNetVersion: "0.1.0-alpha.17"),
            new GeocodingInfo(true, "nominatim.openstreetmap.org", null)
        );
        Assert.Equal("Engine 1.2.3 · APRS.NET 0.1.0-alpha.17", about.EngineText);
        Assert.True(about.ShowLookupCredit);

        about.Show(null, GeocodingInfo.Disabled);
        Assert.False(about.ShowLookupCredit);
        Assert.Null(about.VersionsDifferText); // its own engine
    }

    [Fact]
    public void AboutNamesAGatewaysVersionAndHowItsInstalledAndSaysWhenItDiffers()
    {
        // #453
        var (_, _, _, text) = Create();
        var about = new AboutViewModel(text) { Remote = true };
        var app = Brand.Version.Split('+')[0];

        about.Show(
            new EngineStatus(
                ConnectionState.Connected,
                null,
                1,
                app,
                AprsNetVersion: "0.7.0",
                Install: EngineInstall.Service
            ),
            null
        );
        Assert.Equal($"Gateway {app} (Linux service) · APRS.NET 0.7.0", about.EngineText);
        Assert.Null(about.VersionsDifferText);

        about.Show(new EngineStatus(ConnectionState.Connected, null, 1, "99.0.1", AprsNetVersion: "0.7.0"), null);
        Assert.Equal("Gateway 99.0.1 · APRS.NET 0.7.0", about.EngineText); // an older gateway doesn't say how
        Assert.StartsWith("Its version differs from this app's", about.VersionsDifferText, StringComparison.Ordinal);
    }

    [Fact]
    public void ALanguageChangeRewordsThePageAndKeepsTheSection()
    {
        var (settings, _, _, text) = Create();
        settings.SelectedSection = settings.Sections[0];

        text.SetLanguage(Localizer.PseudoLanguage);

        Assert.StartsWith("[Šéţţîñĝš", settings.Title, StringComparison.Ordinal);
        Assert.Equal("appearance", settings.SelectedSection?.Id);
        Assert.All(settings.AppearanceItems, i => Assert.StartsWith("[", i.Label, StringComparison.Ordinal));
        Assert.StartsWith(
            "[",
            Item<ChoiceSetting>(settings, "appearance.theme").Selected?.Label,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void CloseIsAnnouncedAndDisposingStopsFollowingChanges()
    {
        var (settings, appearance, _, _) = Create();
        var closed = 0;
        settings.Closed += (_, _) => closed++;

        settings.CloseCommand.Execute(null);
        settings.Dispose();
        appearance.Apply(new AppearanceSettings { Theme = ThemeMode.Dark });

        Assert.Equal(1, closed);
        Assert.Equal("Auto", Item<ChoiceSetting>(settings, "appearance.theme").Selected?.Label);
    }
}
