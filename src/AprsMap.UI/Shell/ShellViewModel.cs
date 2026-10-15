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
        Factory.DockableClosed += (_, _) => OnChanged();
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
        Layout = Factory.CreateLayout();
        Factory.InitLayout(Layout);
        _text.LanguageChanged += (_, _) => Retitle();
        Retitle();
    }

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
        return LayoutRestore.Restored;
    }

    /// <summary>The stored form of the current layout, with the main window's place where there is one.</summary>
    public string Save(MainWindowPlacement? mainWindow = null)
    {
        SyncOpenWindows(Layout);
        return new LayoutFile([.. PanelKinds.All.Select(p => p.Kind)], mainWindow, Layout).Write();
    }

    /// <summary>Back to the default layout (behavior 10).</summary>
    public void Reset()
    {
        var layout = Factory.CreateLayout();
        Factory.InitLayout(layout);
        CloseWindows(Layout);
        Layout = layout;
        Retitle();
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

    /// <summary>Tells the shell the user changed something Dock doesn't report, such as dragging a splitter.</summary>
    public void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

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

        var known = PanelKinds.All.Select(p => p.Kind).ToHashSet(StringComparer.Ordinal);
        // Removed without collapsing, so a default dock stays for new panels; docks left empty collapse at the end.
        foreach (var unknown in all.OfType<PanelTool>().Where(p => !known.Contains(p.Kind)).ToList())
        {
            Factory.RemoveDockable(unknown, collapse: false);
        }

        // Panels added since the layout was saved go to their default dock, if it is still there.
        var present = Dockables(layout).OfType<PanelTool>().Select(p => p.Kind).ToHashSet(StringComparer.Ordinal);
        foreach (var (kind, dockId) in PanelKinds.All)
        {
            if (present.Contains(kind) || file.Panels.Contains(kind))
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
