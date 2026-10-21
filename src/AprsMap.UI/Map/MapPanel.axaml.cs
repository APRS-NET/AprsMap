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
        // Clear stations asks first, with the count now; either answer closes the question.
        var question = ClearStationsButton.Flyout!;
        question.Opened += (_, _) => _main?.AskClearStations();
        // Closed after the click has run Clear: closing it first takes the button out of the tree, and its command
        // binding with it, before the click gets to run the command.
        ConfirmClearStationsButton.Click += (_, _) => Dispatcher.UIThread.Post(question.Hide);
        KeepStationsButton.Click += (_, _) => question.Hide();
        _linkTimer.Tick += (_, _) => _ = LoadLinksAsync();
        _linkTimer.Start();
        StopFollowingButton.Click += (_, _) => StopFollowing();
        SetDestinationItem.Click += (_, _) =>
        {
            if (_menuAt is var (lat, lon) && _main is not null)
            {
                _ = _main.Destination.SetAsync(lat, lon);
            }
        };
        DestinationChipButton.Click += (_, _) =>
        {
            if (_main?.Destination.Point is { } point && _map.View is var (_, _, resolution))
            {
                _map.GoTo(point.Latitude, point.Longitude, resolution); // behavior 2: centres on the destination
            }
        };
        SetMyLocationItem.Click += (_, _) =>
        {
            if (_menuAt is var (lat, lon) && _main is not null)
            {
                _ = _main.SetMyLocationAsync(lat, lon);
            }
        };
        PlaceObjectItem.Click += (_, _) =>
        {
            if (_menuAt is var (lat, lon) && _main is not null)
            {
                _ = _main.PlaceObjectAtAsync(lat, lon); // F2.7 behavior 1
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
            _main.DestinationRequested -= OnDestinationRequested;
            _main.Destination.PropertyChanged -= OnDestinationChanged;
        }
        _main = DataContext as MainViewModel;
        if (_main is not null)
        {
            _main.StationsChanged += OnStationsChanged;
            _main.PropertyChanged += OnMainChanged;
            _main.Details.CentreRequested += OnCentreRequested;
            _main.Details.PropertyChanged += OnDetailsChanged;
            _main.CentreOnStationRequested += OnCentreOnStation;
            _main.DestinationRequested += OnDestinationRequested;
            _main.Destination.PropertyChanged += OnDestinationChanged;
            ShowDestination();
            _map.SetStations(_main.Stations);
            _map.SetTracks(_main.Tracks);
            _map.Route = _main.PickedRoute;
            _map.RoutesOut = _main.RoutesOut;
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
            case Key.W when main.SelectedStation is not null:
                main.WatchSelectedCommand.Execute(null); // Watch in a view (F1.16 behavior 1)
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
        else if (e.PropertyName == nameof(MainViewModel.RoutesOut) && _main is not null)
        {
            _map.RoutesOut = _main.RoutesOut;
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

    /// <summary>The destination's flag and line follow the view model (F1.18 behavior 3).</summary>
    private void OnDestinationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DestinationViewModel.Point) or nameof(DestinationViewModel.Line))
        {
            ShowDestination();
        }
    }

    private void ShowDestination()
    {
        if (_main is not null)
        {
            _map.SetDestination(_main.Destination.Point, _main.Destination.Line);
        }
    }

    /// <summary>
    /// Right-click, long-press or the context-menu key offers, for the place pointed at (or the map's centre from the
    /// keyboard), Set destination here (F1.18 behavior 1), which is each viewer's own, and Set my location here (F1.13
    /// behavior 2), not on a read-only gateway.
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_main is null)
        {
            return;
        }
        SetMyLocationItem.IsVisible = _main.CanSetMyLocation;
        PlaceObjectItem.IsVisible = _main.CanPlaceObjects;
        MoveObjectItem.IsVisible = _main.CanPlaceObjects && _main.LiveObjects.Count > 0;
        MoveObjectItem.ItemsSource = _main
            .LiveObjects.Select(placed =>
            {
                var item = new MenuItem { Header = placed.Name };
                item.Click += (_, _) =>
                {
                    if (_menuAt is var (lat, lon))
                    {
                        _ = _main.MoveObjectAsync(placed.Id, lat, lon); // F2.7 behavior 4
                    }
                };
                return item;
            })
            .ToList();
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

    /// <summary>Station › Destination › Set… (F1.29): the destination box, as its button opens it.</summary>
    private void OnDestinationRequested(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (TypeDestinationButton.IsEffectivelyVisible)
            {
                TypeDestinationButton.Flyout?.ShowAt(TypeDestinationButton);
            }
        });
}
