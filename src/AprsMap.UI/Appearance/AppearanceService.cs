// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia;
using Avalonia.Styling;

namespace AprsMap.UI.Appearance;

/// <summary>
/// Applies <see cref="AppearanceSettings"/> by rewriting the size and shape tokens and the theme variant at runtime.
/// Views use DynamicResource, so every change shows immediately without reloading anything.
/// </summary>
public sealed class AppearanceService
{
    private Application? _app;

    /// <summary>The settings currently applied.</summary>
    public AppearanceSettings Current { get; private set; } = new();

    /// <summary>Raised after new settings are applied.</summary>
    public event EventHandler<AppearanceSettings>? Changed;

    /// <summary>Binds the service to an application and applies the current settings.</summary>
    public void Attach(Application app)
    {
        _app = app;
        Apply(Current);
    }

    /// <summary>Applies new settings (out-of-range text scales are clamped).</summary>
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
        };

        if (_app is not null)
        {
            _app.RequestedThemeVariant = Current.Theme switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            foreach (var (key, value) in Tokens(Current))
            {
                _app.Resources[key] = value;
            }
        }

        Localization.Localizer.Instance.SetLanguage(Current.Language);

        Changed?.Invoke(this, Current);
    }

    // The palette dictionaries (HighContrast, ColourVisionSafe) are layered in a later change (scope F0.6).
    internal static IEnumerable<(string Key, object Value)> Tokens(AppearanceSettings s)
    {
        yield return ("Text.Body", 14 * s.TextScale);
        yield return ("Text.Small", 12.5 * s.TextScale);
        yield return ("Text.Title", 17 * s.TextScale);
        yield return (
            "Icon.Size",
            s.Icons switch
            {
                IconSize.Small => 16d,
                IconSize.Large => 24d,
                _ => 20d,
            }
        );
        var rounded = s.Corners == CornerStyle.Rounded;
        yield return ("Radius.Control", new CornerRadius(rounded ? 8 : 2));
        yield return ("Radius.Panel", new CornerRadius(rounded ? 12 : 0));
    }
}
