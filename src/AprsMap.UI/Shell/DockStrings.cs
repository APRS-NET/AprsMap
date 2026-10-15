// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using Avalonia.Controls;

namespace AprsMap.UI.Shell;

/// <summary>
/// Dock's own menu and drag texts (resources its themes look up dynamically), set from our translated strings and
/// again whenever the language changes (F1.0 behavior 6, F0.9).
/// </summary>
public static class DockStrings
{
    // Dock's resource key → our string key. Dock's document-tab and MDI strings aren't listed: the map is the only
    // document and has no tab menu.
    private static readonly (string Dock, string Ours)[] Map =
    [
        ("ToolChromeControlFloatString", "Dock.Float"),
        ("ToolChromeControlDockString", "Dock.Dock"),
        ("ToolChromeControlAutoHideString", "Dock.AutoHide"),
        ("ToolChromeControlDockAsDocumentString", "Dock.DockAsDocument"),
        ("ToolChromeControlCloseString", "Dock.Close"),
        ("ToolTabStripItemFloatString", "Dock.Float"),
        ("ToolTabStripItemFloatAllString", "Dock.FloatAll"),
        ("ToolTabStripItemDockString", "Dock.Dock"),
        ("ToolTabStripItemAutoHideString", "Dock.AutoHide"),
        ("ToolTabStripItemDockAsDocumentString", "Dock.DockAsDocument"),
        ("ToolTabStripItemCloseString", "Dock.Close"),
        ("ToolPinItemControlFloatString", "Dock.Float"),
        ("ToolPinItemControlShowString", "Dock.Show"),
        ("ToolPinItemControlDockAsDocumentString", "Dock.DockAsDocument"),
        ("ToolPinItemControlCloseString", "Dock.Close"),
        ("DragPreviewControlDockString", "Dock.Preview.Dock"),
        ("DragPreviewControlFloatString", "Dock.Preview.Float"),
        ("DragPreviewControlNoneString", "Dock.Preview.None"),
    ];

    /// <summary>The string keys used, for the localization guard.</summary>
    public static IEnumerable<string> Keys => Map.Select(m => m.Ours).Distinct();

    /// <summary>Sets Dock's texts in <paramref name="resources"/> now and after every language change.</summary>
    public static void Attach(IResourceDictionary resources, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(text);
        Apply(resources, text);
        text.LanguageChanged += (_, _) => Apply(resources, text);
    }

    internal static void Apply(IResourceDictionary resources, Localizer text)
    {
        foreach (var (dock, ours) in Map)
        {
            resources[dock] = text[ours];
        }
    }
}
