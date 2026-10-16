// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Stations;
using AprsMap.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AprsMap.UI.Views;

/// <summary>The Stations panel (docs/features/F1.7-station-list.md).</summary>
public sealed partial class StationsPanel : UserControl
{
    private MainViewModel? _main;
    private bool _selecting;

    /// <summary>Creates the panel.</summary>
    public StationsPanel()
    {
        InitializeComponent();
        StationList.SelectionChanged += OnSelectionChanged;
        StationList.DoubleTapped += (_, _) => CentreSelected();
        StationList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        StationList.PointerEntered += (_, _) => Hold();
        StationList.PointerExited += (_, _) => Hold();
        StationList.GotFocus += (_, _) => Hold();
        StationList.LostFocus += (_, _) => Hold();
        StationList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        CentreItem.Click += (_, _) => CentreSelected();
        FollowItem.Click += (_, _) => FollowSelected();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_main is not null)
        {
            _main.FocusStationSearchRequested -= OnFocusSearch;
            _main.PropertyChanged -= OnMainChanged;
        }
        _main = DataContext as MainViewModel;
        if (_main is not null)
        {
            _main.FocusStationSearchRequested += OnFocusSearch;
            _main.PropertyChanged += OnMainChanged;
        }
    }

    private StationListRow? Selected => StationList.SelectedItem as StationListRow;

    /// <summary>Selecting a row selects the station on the map and opens its details (behavior 7).</summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_selecting && _main is not null && Selected is { } row)
        {
            _main.SelectedStation = row.Name;
        }
    }

    /// <summary>A station selected elsewhere is selected here too, when it's listed.</summary>
    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedStation) || _main is null)
        {
            return;
        }
        _selecting = true;
        StationList.SelectedItem = _main.StationList.Rows.FirstOrDefault(r => r.Name == _main.SelectedStation);
        _selecting = false;
    }

    /// <summary>Enter centres the map on the selected station; L follows it (behaviors 7 and 8).</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            CentreSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            FollowSelected();
            e.Handled = true;
        }
    }

    private void CentreSelected()
    {
        if (_main is not null && Selected is { HasPosition: true } row)
        {
            _main.CentreOn(row.Name);
        }
    }

    private void FollowSelected()
    {
        if (_main is not null && Selected is { HasPosition: true } row)
        {
            _main.SelectedStation = row.Name;
            _main.Details.IsFollowing = true;
        }
    }

    /// <summary>Rows hold their order while the pointer or focus is in the list (behavior 5).</summary>
    private void Hold()
    {
        if (_main is not null)
        {
            _main.StationList.IsHolding = StationList.IsPointerOver || StationList.IsKeyboardFocusWithin;
        }
    }

    /// <summary>The next page loads as the list nears its end (behavior 6).</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (
            e.Source is ScrollViewer viewer
            && _main is not null
            && viewer.Offset.Y + viewer.Viewport.Height * 2 >= viewer.Extent.Height
        )
        {
            _ = _main.StationList.LoadMoreAsync();
        }
    }

    private void OnFocusSearch(object? sender, EventArgs e) => SearchBox.Focus();
}
