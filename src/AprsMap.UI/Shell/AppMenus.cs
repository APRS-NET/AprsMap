// SPDX-License-Identifier: AGPL-3.0-only
using System.Windows.Input;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Shell;

/// <summary>Where the menus are shown (docs/features/F1.29-menus.md, behaviors 4–6).</summary>
public enum MenuStyle
{
    /// <summary>A bar in the window, hidden until Alt or F10 (Windows, Linux).</summary>
    InWindow,

    /// <summary>The system menu bar (macOS).</summary>
    System,

    /// <summary>Submenus of the ☰ button, so Alt stays the browser's (the web app).</summary>
    Hamburger,
}

/// <summary>One line of a menu.</summary>
public abstract record MenuEntry;

/// <summary>A command: its words, what it runs, its shortcut, and whether it's ticked or can run now.</summary>
/// <param name="Id">Stable, for tests and the shortcut list.</param>
/// <param name="Header">The words, with <c>_</c> before the access key.</param>
/// <param name="Command">What it runs.</param>
/// <param name="Parameter">The command's parameter.</param>
/// <param name="Gesture">The shortcut shown beside it.</param>
/// <param name="Checked">Ticked or not; null for a command that isn't a switch.</param>
/// <param name="WhyNot">Why it can't run now, shown greyed out with this as its tooltip (behavior 2); null when it can.</param>
public sealed record CommandEntry(
    string Id,
    string Header,
    ICommand Command,
    object? Parameter = null,
    KeyGesture? Gesture = null,
    bool? Checked = null,
    string? WhyNot = null
) : MenuEntry;

/// <summary>A submenu.</summary>
public sealed record SubmenuEntry(string Header, IReadOnlyList<MenuEntry> Items) : MenuEntry;

/// <summary>A line between groups.</summary>
public sealed record SeparatorEntry : MenuEntry
{
    /// <summary>The one separator.</summary>
    public static SeparatorEntry Instance { get; } = new();
}

/// <summary>A top-level menu, whose lines are worked out each time it opens.</summary>
public sealed record TopMenu(string Id, string Header, Func<IReadOnlyList<MenuEntry>> Items);

/// <summary>What the menus need from the window: things only a view can do.</summary>
public interface IMenuActions
{
    /// <summary>Whether the app can quit itself (the desktop).</summary>
    bool CanQuit { get; }

    /// <summary>Whether the window can go full screen.</summary>
    bool CanFullScreen { get; }

    /// <summary>Whether the window is full screen now.</summary>
    bool IsFullScreen { get; }

    /// <summary>Ends the app.</summary>
    void Quit();

    /// <summary>Turns full screen on or off.</summary>
    void ToggleFullScreen();

    /// <summary>Saves the packet list in a format, where the viewer picks.</summary>
    Task SavePacketsAsync(Packets.Export.PacketExporter exporter);

    /// <summary>Opens a page in the browser.</summary>
    Task OpenUriAsync(Uri uri);
}

/// <summary>
/// Every command's one home (docs/features/F1.29-menus.md, behaviors 1–3): File, View, Station, Tools and Help, worked
/// out from the main view model each time a menu opens, so ticks, greyed states and lists (profiles, sources, panels,
/// layouts) are always current. The menu bar, the macOS menu bar and the ☰ menu are all built from this.
/// </summary>
public sealed class AppMenus(MainViewModel vm, Localizer text, IMenuActions actions)
{
    private static KeyModifiers CommandKey =>
        Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    /// <summary>The shortcut for Settings: Ctrl+, (Cmd+, on macOS).</summary>
    public static KeyGesture SettingsGesture => new(Key.OemComma, CommandKey);

    /// <summary>The shortcut for Find a station: Ctrl+F.</summary>
    public static KeyGesture FindGesture => new(Key.F, CommandKey);

