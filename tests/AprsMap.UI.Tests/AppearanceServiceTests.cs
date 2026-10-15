// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Appearance;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AprsMap.UI.Tests;

/// <summary>Changes the running app's appearance, so these run alone, after the parallel tests.</summary>
[Collection(nameof(LanguageViewTests))]
public class AppearanceServiceTests
{
    private static Dictionary<string, object> Tokens(AppearanceSettings settings, bool reducedMotion = false) =>
        AppearanceService.Tokens(settings, reducedMotion).ToDictionary(t => t.Key, t => t.Value);

    [Fact]
    public void TokensFollowTheSettings()
    {
        var tokens = Tokens(
            new AppearanceSettings
            {
                TextScale = 1.5,
                Icons = IconSize.Large,
                IconsFollowText = false,
                Corners = CornerStyle.Sharp,
            }
        );

        Assert.Equal(21d, tokens["Text.Body"]);
        Assert.Equal(24d, tokens["Icon.Size"]);
        Assert.Equal(new CornerRadius(2), tokens["Radius.Control"]);
        Assert.Equal(new CornerRadius(0), tokens["Radius.Panel"]);
    }

    [Theory]
    [InlineData(IconSize.Small, 1.0, 16)]
    [InlineData(IconSize.Medium, 1.5, 30)]
    [InlineData(IconSize.Large, 2.0, 48)]
    public void IconsFollowTheTextSizeByDefault(IconSize icons, double textScale, double expected) =>
        Assert.Equal(expected, Tokens(new AppearanceSettings { Icons = icons, TextScale = textScale })["Icon.Size"]);

    [Fact]
    public void DensityAndMotionHaveTheirTokens()
    {
        var comfortable = Tokens(new AppearanceSettings());
        var compact = Tokens(new AppearanceSettings { Density = Density.Compact }, reducedMotion: true);

        Assert.Equal(new Thickness(12, 8), comfortable["Space.ListItem"]);
        Assert.Equal(36d, comfortable["Size.ListItemMinHeight"]);
        Assert.Equal(TimeSpan.FromMilliseconds(150), comfortable["Motion.Duration"]);
        Assert.Equal(new Thickness(10, 3), compact["Space.ListItem"]);
        Assert.Equal(28d, compact["Size.ListItemMinHeight"]);
        Assert.Equal(TimeSpan.Zero, compact["Motion.Duration"]);
    }

    [Fact]
    public void ScalesAreClampedAndChangeIsRaised()
    {
        var service = new AppearanceService();
        AppearanceSettings? raised = null;
        service.Changed += (_, s) => raised = s;

        service.Apply(new AppearanceSettings { TextScale = 5, MapSymbolScale = 0.1 });

        Assert.Equal(AppearanceSettings.MaxTextScale, service.Current.TextScale);
        Assert.Equal(AppearanceSettings.MinMapSymbolScale, service.Current.MapSymbolScale);
        Assert.Same(service.Current, raised);
    }

    [Fact]
    public void WithoutAChoiceThePaletteFollowsTheSystemAndMotionCanBeReduced()
    {
        var service = new AppearanceService();
        Assert.Null(service.Current.Palette);
        Assert.Equal(Palette.Standard, service.EffectivePalette); // no platform: no contrast preference
        Assert.False(service.ReducedMotion);

        service.SetSystemReducedMotion(true);
        Assert.True(service.ReducedMotion);
        service.Apply(new AppearanceSettings { Palette = Palette.ColourVisionSafe, Motion = MotionPreference.Reduced });
        Assert.Equal(Palette.ColourVisionSafe, service.EffectivePalette);
        service.SetSystemReducedMotion(false);
        Assert.True(service.ReducedMotion); // chosen, so it stays
    }

    [AvaloniaFact]
    public void ApplyingSettingsUpdatesTheRunningApp()
    {
        var app = Assert.IsType<App>(Application.Current);
        try
        {
            app.Appearance.Apply(
                new AppearanceSettings
                {
                    Theme = ThemeMode.Dark,
                    Corners = CornerStyle.Sharp,
                    Palette = Palette.HighContrast,
                    Density = Density.Compact,
                }
            );

            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.Equal(new CornerRadius(2), app.Resources["Radius.Control"]);
            Assert.True(app.TryGetResource("Color.Text", ThemeVariant.Dark, out var text));
            Assert.Equal(Color.Parse("#FFFFFF"), text);
            Assert.True(app.TryGetResource("Thickness.Border", ThemeVariant.Dark, out var border));
            Assert.Equal(new Thickness(2), border);
            var fluent = app.Styles.OfType<FluentTheme>().Single();
            Assert.Equal(DensityStyle.Compact, fluent.DensityStyle);
            Assert.Equal(Color.Parse("#A9C8FF"), fluent.Palettes[ThemeVariant.Dark].Accent);
        }
        finally
        {
            app.Appearance.Apply(new AppearanceSettings());
        }
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.True(app.TryGetResource("Color.Text", ThemeVariant.Light, out var standard));
        Assert.Equal(Color.Parse("#16191D"), standard);
        Assert.Equal(DensityStyle.Normal, app.Styles.OfType<FluentTheme>().Single().DensityStyle);
    }
}
