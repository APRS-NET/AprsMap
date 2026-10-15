// SPDX-License-Identifier: AGPL-3.0-only
using System.ComponentModel;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
    }

    /// <summary>Releases the map's tiles.</summary>
    public void Dispose() => _map.Dispose();

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
        }
        _main = DataContext as MainViewModel;
        if (_main is not null)
        {
            _main.StationsChanged += OnStationsChanged;
            OnStationsChanged(_main, EventArgs.Empty);
        }
        _state = _main?.Map;
        if (_state is not null)
        {
            _state.PropertyChanged += OnStateChanged;
            ShowProvider();
            ShowHome();
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
        if (e.KeyModifiers != KeyModifiers.None)
        {
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
                ShowHome();
                break;
            case nameof(MapViewModel.IsLoaded):
                ShowProvider();
                Place();
                break;
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

    private void ShowHome() => HomeButton.IsVisible = _state?.HasHome == true;

    private void OnStationsChanged(object? sender, EventArgs e)
    {
        if (_main is not null)
        {
            _map.SetStations(_main.Stations);
        }
    }
}