    /// <summary>The shortcut for Next filter: Ctrl+Shift+F (docs/features/F1.42-saved-filters.md, decision 4).</summary>
    public static KeyGesture NextFilterGesture => new(Key.F, CommandKey | KeyModifiers.Shift);

    /// <summary>The shortcut for Quit: Ctrl+Q (Cmd+Q on macOS).</summary>
    public static KeyGesture QuitGesture => new(Key.Q, CommandKey);

    /// <summary>The shortcut for Full screen: F11.</summary>
    public static KeyGesture FullScreenGesture => new(Key.F11);

    /// <summary>The five menus, in order.</summary>
    public IReadOnlyList<TopMenu> Menus =>
        [
            new("file", text["Menu.File"], () => ForViewer(File())),
            new("view", text["Menu.View"], () => ForViewer(View())),
            new("station", text["Menu.Station"], () => ForViewer(Station())),
            new("tools", text["Menu.Tools"], () => ForViewer(Tools())),
            new("help", text["Menu.Help"], Help),
        ];

    /// <summary>
    /// What a gateway viewer who can never act on the station may not do, by command id or id prefix: left out of their
    /// menus, not greyed (docs/features/F1.26-public-console.md, behavior 9).
    /// </summary>
    private static readonly string[] StationOnly =
    [
        "file.profile.",
        "file.edit-profiles",
        "file.export-capture",
        "station.internet",
        "station.aprs-is",
        "station.tiles",
        "station.transmit",
        "station.reconnect-all",
        "station.send-",
        "station.sources",
        "station.beacons",
        "station.console.",
        "station.tnc.",
        "tools.messages",
        "tools.clear-data",
        "tools.setup",
    ];

    private IReadOnlyList<MenuEntry> ForViewer(IReadOnlyList<MenuEntry> entries) =>
        vm.IsViewerOnly ? Without(entries) : entries;

