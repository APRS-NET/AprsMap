// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Appearance;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

namespace AprsMap.UI.Tests;

public class AppearanceServiceTests
{
    [Fact]
    public void TokensFollowTheSettings()
    {
        var tokens = AppearanceService
            .Tokens(
                new AppearanceSettings
                {
                    TextScale = 1.5,
                    Icons = IconSize.Large,
                    Corners = CornerStyle.Sharp,
                }
            )
            .ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal(21d, tokens["Text.Body"]);
        Assert.Equal(24d, tokens["Icon.Size"]);
        Assert.Equal(new CornerRadius(2), tokens["Radius.Control"]);
        Assert.Equal(new CornerRadius(0), tokens["Radius.Panel"]);
    }

    [Fact]
    public void TextScaleIsClampedAndChangeIsRaised()
    {
        var service = new AppearanceService();
        AppearanceSettings? raised = null;
        service.Changed += (_, s) => raised = s;

        service.Apply(new AppearanceSettings { TextScale = 5 });

        Assert.Equal(AppearanceSettings.MaxTextScale, service.Current.TextScale);
        Assert.Same(service.Current, raised);
    }

    [AvaloniaFact]
    public void ApplyingSettingsUpdatesTheRunningApp()
    {
        var app = Assert.IsType<App>(Application.Current);

        app.Appearance.Apply(new AppearanceSettings { Theme = ThemeMode.Dark, Corners = CornerStyle.Sharp });

        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.Equal(new CornerRadius(2), app.Resources["Radius.Control"]);

        app.Appearance.Apply(new AppearanceSettings());
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
    }
}
