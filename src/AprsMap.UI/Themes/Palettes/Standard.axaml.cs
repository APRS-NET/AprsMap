// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AprsMap.UI.Themes.Palettes;

/// <summary>The Standard palette, compiled so it can be swapped in at runtime without loading XAML (trim-safe).</summary>
public sealed partial class StandardPalette : ResourceDictionary
{
    /// <summary>Creates the palette's resources.</summary>
    public StandardPalette() => AvaloniaXamlLoader.Load(this);
}