    private static List<MenuEntry> Without(IReadOnlyList<MenuEntry> entries)
    {
        var kept = new List<MenuEntry>();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case CommandEntry c when StationOnly.Any(id => c.Id.StartsWith(id, StringComparison.Ordinal)):
                    continue;
                case SubmenuEntry s:
                    var items = Without(s.Items);
                    if (items.OfType<CommandEntry>().Any() || items.OfType<SubmenuEntry>().Any())
                    {
                        kept.Add(s with { Items = items });
                    }
                    continue;
                default:
                    kept.Add(entry);
                    continue;
            }
        }
        return kept;
    }

    /// <summary>A name shown in a menu as typed: an underscore isn't an access key.</summary>
    internal static string Literal(string name) => name.Replace("_", "__", StringComparison.Ordinal);

    private static CommandEntry Run(
        string id,
        string header,
        Action run,
        KeyGesture? gesture = null,
        bool? ticked = null,
        string? whyNot = null
    ) => new(id, header, new RelayCommand(run), null, gesture, ticked, whyNot);

    private static CommandEntry RunAsync(string id, string header, Func<Task> run, string? whyNot = null) =>
        new(id, header, new AsyncRelayCommand(run), null, null, null, whyNot);

    private List<MenuEntry> File()
    {
        List<MenuEntry> items = [];
        if (vm.Profiles.Count > 0)
        {
            List<MenuEntry> profiles =
            [
                .. vm.Profiles.Select(p =>
                    Run(
                        "file.profile." + p.Id,
                        Literal($"{p.Name} ({p.Callsign})"),
                        () => vm.SelectedProfile = p,
                        ticked: p.Id == vm.SelectedProfile?.Id,
                        whyNot: vm.CanSwitchProfiles || p.Id == vm.SelectedProfile?.Id
                            ? null
                            : text["Menu.Why.ReadOnly"]
                    )
                ),
                SeparatorEntry.Instance,
                RunAsync(
                    "file.edit-profiles",
                    text["Menu.File.EditProfiles"],
                    () => vm.OpenSettingsSectionAsync("profiles")
                ),
            ];
            items.Add(new SubmenuEntry(text["Menu.File.Profile"], profiles));
        }
        items.Add(
            new SubmenuEntry(
                text["Menu.File.SavePackets"],
                [
                    .. Packets.Export.PacketExporter.All.Select(e =>
                        RunAsync(
                            "file.save-packets." + e.Format.ToString().ToLowerInvariant(),
                            text[e.NameKey],
                            () => actions.SavePacketsAsync(e)
                        )
                    ),
                ]
            )
        );
        items.Add(
            new CommandEntry(
                "file.export-capture",
                text["Menu.File.ExportCapture"],
                vm.OpenCapturedPacketsCommand,
                WhyNot: vm.CanClearData ? null : text["Menu.Why.Operator"]
            )
        );
        items.Add(SeparatorEntry.Instance);
        items.Add(
            new CommandEntry(
                "file.settings",
                text["Menu.File.Settings"],
                vm.OpenSettingsCommand,
                Gesture: SettingsGesture
            )
        );
        if (vm.IsSignedIn)
        {
            items.Add(SeparatorEntry.Instance);
            items.Add(Run("file.signed-in", Literal(vm.SignedInText ?? string.Empty), () => { }, whyNot: string.Empty));
            if (vm.Auth is not { ApiKey: true })
            {
                items.Add(new CommandEntry("file.sign-out", text["Auth.SignOut"], vm.SignOutCommand)); // a key has none
            }
        }
        else if (vm.CanSignIn)
        {
            items.Add(SeparatorEntry.Instance);
            items.Add(new CommandEntry("file.sign-in", text["Auth.SignInMenu"], vm.OpenSignInCommand));
        }
        if (actions.CanQuit)
        {
            items.Add(SeparatorEntry.Instance);
            items.Add(Run("file.quit", text["Menu.File.Quit"], actions.Quit, QuitGesture));
        }
        return items;
    }

    private List<MenuEntry> View()
    {
        var closed = vm.Shell.ClosedPanels;
        List<MenuEntry> panels =
        [
            .. PanelKinds.Ordered.Select(kind =>
                Run(
                    "view.panel." + kind,
                    text[PanelKinds.TitleKey(kind)],
                    () => vm.TogglePanel(kind),
                    ticked: !closed.Contains(kind),
                    whyNot: kind == PanelKinds.Map ? text["Menu.Why.MapStays"] : null
                )
            ),
        ];
        List<MenuEntry> layouts =
        [
            .. vm.Layouts.Layouts.Select(l => new CommandEntry(
                "view.layout." + l.Name,
                Literal(l.Name),
                l.ApplyCommand
            )),
        ];
        if (layouts.Count > 0)
        {
            layouts.Add(SeparatorEntry.Instance);
        }
        layouts.Add(Run("view.save-layout", text["Layouts.SaveAs"], vm.OpenSaveLayoutAs));
        layouts.Add(
            RunAsync("view.manage-layouts", text["Menu.View.ManageLayouts"], () => vm.OpenLayoutSettingsAsync())
        );
        List<MenuEntry> items =
        [
            new SubmenuEntry(text["Menu.View.Panels"], panels),
            new SubmenuEntry(text["Menu.View.BesideMap"], BesideMap()),
            Run(
                "view.show-all",
                text["Menu.View.ShowAll"],
                vm.ShowAllPanels,
                whyNot: closed.All(PanelKinds.OnDemand.Contains) ? text["Menu.Why.AllShown"] : null
            ),
            Run("view.reset-layout", text["Shell.Menu.ResetLayout"] + "…", vm.AskResetLayout),
            new SubmenuEntry(text["Menu.View.Layouts"], layouts),
            new SubmenuEntry(text["Menu.View.Filter"], Filters()),
        ];
        if (vm.CanUndoResetLayout)
        {
            items.Insert(3, Run("view.undo-reset", text["Menu.View.UndoReset"], vm.UndoResetLayout));
        }
        if (vm.CanGatherWindows)
        {
            items.Add(new CommandEntry("view.gather", text["Shell.Menu.Gather"], vm.GatherWindowsCommand));
        }
        items.Add(SeparatorEntry.Instance);
        if (actions.CanFullScreen)
        {
            items.Add(
                Run(
                    "view.full-screen",
                    text["Menu.View.FullScreen"],
                    actions.ToggleFullScreen,
                    FullScreenGesture,
                    actions.IsFullScreen
                )
            );
        }
        if (vm.MenuStyle != MenuStyle.System)
        {
            items.Add(
                Run(
                    "view.always-show-menu",
                    text["Menu.View.AlwaysShowMenuBar"],
                    () => vm.AlwaysShowMenuBar = !vm.AlwaysShowMenuBar,
                    ticked: vm.AlwaysShowMenuBar
                )
            );
        }
        return items;
    }

    /// <summary>All, My filter and each saved filter, ticked in use, then Next filter (F1.42 behavior 4).</summary>
    private List<MenuEntry> Filters()
    {
        List<MenuEntry> items =
        [
            .. vm.ViewFilter.Choices.Select(c =>
                (MenuEntry)
                    new CommandEntry(
                        "view.filter." + (c.SavedId ?? c.Kind.ToString()),
                        c.Kind == Stations.FilterChoiceKind.Saved ? Literal(c.Label) : c.Label,
                        c.ChooseCommand,
                        null,
                        null,
                        c.IsChecked,
                        null
                    )
            ),
            SeparatorEntry.Instance,
            new CommandEntry(
                "view.next-filter",
                text["Menu.View.NextFilter"],
                vm.ViewFilter.NextFilterCommand,
                null,
                NextFilterGesture,
                null,
                null
            ),
        ];
        return items;
    }

    /// <summary>Each panel, ticked while it's a tab beside the map (F1.0 behavior 16).</summary>
    private List<MenuEntry> BesideMap()
    {
        var beside = vm.Shell.BesideMap;
        return
        [
            .. PanelKinds
                .Ordered.Where(k => k is not PanelKinds.Map and not PanelKinds.Dashboard)
                .Select(kind =>
                    Run(
                        "view.beside-map." + kind,
                        text[PanelKinds.TitleKey(kind)],
                        () => vm.Shell.SetBesideMap(kind, !beside.Contains(kind)),
                        ticked: beside.Contains(kind)
                    )
                ),
        ];
    }

    private List<MenuEntry> Station()
    {
        var readOnly = vm.CanToggle ? null : text["Menu.Why.ReadOnly"];
        CommandEntry Switch(string id, Toggle toggle, string key, bool on, string? whyNot) =>
            new("station." + id, text[key], vm.ToggleCommand, toggle, null, on, whyNot);
        List<MenuEntry> items =
        [
            Switch("internet", Toggle.Internet, "Toggles.Internet", vm.InternetOn, readOnly),
            Switch(
                "aprs-is",
                Toggle.AprsIs,
                "Toggles.AprsIs",
                vm.AprsIsOn,
                readOnly ?? (vm.InternetOn ? null : text["Menu.Why.InternetOff"])
            ),
            Switch(
                "tiles",
                Toggle.Tiles,
                "Toggles.Tiles",
                vm.TilesOn,
                readOnly ?? (vm.InternetOn ? null : text["Menu.Why.InternetOff"])
            ),
            Switch(
                "transmit",
                Toggle.Transmit,
                "Toggles.Transmit",
                vm.TransmitOn,
                vm.CanTransmit ? null : TransmitWhyNot()
            ),
            SeparatorEntry.Instance,
            RunAsync("station.reconnect-all", text["Menu.Station.ReconnectAll"], vm.ReconnectAllAsync, readOnly),
        ];
        var sendWhyNot =
            !vm.CanTransmit ? TransmitWhyNot()
            : !vm.TransmitOn ? text["Menu.Why.TransmitOff"]
            : null;
        items.Add(
            new CommandEntry(
                "station.send-position",
                text["Transmit.SendPosition"],
                vm.SendPositionCommand,
                WhyNot: sendWhyNot
            )
        );
        List<MenuEntry> sendOn = [];
        if (vm.IsAprsIsTransmitEnabled)
        {
            sendOn.Add(
                new CommandEntry(
                    "station.send-on.aprs-is",
                    text["Toggles.AprsIs"],
                    vm.SendPositionOnAprsIsCommand,
                    WhyNot: sendWhyNot
                )
            );
        }
        sendOn.AddRange(
            vm.SourcePills.Where(p => p.IsTransmitEnabled)
                .Select(p => new CommandEntry(
                    "station.send-on." + p.Id,
                    Literal(p.Name),
                    p.SendPositionCommand,
                    WhyNot: sendWhyNot
                ))
        );
        if (sendOn.Count > 0)
        {
            items.Add(new SubmenuEntry(text["Menu.Station.SendOn"], sendOn));
        }
        items.Add(
            new SubmenuEntry(
                text["Menu.Station.Destination"],
                [
                    Run(
                        "station.destination.set",
                        text["Menu.Station.DestinationSet"],
                        vm.RequestDestination,
                        whyNot: vm.Destination.IsSet ? text["Menu.Why.DestinationSet"] : null
                    ),
                    RunAsync(
                        "station.destination.clear",
                        text["Menu.Station.DestinationClear"],
                        () => vm.Destination.ClearAsync(),
                        vm.Destination.IsSet ? null : text["Menu.Why.NoDestination"]
                    ),
                ]
            )
        );
        items.Add(SeparatorEntry.Instance);
        items.Add(
            RunAsync("station.sources", text["Menu.Station.Sources"], () => vm.OpenSettingsSectionAsync("sources"))
        );
        items.Add(
            RunAsync("station.beacons", text["Menu.Station.Beacons"], () => vm.OpenSettingsSectionAsync("beacons"))
        );
        List<MenuEntry> consoles =
        [
            new CommandEntry(
                "station.console.aprs-is",
                text["Toggles.AprsIs"],
                vm.OpenAprsIsConsoleCommand,
                WhyNot: vm.CanClearData ? null : text["Menu.Why.Operator"]
            ),
            .. vm.SourcePills.Select(p => new CommandEntry(
                "station.console." + p.Id,
                Literal(p.Name),
                p.ConsoleCommand,
                WhyNot: vm.CanClearData ? null : text["Menu.Why.Operator"]
            )),
        ];
        items.Add(new SubmenuEntry(text["Menu.Station.Console"], consoles));
        var typing = vm.SourcePills.Where(p => p.CanType).ToList();
        if (typing.Count > 0)
        {
            items.Add(
                new SubmenuEntry(
                    text["Menu.Station.TncCommands"],
                    [
                        .. typing.Select(p => new CommandEntry(
                            "station.tnc." + p.Id,
                            Literal(p.Name),
                            p.TncCommandsCommand
                        )),
                    ]
                )
            );
        }
        return items;
    }

    private string TransmitWhyNot() =>
        vm.SharedStation && !vm.IsOperator ? text["Menu.Why.Operator"]
        : vm.CanToggle ? text["Menu.Why.ReceiveOnly"]
        : text["Menu.Why.ReadOnly"];

    private IReadOnlyList<MenuEntry> Tools() =>
        [
            Run("tools.find", text["Menu.Tools.Find"], vm.FindStation, FindGesture),
            new CommandEntry("tools.messages", text["Menu.Tools.Messages"], vm.ShowMessagesCommand),
            Run(
                "tools.bulletins",
                text["Menu.Tools.Bulletins"],
                () => vm.TogglePanel(PanelKinds.Bulletins, show: true)
            ),
            SeparatorEntry.Instance,
            Run(
                "tools.clear-data",
                text["Shell.Menu.ClearData"],
                vm.OpenClearData,
                whyNot: vm.CanClearData ? null : text["Menu.Why.ReadOnly"]
            ),
            new CommandEntry(
                "tools.setup",
                text["Menu.Tools.Setup"],
                vm.OpenSetupCommand,
                WhyNot: vm.CanClearData ? null : text["Menu.Why.ReadOnly"]
            ),
            Run("tools.start-over", text["Menu.Tools.StartOver"], vm.OpenStartOver),
        ];

    private IReadOnlyList<MenuEntry> Help()
    {
        var source = new Uri(Brand.SourceUrl.TrimEnd('/') + "/");
        return
        [
            Run("help.shortcuts", text["Menu.Help.Shortcuts"], () => vm.ShowShortcuts(Shortcuts())),
            .. (
                (!vm.SharedStation || vm.IsOperator) && vm.Updates.Status is not { CheckDisabled: true }
                    ? new MenuEntry[]
                    {
                        new CommandEntry("help.check-updates", text["Menu.Help.CheckUpdates"], vm.Updates.CheckCommand),
                    }
                    : []
            ),
            SeparatorEntry.Instance,
            RunAsync(
                "help.docs",
                text["Menu.Help.Docs"],
                () => actions.OpenUriAsync(new Uri(source, "tree/main/docs"))
            ),
            RunAsync(
                "help.report",
                text["Menu.Help.Report"],
                () => actions.OpenUriAsync(new Uri(source, "issues/new"))
            ),
            RunAsync(
                "help.source",
                text["Settings.About.Source"],
                () => actions.OpenUriAsync(new Uri(Brand.SourceUrl))
            ),
            SeparatorEntry.Instance,
            RunAsync("help.about", text["Menu.Help.About"], vm.OpenAboutAsync),
        ];
    }

    /// <summary>Every shortcut, menu by menu, from the same commands (behavior 17), and the few that aren't in a menu.</summary>
    public IReadOnlyList<ShortcutRow> Shortcuts()
    {
        var rows = new List<ShortcutRow>();
        foreach (var menu in Menus.Where(m => m.Id != "help"))
        {
            foreach (var command in WithGestures(menu.Items()))
            {
                rows.Add(new ShortcutRow(Plain(menu.Header), Plain(command.Header), Keys(command.Gesture!)));
            }
        }
        var other = text["Menu.Shortcuts.Other"];
        if (vm.MenuStyle == MenuStyle.InWindow)
        {
            rows.Add(new ShortcutRow(other, text["Menu.Shortcuts.ShowMenus"], "Alt, F10"));
            rows.Add(new ShortcutRow(other, text["Menu.Shortcuts.OpenMenu"], text["Menu.Shortcuts.AltLetter"]));
        }
        rows.Add(new ShortcutRow(other, text["Menu.Shortcuts.Connection"], Keys(new KeyGesture(Key.T, CommandKey))));
        rows.Add(
            new ShortcutRow(
                other,
                text["Menu.Shortcuts.SwitchPanel"],
                Keys(new KeyGesture(Key.Tab, KeyModifiers.Control))
            )
        );
        return rows;
    }

    private static IEnumerable<CommandEntry> WithGestures(IEnumerable<MenuEntry> entries) =>
        entries.SelectMany(e =>
            e switch
            {
                CommandEntry { Gesture: not null } c => [c],
                SubmenuEntry s => WithGestures(s.Items),
                _ => Enumerable.Empty<CommandEntry>(),
            }
        );

    private static string Keys(KeyGesture gesture) =>
        gesture.ToString("p", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A header as words: no access-key marks, no trailing ellipsis.</summary>
    internal static string Plain(string header) =>
        header
            .Replace("__", "\u0001", StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("\u0001", "_", StringComparison.Ordinal)
            .TrimEnd('…');
}
