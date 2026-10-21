// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.ViewModels;

/// <summary>What the menus need from the main view model (docs/features/F1.29-menus.md).</summary>
public sealed partial class MainViewModel
{
    /// <summary>Where the menus are shown: a hidden bar, the system menu bar, or under ☰ (behaviors 4–6).</summary>
    public MenuStyle MenuStyle { get; init; } = MenuStyle.InWindow;

    /// <summary>Whether the in-window menu bar stays shown (behavior 7): the viewer's own Appearance choice.</summary>
    public bool AlwaysShowMenuBar
    {
        get => _appearance.Current.AlwaysShowMenuBar;
        set
        {
            if (value != AlwaysShowMenuBar)
            {
                _appearance.Apply(_appearance.Current with { AlwaysShowMenuBar = value });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Whether the menu bar is shown now: always, or revealed by Alt or F10 until a command is chosen (behavior 4).</summary>
    [ObservableProperty]
    public partial bool MenuBarRevealed { get; set; }

    /// <summary>Shows a panel, or closes it when it's shown and <paramref name="show"/> is null (View › Panels, behavior 12).</summary>
    internal void TogglePanel(string kind, bool? show = null)
    {
        var shown = !Shell.ClosedPanels.Contains(kind);
        if (show ?? !shown)
        {
            Shell.Show(kind);
        }
        else if (shown)
        {
            _togglingPanel = true; // closed from the menu: it knows where to get it back
            try
            {
                Shell.Close(kind);
            }
            finally
            {
                _togglingPanel = false;
            }
        }
    }

    /// <summary>Shows every closed panel where it was, without moving the open ones (behavior 13).</summary>
    internal void ShowAllPanels()
    {
        foreach (var kind in Shell.ClosedPanels.Where(k => !PanelKinds.OnDemand.Contains(k))) // not the GPS (F3.12)
        {
            Shell.Show(kind);
        }
    }

    /// <summary>Opens Settings at one section.</summary>
    internal async Task OpenSettingsSectionAsync(string id)
    {
        await OpenSettingsAsync();
        Settings?.ShowSection(id);
    }

    /// <summary>Reconnects APRS-IS and every other source that can be (Station › Reconnect all).</summary>
    internal async Task ReconnectAllAsync()
    {
        if (ReconnectCommand.CanExecute(null))
        {
            await ReconnectAsync();
        }
        foreach (var pill in SourcePills.Where(p => p.ReconnectCommand.CanExecute(null)).ToList())
        {
            await pill.ReconnectCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Asks the map for a destination to be typed (Station › Destination › Set…).</summary>
    public event EventHandler? DestinationRequested;

    /// <summary>Shows the map and opens its destination box.</summary>
    internal void RequestDestination()
    {
        Shell.ShowMap();
        DestinationRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the Stations panel and puts the keyboard in its search (Tools › Find a station…, Ctrl+F).</summary>
    internal void FindStation()
    {
        Shell.Show(PanelKinds.Stations);
        FocusStationSearch();
    }
}
