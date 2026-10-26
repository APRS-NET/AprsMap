// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace AprsMap.UI.Shell;

/// <summary>How a saved layout was restored.</summary>
public enum LayoutRestore
{
    /// <summary>There was none: the default layout is shown.</summary>
    Default,

    /// <summary>The saved layout is shown.</summary>
    Restored,

    /// <summary>It couldn't be read: the default is shown, and the app says so once (behavior 10).</summary>
    Unreadable,
}

/// <summary>
/// The shell's layout (docs/features/F1.0-shell-and-docking.md): the map in the centre and panels around it. Restores
/// a saved layout, fitting floating windows to the screens that exist; saves the arrangement; resets to the default.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Localizer _text;

    /// <summary>Creates the shell with the default layout.</summary>
    /// <param name="context">What every panel's view binds to: the main view model.</param>
    /// <param name="text">For the panels' titles, which follow the language.</param>
    /// <param name="managedWindows">Float panels inside the app (the browser) rather than as OS windows.</param>
    public ShellViewModel(object context, Localizer text, bool managedWindows)
    {
        _text = text;
        Factory = new ShellFactory(context, managedWindows);
        // Dock's events for changes the user makes; each one saves the layout after a pause.
        Factory.ActiveDockableChanged += (_, _) => OnChanged();
        Factory.DockableClosed += (_, e) =>
        {
            var kind = e.Dockable switch
            {
                PanelTool panel => panel.Kind,
                DashboardDocument => PanelKinds.Dashboard,
                _ => null,
            };
            if (kind is not null)
            {
                PanelClosed?.Invoke(this, kind);
            }
            if (e.Dockable is WatchDocument)
            {
                WatchedChanged?.Invoke(this, EventArgs.Empty); // its station leaves the filter (F1.16 behavior 6)
            }
            OnChanged();
        };
        Factory.DockableDocked += (_, _) => OnChanged();
        Factory.DockableHidden += (_, _) => OnChanged();
        Factory.DockableMoved += (_, _) => OnChanged();
        Factory.DockablePinned += (_, _) => OnChanged();
        Factory.DockableRestored += (_, _) => OnChanged();
        Factory.DockableSwapped += (_, _) => OnChanged();
        Factory.DockableUndocked += (_, _) => OnChanged();
        Factory.DockableUnpinned += (_, _) => OnChanged();
        Factory.WindowClosed += (_, _) => OnChanged();
        Factory.WindowMoveDragEnd += (_, _) => OnChanged();
        Factory.WindowOpened += (_, _) => OnChanged();
        Factory.Mover = MoveTo;
        Factory.Returner = DockBesideMap;
        Layout = Factory.CreateLayout();
        Factory.InitLayout(Layout);
        _text.LanguageChanged += (_, _) => Retitle();
        Retitle();
    }

    /// <summary>The most extra map views, besides the main map (F1.16 decision 1).</summary>
    public const int MaxWatchViews = 3;

    /// <summary>The id of the dock extra map views open in, beside the map.</summary>
    public const string WatchDock = "watch";

    /// <summary>Raised when an extra map view opens or closes, or a layout brings others.</summary>
    public event EventHandler? WatchedChanged;

    /// <summary>The stations the extra map views follow (F1.16).</summary>
    public IReadOnlyList<string> Watched =>
        [
            .. Dockables(Layout)
                .OfType<WatchDocument>()
                .Select(d => d.Callsign)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

    /// <summary>
    /// Watch in a view (F1.16 behavior 1): brings the view following <paramref name="callsign"/> to the front, or opens
    /// one beside the map. False, with nothing opened, when <see cref="MaxWatchViews"/> are open already.
    /// </summary>
    public bool Watch(string callsign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);
        var open = Dockables(Layout).OfType<WatchDocument>().ToList();
        if (
            open.FirstOrDefault(d => string.Equals(d.Callsign, callsign, StringComparison.OrdinalIgnoreCase)) is
            { } shown
        )
        {
            if (shown.Owner is IDock owner)
            {
                owner.ActiveDockable = shown;
            }
            return true;
        }
        if (open.Count >= MaxWatchViews)
        {
            return false;
        }
        var view = new WatchDocument
        {
            Id = "watch:" + callsign,
            Callsign = callsign,
            Title = callsign,
            CanClose = true,
            CanFloat = true,
            CanPin = false,
        };
        if (Dockables(Layout).OfType<IDocumentDock>().FirstOrDefault(d => d.Id == WatchDock) is { } dock)
        {
            Factory.AddDockable(dock, view);
            dock.ActiveDockable = view;
        }
        else
        {
            // Beside the map, splitting its space, so both show (behavior 2).
            var created = new Dock.Model.Mvvm.Controls.DocumentDock
            {
                Id = WatchDock,
                Proportion = 0.4,
                CanCreateDocument = false,
                VisibleDockables = Factory.CreateList<IDockable>(view),
                ActiveDockable = view,
            };
            Factory.SplitToDock(Centre(Layout), created, DockOperation.Right);
        }
        Factory.InitLayout(Layout);
        Retitle();
        WatchedChanged?.Invoke(this, EventArgs.Empty);
        OnChanged();
        return true;
    }

    /// <summary>Raised when a panel is really closed (its ×, or Close), with its kind; not when it moves.</summary>
    public event EventHandler<string>? PanelClosed;

    /// <summary>Builds layouts and attaches the view model to panels.</summary>
    public ShellFactory Factory { get; }

    /// <summary>The current layout.</summary>
    [ObservableProperty]
    public partial IRootDock Layout { get; private set; }

    /// <summary>Raised when the user changes the layout, so it can be saved.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Shows <paramref name="saved"/>, the stored text, or the default when there is none or it can't be read. Floating
    /// windows are fitted to <paramref name="screens"/> before any opens (behavior 12); a panel this version doesn't
    /// know is dropped, and one it knows that the saved layout didn't is added in its default place (behavior 11).
    /// </summary>
    public LayoutRestore Restore(string? saved, IReadOnlyList<ScreenArea> screens)
    {
        if (string.IsNullOrWhiteSpace(saved))
        {
            return LayoutRestore.Default;
        }
        if (LayoutFile.Read(saved) is not { } file || !Normalize(file))
        {
            Reset();
            return LayoutRestore.Unreadable;
        }
        FitWindows(file.Dock, screens);
        Layout = file.Dock;
        Retitle();
        WatchedChanged?.Invoke(this, EventArgs.Empty); // views saved with the layout (F1.16 behavior 9)
        return LayoutRestore.Restored;
    }

    /// <summary>
    /// Puts a saved layout on screen now (docs/features/F1.23-saved-layouts.md, behavior 1): the floating windows open
    /// close, and the saved one is restored as at start-up, fitted to <paramref name="screens"/>.
    /// </summary>
    public LayoutRestore Apply(string saved, IReadOnlyList<ScreenArea> screens)
    {
        if (LayoutFile.Read(saved) is null)
        {
            return LayoutRestore.Unreadable;
        }
        CloseWindows(Layout);
        var result = Restore(saved, screens);
        OnChanged();
        return result;
    }

    /// <summary>The stored form of the current layout, with the main window's place where there is one.</summary>
    public string Save(MainWindowPlacement? mainWindow = null)
    {
        SyncOpenWindows(Layout);
        return new LayoutFile(
            [.. PanelKinds.All.Select(p => p.Kind), PanelKinds.Dashboard],
            mainWindow,
            Layout
        ).Write();
    }

    /// <summary>Back to the default layout (behavior 10).</summary>
    /// <summary>
    /// Shows <paramref name="kind"/> alone, for a panel popped out of another tab (docs/features/F1.24-web-pop-out.md,
    /// behavior 2). It isn't saved: the tab it came from keeps the layout.
    /// </summary>
    public void ShowOnly(string kind)
    {
        Factory.Mover = null; // there's nowhere to move the only panel: the panel menu's Move items do nothing
        var layout = Factory.CreatePopOutLayout(kind);
        Factory.InitLayout(layout);
        Layout = layout;
        Retitle();
    }

    public void Reset()
    {
        _lastDock.Clear();
        _lastSide.Clear();
        var layout = Factory.CreateLayout();
        Factory.InitLayout(layout);
        CloseWindows(Layout);
        Layout = layout;
        Retitle();
        WatchedChanged?.Invoke(this, EventArgs.Empty);
        OnChanged();
    }

    /// <summary>Moves open floating windows onto the screens that exist now (behavior 13).</summary>
    public void FitOpenWindows(IReadOnlyList<ScreenArea> screens)
    {
        foreach (var window in Layout.Windows ?? [])
        {
            if (window.Host is not { } host)
            {
                continue;
            }
            host.GetPosition(out var x, out var y);
            host.GetSize(out var width, out var height);
            var (position, size) = WindowPlacement.Fit(new((int)x, (int)y), new(width, height), screens);
            if (position.X != (int)x || position.Y != (int)y)
            {
                host.SetPosition(position.X, position.Y);
            }
            if (size.Width < width || size.Height < height)
            {
                host.SetSize(size.Width, size.Height);
            }
        }
    }

    /// <summary>The panels that are closed, the dashboard first, so the app menu can show them again (behavior 4).</summary>
    public IReadOnlyList<string> ClosedPanels =>
        [
            .. Dockables(Layout).OfType<DashboardDocument>().Any() ? [] : new[] { PanelKinds.Dashboard },
            .. PanelKinds
                .All.Select(p => p.Kind)
                .Where(kind => !Dockables(Layout).OfType<PanelTool>().Any(p => p.Kind == kind)),
        ];

    /// <summary>
    /// Moves a panel to the dock with <paramref name="dockId"/> (left, right or bottom), from wherever it is: docked,
    /// auto-hidden or floating. A dock that collapsed when it emptied is created again beside the map.
    /// </summary>
    public void MoveTo(IDockable panel, string dockId)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (IsPinned(panel))
        {
            Factory.PinDockable(panel); // back from the edge first: it toggles
        }
        var target = Target(dockId);
        if (panel.Owner is IDock from && !ReferenceEquals(from, target))
        {
            Factory.MoveDockable(from, target, panel, null);
        }
        target.ActiveDockable = panel;
        Factory.InitLayout(Layout);
        OnChanged();
    }

    /// <summary>The side dock each panel was last seen in, so showing it again puts it back there (F1.29 behavior 12).</summary>
    private readonly Dictionary<string, string> _lastDock = new(StringComparer.Ordinal);

    private void RememberDocks()
    {
        foreach (var panel in Dockables(Layout).OfType<PanelTool>())
        {
            if (
                panel.Owner is IDock
                {
                    Id: ShellFactory.LeftDock
                        or ShellFactory.RightDock
                        or ShellFactory.BottomDock
                        or ShellFactory.CentreDock,
                } dock
            )
            {
                _lastDock[panel.Kind] = dock.Id;
                if (dock.Id != ShellFactory.CentreDock)
                {
                    _lastSide[panel.Kind] = dock.Id;
                }
            }
        }
    }

    /// <summary>Closes a panel, as its × does (F1.29 behavior 12); the map stays.</summary>
    public void Close(string kind)
    {
        RememberDocks();
        IDockable? panel =
            kind == PanelKinds.Dashboard
                ? Dockables(Layout).OfType<DashboardDocument>().FirstOrDefault()
                : Dockables(Layout).OfType<PanelTool>().FirstOrDefault(p => p.Kind == kind);
        if (panel is { CanClose: true })
        {
            Factory.CloseDockable(panel);
            OnChanged();
        }
    }

    /// <summary>Shows a closed panel again, where it was last or in its default dock (behavior 4, F1.29 behavior 12).</summary>
    public void Show(string kind)
    {
        if (kind == PanelKinds.Dashboard)
        {
            ShowDashboard();
            return;
        }
        if (Dockables(Layout).OfType<PanelTool>().FirstOrDefault(p => p.Kind == kind) is { } open)
        {
            if (open.Owner is IDock owner)
            {
                owner.ActiveDockable = open;
            }
            return;
        }
        var dockId = _lastDock.GetValueOrDefault(kind) ?? PanelKinds.All.First(p => p.Kind == kind).Dock;
        var target = Target(dockId);
        var panel = ShellFactory.CreatePanel(kind);
        Factory.AddDockable(target, panel);
        target.ActiveDockable = panel;
        Factory.InitLayout(Layout);
        Retitle();
        OnChanged();
    }

    /// <summary>Brings the map's tab to the front (a dashboard row applied a filter to it, F1.22 behavior 12).</summary>
    public void ShowMap()
    {
        if (Dockables(Layout).OfType<MapDocument>().FirstOrDefault() is { Owner: IDock owner } map)
        {
            owner.ActiveDockable = map;
        }
    }

    /// <summary>The dashboard shown and brought to the front, as a tab beside the map if it was closed (F1.22 behavior 1).</summary>
    private void ShowDashboard()
    {
        if (Dockables(Layout).OfType<DashboardDocument>().FirstOrDefault() is not { } dashboard)
        {
            dashboard = ShellFactory.CreateDashboard();
            Factory.AddDockable(Centre(Layout), dashboard);
            Factory.InitLayout(Layout);
            Retitle();
        }
        if (dashboard.Owner is IDock owner)
        {
            owner.ActiveDockable = dashboard;
        }
        OnChanged();
    }

    /// <summary>Puts <paramref name="dashboard"/> back as a tab beside the map, from a floating window.</summary>
    public void DockBesideMap(IDockable dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        var centre = Centre(Layout);
        if (dashboard.Owner is IDock from && !ReferenceEquals(from, centre))
        {
            Factory.MoveDockable(from, centre, dashboard, null);
        }
        centre.ActiveDockable = dashboard;
        Factory.InitLayout(Layout);
        OnChanged();
    }

    private static IDock Centre(IDockable layout) =>
        Dockables(layout).OfType<IDock>().First(d => d.Id == ShellFactory.CentreDock);

    /// <summary>
    /// Moves every floating window onto <paramref name="screen"/>, the main window's screen (behavior 14). A window
    /// already reachable there stays.
    /// </summary>
    public void Gather(ScreenArea screen) => FitOpenWindows([screen]);

    /// <summary>Tells the shell the user changed something Dock doesn't report, such as dragging a splitter.</summary>
    public void OnChanged()
    {
        RememberDocks();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every panel and the map in a layout, including those in floating windows.</summary>
    internal static IEnumerable<IDockable> Dockables(IDockable root)
    {
        yield return root;
        if (root is IDock dock)
        {
            foreach (var child in dock.VisibleDockables ?? [])
            {
                foreach (var inner in Dockables(child))
                {
                    yield return inner;
                }
            }
            foreach (var hidden in (dock as IRootDock)?.HiddenDockables ?? [])
            {
                yield return hidden;
            }
            foreach (var pinned in (dock as IRootDock)?.LeftPinnedDockables ?? [])
            {
                yield return pinned;
            }
            foreach (var pinned in (dock as IRootDock)?.RightPinnedDockables ?? [])
            {
                yield return pinned;
            }
            foreach (var pinned in (dock as IRootDock)?.TopPinnedDockables ?? [])
            {
                yield return pinned;
            }
            foreach (var pinned in (dock as IRootDock)?.BottomPinnedDockables ?? [])
            {
                yield return pinned;
            }
        }
        if (root is IRootDock rootDock)
        {
            foreach (var window in rootDock.Windows ?? [])
            {
                if (window.Layout is { } nested)
                {
                    foreach (var inner in Dockables(nested))
                    {
                        yield return inner;
                    }
                }
            }
        }
    }

    private bool IsPinned(IDockable panel) =>
        new[]
        {
            Layout.LeftPinnedDockables,
            Layout.RightPinnedDockables,
            Layout.TopPinnedDockables,
            Layout.BottomPinnedDockables,
        }.Any(list => list?.Contains(panel) == true);

    /// <summary>The left, right or bottom dock, created beside the map if it has collapsed.</summary>
    /// <summary>The dock for <paramref name="dockId"/>: the map's for <see cref="ShellFactory.CentreDock"/>, else a side.</summary>
    private IDock Target(string dockId) => dockId == ShellFactory.CentreDock ? Centre(Layout) : SideDock(dockId);

    /// <summary>The side each panel was last in, for moving it back from beside the map.</summary>
    private readonly Dictionary<string, string> _lastSide = new(StringComparer.Ordinal);

    /// <summary>
    /// Moves a panel beside the map, or back to the side it came from (View › Beside the map, F1.0 behavior 16); a closed
    /// panel is shown first.
    /// </summary>
    public void SetBesideMap(string kind, bool beside)
    {
        if (kind is PanelKinds.Map or PanelKinds.Dashboard)
        {
            return;
        }
        Show(kind);
        RememberDocks();
        if (Dockables(Layout).OfType<PanelTool>().FirstOrDefault(p => p.Kind == kind) is not { } panel)
        {
            return;
        }
        MoveTo(
            panel,
            beside
                ? ShellFactory.CentreDock
                : _lastSide.GetValueOrDefault(kind) ?? PanelKinds.All.First(p => p.Kind == kind).Dock
        );
    }

    /// <summary>The panels that are tabs beside the map (F1.0 behavior 16).</summary>
    public IReadOnlyList<string> BesideMap => [.. Dockables(Centre(Layout)).OfType<PanelTool>().Select(p => p.Kind)];

    private IDock SideDock(string dockId)
    {
        if (Dockables(Layout).OfType<IToolDock>().FirstOrDefault(d => d.Id == dockId) is { } existing)
        {
            return existing;
        }
        var (alignment, operation, proportion) = dockId switch
        {
            ShellFactory.LeftDock => (Alignment.Left, DockOperation.Left, 0.2),
            ShellFactory.RightDock => (Alignment.Right, DockOperation.Right, 0.28),
            _ => (Alignment.Bottom, DockOperation.Bottom, 0.3),
        };
        var dock = new Dock.Model.Mvvm.Controls.ToolDock
        {
            Id = dockId,
            Alignment = alignment,
            Proportion = proportion,
            VisibleDockables = Factory.CreateList<IDockable>(),
        };
        var centre = Dockables(Layout).OfType<IDock>().First(d => d.Id == ShellFactory.CentreDock);
        Factory.SplitToDock(centre, dock, operation);
        return dock;
    }

    /// <summary>Makes a loaded layout one this version can show; false when it can't (no map).</summary>
    private bool Normalize(LayoutFile file)
    {
        var layout = file.Dock;
        Factory.InitLayout(layout);
        var all = Dockables(layout).ToList();
        if (all.OfType<MapDocument>().SingleOrDefault() is not { } map)
        {
            return false;
        }
        // A hand-edited file mustn't make the map closable or floatable.
        map.CanClose = false;
        map.CanFloat = false;
        map.CanPin = false;

        foreach (var panel in all.OfType<PanelTool>())
        {
            panel.CanDockAsDocument = false;
        }
        var known = PanelKinds.All.Select(p => p.Kind).ToHashSet(StringComparer.Ordinal);
        // Removed without collapsing, so a default dock stays for new panels; docks left empty collapse at the end.
        foreach (var unknown in all.OfType<PanelTool>().Where(p => !known.Contains(p.Kind)).ToList())
        {
            Factory.RemoveDockable(unknown, collapse: false);
        }

        // The dashboard, new since a layout saved before it, joins the map as a tab behind it.
        if (!all.OfType<DashboardDocument>().Any() && !file.Panels.Contains(PanelKinds.Dashboard))
        {
            Factory.AddDockable(Centre(layout), ShellFactory.CreateDashboard());
        }

        // Panels added since the layout was saved go to their default dock, if it is still there.
        var present = Dockables(layout).OfType<PanelTool>().Select(p => p.Kind).ToHashSet(StringComparer.Ordinal);
        foreach (var (kind, dockId) in PanelKinds.All)
        {
            if (present.Contains(kind) || file.Panels.Contains(kind) || PanelKinds.OnDemand.Contains(kind))
            {
                continue;
            }
            if (Dockables(layout).OfType<IDock>().FirstOrDefault(d => d.Id == dockId) is { } dock)
            {
                var panel = ShellFactory.CreatePanel(kind);
                Factory.AddDockable(dock, panel);
                dock.ActiveDockable ??= panel;
            }
        }
        foreach (
            var empty in Dockables(layout)
                .OfType<IToolDock>()
                .Where(d => d.VisibleDockables is not { Count: > 0 })
                .ToList()
        )
        {
            Factory.CollapseDock(empty);
        }
        Factory.InitLayout(layout);
        return true;
    }

    private void Retitle()
    {
        foreach (var dockable in Dockables(Layout))
        {
            switch (dockable)
            {
                case PanelTool panel:
                    panel.Title = _text[PanelKinds.TitleKey(panel.Kind)];
                    break;
                case MapDocument map:
                    map.Title = _text[PanelKinds.TitleKey(PanelKinds.Map)];
                    break;
                case DashboardDocument dashboard:
                    dashboard.Title = _text[PanelKinds.TitleKey(PanelKinds.Dashboard)];
                    break;
                case WatchDocument watch:
                    watch.Title = watch.Callsign; // the station's name in any language
                    break;
            }
        }
    }

    private static void FitWindows(IRootDock layout, IReadOnlyList<ScreenArea> screens)
    {
        foreach (var window in layout.Windows ?? [])
        {
            var (position, size) = WindowPlacement.Fit(
                new((int)window.X, (int)window.Y),
                new(window.Width, window.Height),
                screens
            );
            window.X = position.X;
            window.Y = position.Y;
            window.Width = size.Width;
            window.Height = size.Height;
            if (window.Layout is { } nested)
            {
                FitWindows(nested, screens);
            }
        }
    }

    // Floating windows report their place to the model when they close, not as they move, so save where they are now.
    private static void SyncOpenWindows(IRootDock layout)
    {
        foreach (var window in layout.Windows ?? [])
        {
            if (window.Host is { } host)
            {
                host.GetPosition(out var x, out var y);
                host.GetSize(out var width, out var height);
                window.X = x;
                window.Y = y;
                window.Width = width;
                window.Height = height;
            }
        }
    }

    private static void CloseWindows(IRootDock layout)
    {
        foreach (var window in (layout.Windows ?? []).ToList())
        {
            window.Exit();
        }
    }
}
