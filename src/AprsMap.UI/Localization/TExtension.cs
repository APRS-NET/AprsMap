// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia;

namespace AprsMap.UI.Localization;

/// <summary>
/// Translated text in XAML: <c>Text="{l:T Main.ProfilePicker.ToolTip}"</c> with
/// <c>xmlns:l="using:AprsMap.UI.Localization"</c>. It updates when the language changes. Built on an observable rather
/// than a reflection binding, so it is safe in the trimmed browser build.
/// </summary>
public sealed class TExtension(string key)
{
    /// <summary>The string's key in <c>Strings.resx</c>.</summary>
    public string Key { get; } = key;

    /// <summary>Called by the XAML loader.</summary>
    public object ProvideValue(IServiceProvider serviceProvider) => Localizer.Instance.Observe(Key).ToBinding();
}
