// SPDX-License-Identifier: AGPL-3.0-only
using System.ComponentModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace AprsMap.UI.Map;

/// <summary>
/// An extra map view following one station (docs/features/F1.16-extra-map-views.md): its own <see cref="MapView"/> and
/// zoom, the main map's tiles and stations. It doesn't report its view for the filter or remember it: the station it
/// follows is in the filter instead (behavior 6). Panning by hand stops following here only (behavior 4).
/// </summary>
public sealed partial class WatchPanel : UserControl, IDisposable
{
    /// <summary>How much the view shows around the station when it first finds it.</summary>
    public const double SpanKm = 20;

    private readonly MapView _map = new();
    private readonly MainViewModel? _main;
    private readonly Localizer _text = Localizer.Instance;
    private bool _placed;

    /// <summary>For the XAML previewer.</summary>
    public WatchPanel()
        : this(null, string.Empty) { }

    /// <summary>A view of <paramref name="main"/>'s stations following <paramref name="callsign"/>.</summary>
    public WatchPanel(MainViewModel? main, string callsign)
    {
        InitializeComponent();
        Callsign = callsign;
        _main = main;
        MapHost.Child = _map;
        ZoomInButton.Click += (_, _) => _map.ZoomIn();
        ZoomOutButton.Click += (_, _) => _map.ZoomOut();
        FollowAgainButton.Click += (_, _) => FollowAgain();
        _map.PannedByHand += (_, _) =>
        {
            IsFollowing = false;
            ShowChip();
        };
        _map.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0)
            {
                Centre();
            }
        };
        _map.KeyDown += OnMapKeyDown;
        _map.SelectionChanged += (_, name) =>
        {
            if (_main is not null)
            {
                _main.SelectedStation = name;
            }
        };
        _map.Followed = callsign; // its track in the followed style (behavior 7)
        ShowChip();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _text.LanguageChanged += OnLanguageChanged;
        if (_main?.Map is { } state)
        {
            _main.StationsChanged += OnStationsChanged;
            state.PropertyChanged += OnStateChanged;
            _map.SetStations(_main.Stations); // what changed while it was away
            _map.SetTracks(_main.Tracks);
            ShowProvider();
            _map.DistanceUnit = state.DistanceUnit;
            _map.Units = state.Units;
        }
        Centre();
        ShowChip();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe(); // closed, or moving to another dock or window: it listens again when it's back
    }

    private void Unsubscribe()
    {
        _text.LanguageChanged -= OnLanguageChanged;
        if (_main?.Map is { } state)
        {
            _main.StationsChanged -= OnStationsChanged;
            state.PropertyChanged -= OnStateChanged;
        }
    }

    /// <summary>The station this view follows.</summary>
    public string Callsign { get; }

    /// <summary>Whether the view keeps the station centred.</summary>
    public bool IsFollowing { get; private set; } = true;

    /// <summary>The map surface, for tests.</summary>
    internal MapView View => _map;

    /// <inheritdoc />
    public void Dispose()
    {
        Unsubscribe();
        _map.Dispose();
    }

    private StationState? Station =>
        _main?.Stations.FirstOrDefault(s => string.Equals(s.Name, Callsign, StringComparison.OrdinalIgnoreCase));

    private void OnStationsChanged(object? sender, StationBatch batch)
    {
        if (ReferenceEquals(batch, MainViewModel.Snapshot) && _main is not null)
        {
            _map.SetStations(_main.Stations);
            _map.SetTracks(_main.Tracks);
        }
        else
        {
            _map.Apply(batch);
        }
        if (IsFollowing)
        {
            Centre();
        }
        ShowChip();
    }

    /// <summary>Keeps the station in the middle, at the view's zoom; the first time, shows the area around it.</summary>
    private void Centre()
    {
        if (!IsFollowing || Station is not { } station || _map.Bounds.Width <= 0)
        {
            return;
        }
        if (_placed && _map.View is var (_, _, resolution))
        {
            _map.GoTo(station.Latitude, station.Longitude, resolution);
        }
        else
        {
            _map.ShowAround(station.Latitude, station.Longitude, SpanKm);
            _placed = true;
        }
    }

    private void FollowAgain()
    {
        IsFollowing = true;
        Centre();
        ShowChip();
    }

    /// <summary>Escape stops following here, L follows again (as on the main map, F1.7 behavior 8).</summary>
    private void OnMapKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        if (e.Key == Key.Escape && IsFollowing)
        {
            IsFollowing = false;
            ShowChip();
            e.Handled = true;
        }
        else if (e.Key == Key.L && !IsFollowing)
        {
            FollowAgain();
            e.Handled = true;
        }
    }

    /// <summary>"Following NZ0P-9", "NZ0P-9 hasn't been heard yet", or the name and Follow again.</summary>
    private void ShowChip()
    {
        var args = new Dictionary<string, object?> { ["name"] = Callsign };
        WatchText.Text =
            Station is null ? _text.Format("Watch.NotHeard", args)
            : IsFollowing ? _text.Format("Watch.Following", args)
            : Callsign;
        FollowAgainButton.IsVisible = !IsFollowing && Station is not null;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => ShowChip();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MapViewModel.Provider):
            case nameof(MapViewModel.IsLoaded):
                ShowProvider();
                break;
            case nameof(MapViewModel.DistanceUnit):
                _map.DistanceUnit = _main!.Map!.DistanceUnit;
                break;
            case nameof(MapViewModel.Units):
                _map.Units = _main!.Map!.Units;
                break;
        }
    }

    /// <summary>The main map's tiles (behavior 4). Attribution shows on the main map.</summary>
    private void ShowProvider()
    {
        if (_main?.Map is { Tiles: { } tiles, Provider: { } provider })
        {
            _map.ShowTiles(tiles, provider);
        }
    }
}
