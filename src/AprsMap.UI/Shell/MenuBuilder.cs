// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Automation;
using Avalonia.Controls;

namespace AprsMap.UI.Shell;

/// <summary>
/// Turns <see cref="AppMenus"/> into Avalonia menus (docs/features/F1.29-menus.md, behavior 3): menu items for the menu
/// bar and the ☰ menu, and native items for the macOS menu bar. A command that can't run is greyed out with its reason
/// as the tooltip and for screen readers (behaviors 2, 16).
/// </summary>
public static class MenuBuilder
{
    /// <summary>The items for one menu's lines.</summary>
    public static List<object> Items(IReadOnlyList<MenuEntry> entries)
    {
        var items = new List<object>(entries.Count);
        foreach (var entry in Tidy(entries))
        {
            items.Add(Item(entry));
        }
        return items;
    }

    /// <summary>
    /// A top-level menu, filled now. It's kept filled and refreshed while closed, never refilled as it opens: on the
    /// desktop a menu is a window created as it opens, and one emptied and refilled then shows nothing.
    /// </summary>
    public static MenuItem Top(TopMenu menu)
    {
        var item = new MenuItem { Header = menu.Header, Name = "Menu_" + menu.Id };
        Fill(item, menu);
        return item;
    }

    /// <summary>Fills <paramref name="item"/> with <paramref name="menu"/>'s lines now.</summary>
    public static void Fill(MenuItem item, TopMenu menu)
    {
        item.Items.Clear();
        foreach (var line in Items(menu.Items()))
        {
            item.Items.Add(line);
        }
    }

    private static object Item(MenuEntry entry)
    {
        switch (entry)
        {
            case SeparatorEntry:
                return new Separator();
            case SubmenuEntry submenu:
                var parent = new MenuItem { Header = submenu.Header };
                foreach (var child in Items(submenu.Items))
                {
                    parent.Items.Add(child);
                }
                return parent;
            case CommandEntry command:
                var item = new MenuItem
                {
                    Header = command.Header,
                    Command = command.Command,
                    CommandParameter = command.Parameter,
                    InputGesture = command.Gesture,
                    IsEnabled = command.WhyNot is null,
                };
                if (command.Checked is { } ticked)
                {
                    item.ToggleType = MenuItemToggleType.CheckBox;
                    item.IsChecked = ticked;
                }
                if (command.WhyNot is { Length: > 0 } why)
                {
                    ToolTip.SetTip(item, why);
                    ToolTip.SetShowOnDisabled(item, true);
                    AutomationProperties.SetHelpText(item, why);
                }
                return item;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry, "Not a menu entry.");
        }
    }

    /// <summary>The macOS menu bar: the same menus as native items, worked out as each opens.</summary>
    public static NativeMenu Native(IReadOnlyList<TopMenu> menus)
    {
        var bar = new NativeMenu();
        foreach (var menu in menus)
        {
            var submenu = new NativeMenu();
            submenu.Opening += (_, _) => FillNative(submenu, menu.Items());
            FillNative(submenu, menu.Items());
            bar.Items.Add(new NativeMenuItem(Plain(menu.Header)) { Menu = submenu });
        }
        return bar;
    }

    private static void FillNative(NativeMenu menu, IReadOnlyList<MenuEntry> entries)
    {
        menu.Items.Clear();
        foreach (var entry in Tidy(entries))
        {
            menu.Items.Add(NativeItem(entry));
        }
    }

    private static NativeMenuItemBase NativeItem(MenuEntry entry)
    {
        switch (entry)
        {
            case SeparatorEntry:
                return new NativeMenuItemSeparator();
            case SubmenuEntry submenu:
                var children = new NativeMenu();
                FillNative(children, submenu.Items);
                return new NativeMenuItem(Plain(submenu.Header)) { Menu = children };
            case CommandEntry command:
                var item = new NativeMenuItem(Plain(command.Header))
                {
                    Command = command.Command,
                    CommandParameter = command.Parameter,
                    Gesture = command.Gesture,
                    IsEnabled = command.WhyNot is null,
                    ToolTip = command.WhyNot is { Length: > 0 } why ? why : null,
                };
                if (command.Checked is { } ticked)
                {
                    item.ToggleType = MenuItemToggleType.CheckBox;
                    item.IsChecked = ticked;
                }
                return item;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry, "Not a menu entry.");
        }
    }

    /// <summary>macOS has no access keys: a single underscore goes, a doubled one is a literal.</summary>
    private static string Plain(string header) =>
        header
            .Replace("__", "\u0001", StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("\u0001", "_", StringComparison.Ordinal);

    /// <summary>No separator first, last or twice in a row.</summary>
    private static List<MenuEntry> Tidy(IReadOnlyList<MenuEntry> entries)
    {
        MenuEntry? previous = null;
        var kept = new List<MenuEntry>();
        foreach (var entry in entries)
        {
            if (entry is SeparatorEntry && (previous is null or SeparatorEntry))
            {
                continue;
            }
            kept.Add(entry);
            previous = entry;
        }
        if (kept.Count > 0 && kept[^1] is SeparatorEntry)
        {
            kept.RemoveAt(kept.Count - 1);
        }
        return kept;
    }
}
