// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Data.Converters;

namespace AprsMap.UI.Shell;

/// <summary>Converters for the shell's XAML.</summary>
public static class ShellConverters
{
    /// <summary>True unless the value equals the parameter: hides "Move to left" in the left dock.</summary>
    public static IValueConverter IsNot { get; } =
        new FuncValueConverter<string?, string?, bool>((value, parameter) => value != parameter);
}
