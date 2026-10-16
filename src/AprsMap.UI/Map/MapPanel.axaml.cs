// SPDX-License-Identifier: AGPL-3.0-only
using System.ComponentModel;
using AprsMap.Contracts;
using AprsMap.UI.Stations;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AprsMap.UI.Map;

/// <summary>
/// The map panel (docs/features/F1.4-map.md): the <see cref="MapView"/>, Home and zoom buttons, and the provider's
/// attribution. Its DataContext is the main view model; the map's state is its <see cref="MainViewModel.Map"/>.
/// </summary>
public sealed partial class MapPanel : UserControl, IDisposable
{
    private readonly MapView _map = new();
    private MapViewModel? _state;
    private MainViewModel? _main;
    private bool _placed;
    private (double Latitude, double Longitude)? _menuAt;

    /// <summary>Creates the panel.</summary>
    public MapPanel()
    {
        InitializeComponent();
        MapHost.Child = _map;
        ZoomInButton.Click += (_, _) => _map.ZoomIn();
        ZoomOutButton.Click += (_, _) => _map.ZoomOut();
        HomeButton.Click += (_, _) => GoHome();
        FitButton.Click += (_, _) => _map.Fit();
        _map.SelectionChanged += (_, name) =>
        {
            if (_main is not null)
            {
                _main.SelectedStation = name;
            }
        };
        _map.ViewChanged += (_, _) =>
        {
            if (_placed && _map.View is var (lat, lon, resolution))
            {
                _state?.Remember(new MapViewState(lat, lon, resolution));
                // Half the view's diagonal on the ground, for the filter (F1.2 behavior 1.2).
                var diagonal = Math.Sqrt(Math.Pow(_map.Bounds.Width, 2) + Math.Pow(_map.Bounds.Height, 2));
                var radiusKm = diagonal / 2 * resolution * Math.Cos(lat * Math.PI / 180) / 1000;
                _main?.MapMoved(lat, lon, radiusKm);
            }
        };
        _map.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0)
            {
                Place();
            }
        };
        _map.KeyDown += OnMapKeyDown;
        _map.ContextRequested += OnContextRequested;
        _map.PannedByHand += (_, _) => StopFollowing();
        FilterButton.Flyout!.Opened += (_, _) => _ = _main?.ViewFilter.RefreshFacetsAsync(); // what's been heard, now
        _map.TrackPointPicked += (_, point) => _main?.SelectTrackPoint(point.Name, point.Time);
        // The RF network (F1.21 behavior 5): loaded when chosen, and every half minute while it shows.
        _map.PathsChanged += (_, _) => _ = LoadLinksAsync();
        _linkTimer.Tick += (_, _) => _ = LoadLinksAsync();
        _linkTimer.Start();
        StopFollowingButton.Click += (_, _) => StopFollowing();
        SetMyLocationItem.Click += (_, _) =>
        {
            if (_menuAt is var (lat, lon) && _main is not null)
            {
                _ = _main.SetMyLocationAsync(lat, lon);
            }
        };
    }

    /// <summary>Releases the map's tiles.</summary>
    public void Dispose()
    {
        _linkTimer.Stop();
        _map.Dispose();
    }

    /// <summary>The map surface, for tests.</summary>
    internal MapView View => _map;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_state is not null)
        {
            _state.PropertyChanged -= OnStateChanged;
        }
        if (_main is not null)
        {
            _main.StationsChanged -= OnStationsChanged;
            _main.PropertyChanged -= OnMainChanged;
            _main.Details.CentreRequested -= OnCentreRequested;
            _main.Details.PropertyChanged -= OnDetailsChanged;
            _main.CentreOnStationRequested -= OnCentreOnStation;
        }
        _main = DataContext as MainViewModel;
        if (_main is not null)
        {
            _main.StationsChanged += OnStationsChanged;
            _main.PropertyChanged += OnMainChanged;
            _main.Details.CentreRequested += OnCentreRequested;
            _main.Details.PropertyChanged += OnDetailsChanged;
            _main.CentreOnStationRequested += OnCentreOnStation;
            _map.SetStations(_main.Stations);
            _map.SetTracks(_main.Tracks);
            _map.Route = _main.PickedRoute;
            _map.SetFilterAreas(_main.FilterAreas);
            ShowFollowed();
            _map.Select(_main.SelectedStation);
        }
        _state = _main?.Map;
        if (_state is not null)
        {
            _state.PropertyChanged += OnStateChanged;
            ShowProvider();
            ShowHome();
            _map.DistanceUnit = _state.DistanceUnit;
            _map.Units = _state.Units;
            Place();
        }
    }

    /// <summary>Puts the map at its opening position once (behavior 2): the last view, the station, or the world.</summary>
    private void Place()
    {
        if (_placed || _state is null || !_state.IsLoaded || _map.Bounds.Width <= 0)
        {
            return;
        }
        _placed = true;
        if (_state.SavedView is { } view)
        {
            _map.GoTo(view.Latitude, view.Longitude, view.Resolution);
        }
        else if (_state.Home is var (lat, lon))
        {
            _map.ShowAround(lat, lon, MapViewModel.HomeSpanKm);
        }
        else
        {
            _map.ShowWorld();
        }
    }

    private void GoHome()
    {
        if (_state?.Home is var (lat, lon))
        {
            _map.ShowAround(lat, lon, MapViewModel.HomeSpanKm);
        }
    }

    private void OnMapKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && _main is not null)
        {
            _main.FocusStationSearch(); // Ctrl+F finds a station (F1.7 behavior 3)
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        if (_main is not null && FollowKeys(e.Key, _main))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.H && _state?.HasHome == true)
        {
            GoHome(); // H returns home (behavior 3)
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            _map.Fit(); // F fits every station (behavior 4)
            e.Handled = true;
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MapViewModel.Provider):
                ShowProvider();
                break;
            case nameof(MapViewModel.Home):
            case nameof(MapViewModel.HomeName):
                ShowHome();
                break;
            case nameof(MapViewModel.DistanceUnit):
                _map.DistanceUnit = _state!.DistanceUnit;
                break;
            case nameof(MapViewModel.Units):
                _map.Units = _state!.Units;
                break;
            case nameof(MapViewModel.IsLoaded):
                ShowProvider();
                Place();
                break;
        }
    }

    /// <summary>
    /// <c>/</c> finds a station, <c>L</c> follows the selected one, <c>G</c> goes to the last selected station or home,
    /// and Escape stops following (F1.7 behaviors 3, 8 and 9).
    /// </summary>
    private bool FollowKeys(Key key, MainViewModel main)
    {
        switch (key)
        {
            case Key.Oem2 or Key.Divide:
                main.FocusStationSearch();
                return true;
            case Key.L when main.SelectedStation is not null:
                main.Details.IsFollowing = !main.Details.IsFollowing;
                return true;
            case Key.G:
                if ((main.SelectedStation ?? main.LastSelectedStation) is { } name && Find(name) is not null)
                {
                    main.CentreOn(name);
                }
                else
                {
                    GoHome();
                }
                return true;
            case Key.Escape when main.Details.IsFollowing:
                StopFollowing();
                return true;
            default:
                return false;
        }
    }

    private StationState? Find(string name) => _main?.Stations.FirstOrDefault(s => s.Name == name);

    /// <summary>Centres on a station from the list (F1.7 behavior 7), keeping the zoom.</summary>
    private void OnCentreOnStation(object? sender, string name)
    {
        if (Find(name) is { } station)
        {
            OnCentreRequested(sender, (station.Latitude, station.Longitude));
        }
    }

    private readonly DispatcherTimer _linkTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private async Task LoadLinksAsync()
    {
        if (_map.Paths == Appearance.MapPaths.Off || (!_map.ShowAllLinks && _map.Selected is null) || _main is null)
        {
            _map.Links = [];
            return;
        }
        _map.Links = await _main.LoadNetworkLinksAsync();
    }

    /// <summary>The followed station's track is drawn in its own style (F1.5 behavior 6).</summary>
    private void OnDetailsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StationDetailsViewModel.FollowingText))
        {
            ShowFollowed();
        }
    }

    private void ShowFollowed() =>
        _map.Followed = _main?.Details is { IsFollowing: true } details ? details.Name : null;

    private void StopFollowing()
    {
        if (_main is not null)
        {
            _main.Details.IsFollowing = false;
        }
    }

    private void ShowProvider()
    {
        if (_state is { Tiles: { } tiles, Provider: { } provider })
        {
            _map.ShowTiles(tiles, provider);
            AttributionLink.Content = provider.Attribution;
            AttributionLink.NavigateUri = Uri.TryCreate(provider.AttributionUrl, UriKind.Absolute, out var uri)
                ? uri
                : null;
            AttributionBar.IsVisible = true;
        }
        else
        {
            AttributionBar.IsVisible = false;
        }
    }

    /// <summary>A station selected elsewhere (the packet list, say) is selected on the map too.</summary>
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedStation) && _main is not null)
        {
            _map.Select(_main.SelectedStation);
            _ = LoadLinksAsync(); // the newly selected station's connections, fresh
        }
        else if (e.PropertyName == nameof(MainViewModel.PickedRoute) && _main is not null)
        {
            _map.Route = _main.PickedRoute;
        }
        else if (e.PropertyName == nameof(MainViewModel.FilterAreas) && _main is not null)
        {
            _map.SetFilterAreas(_main.FilterAreas);
        }
    }

    /// <summary>Home is offered, and the house drawn, at my location (behavior 3; F1.13 behavior 3).</summary>
    private void ShowHome()
    {
        HomeButton.IsVisible = _state?.HasHome == true;
        _map.ShowMe(_state?.Home?.Latitude, _state?.Home?.Longitude, _state?.HomeName);
    }

    /// <summary>
    /// Right-click, long-press or the context-menu key offers Set my location here (F1.13 behavior 2) for the place
    /// pointed at, or the map's centre from the keyboard; not on a read-only gateway.
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_main is not { CanSetMyLocation: true })
        {
            return;
        }
        var at = e.TryGetPosition(_map, out var point)
            ? point
            : new Point(_map.Bounds.Width / 2, _map.Bounds.Height / 2);
        _menuAt = _map.PlaceAt(at);
        MapMenu.Open(_map);
        e.Handled = true;
    }

    /// <summary>Centre and Follow in the Details panel (F1.6 behavior 4): the map moves, keeping its zoom.</summary>
    private void OnCentreRequested(object? sender, (double Latitude, double Longitude) at)
    {
        if (_map.View is var (_, _, resolution))
        {
            _map.GoTo(at.Latitude, at.Longitude, resolution);
        }
    }

    private void OnStationsChanged(object? sender, StationBatch batch)
    {
        if (ReferenceEquals(batch, MainViewModel.Snapshot) && _main is not null)
        {
            _map.SetStations(_main.Stations); // replaced as a whole
            _map.SetTracks(_main.Tracks);
        }
        else
        {
            _map.Apply(batch);
        }
    }
}
