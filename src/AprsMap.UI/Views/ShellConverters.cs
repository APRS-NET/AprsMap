// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Data.Converters;

namespace AprsMap.UI.Views;

/// <summary>Spike: picks the packets view for the packets panel, a placeholder for the others.</summary>
public static class ShellConverters
{
    public static readonly IValueConverter IsPackets = new FuncValueConverter<string?, bool>(k => k == "packets");
    public static readonly IValueConverter IsNotPackets = new FuncValueConverter<string?, bool>(k => k != "packets");
}
