// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsNet.Packets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Manipulations;
using Mapsui.Projections;
using Mapsui.Rendering.Skia;
using Mapsui.Tiling.Layers;
using SkiaSharp;

namespace AprsMap.UI.Map;

/// <summary>
/// The map surface (docs/features/F1.4-map.md): Mapsui's core for projection, tiles and navigation, drawn on Avalonia's
/// Skia canvas by this control, because Mapsui.Avalonia doesn't support Avalonia 12 (spike 1). Tiles come only through
/// the app's tile source. While tiles load, the theme's ground colour shows, never a checkerboard (behavior 12), and in
/// a dark theme the tiles are dimmed (behavior 13).
/// </summary>
public sealed class MapView : Control, IDisposable
{
    /// <summary>A tenth of the view: what an arrow key pans (behavior 5).</summary>
    private const double ArrowPanShare = 0.1;

    /// <summary>Within this distance of a symbol's centre, beyond its radius, a click or hover counts as on it.</summary>
    private const double HitSlop = 4;

    /// <summary>A press that moves less than this is a click, not a drag.</summary>
    private const double ClickDistance = 4;

    /// <summary>A single station is shown at street level (about zoom 16), not as close as the map goes (behavior 4).</summary>
    private const double SingleStationResolution = 2.4;

    private readonly Mapsui.Map _map = new();
    private readonly MapRenderer _renderer = new();
    private readonly StationLayer _stations = new();
    private readonly TrackLayer _tracks = new();
    private SKColor _trackColour = SKColors.Transparent;
    private SKColor _followedColour = SKColors.Transparent;
    private SKColor _casingColour = SKColors.Transparent;
    private SKColor _textColour = SKColors.Transparent;
    private readonly SKColor[] _areaColours = new SKColor[8];
    private readonly Dictionary<string, MapLabel> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StationMark> _byName = new(StringComparer.Ordinal);
    private IReadOnlyList<StationMark> _marks = [];
    private IReadOnlyList<StationMark> _drawn = [];
    private StationMark? _me;
    private Point? _pressedAt;
    private double _wheel;
    private double _pinch;

    /// <summary>How much pinch makes one zoom step.</summary>
    private const double PinchStep = 0.25;
    private double _symbolDip = 24;
    private double _labelSize = 12;
    private double _labelScale = 1;

    // Theme tokens, set as soon as the map joins the visual tree; nothing is drawn with them before.
    private SKColor _dot = SKColors.Transparent;
    private IBrush? _labelBrush;
    private IBrush? _haloBrush;
    private IPen? _ringPen;
    private IPen? _filterPen;
    private ImmutablePen? _destinationPen;
    private ImmutablePen? _destinationPolePen;
    private ImmutableSolidColorBrush? _flagBrush;
    private IReadOnlyList<FilterArea> _filterAreas = [];
    private TileLayer? _tiles;
    private Point? _dragFrom;
    private SKColor _ground = SKColors.Transparent;
    private bool _refreshQueued;

    /// <summary>Creates the map, focusable so the keyboard can pan and zoom it (behavior 7).</summary>
    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnPinch);
        _map.RefreshGraphicsRequest += (_, _) => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        // Tiles arrive after the frame that asked for them: draw again when they do (the browser draws only on demand).
        _map.DataChanged += (_, _) => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        _map.Navigator.ViewportChanged += (_, _) => ViewChanged?.Invoke(this, EventArgs.Empty);
        this.GetResourceObservable("Color.Ground").Subscribe(new GroundObserver(this));
        Watch("Map.SymbolSize", v => _symbolDip = v is double d ? d : 24);
        Watch("Map.Dimming", v => Dimming = v is double d and >= 0 ? d : null);
        Watch("Map.ShowLabels", v => ShowLabels = v is not false);
        Watch(
            "Map.LabelSize",
            v =>
            {
                _labelSize = v is double d ? d : 12;
                ClearLabels();
            }
        );
        Watch(
            "Color.Text",
            v =>
            {
                _labelBrush = v is Color c ? new ImmutableSolidColorBrush(c) : null;
                ClearLabels();
            }
        );
        Watch(
            "Color.Surface",
            v =>
            {
                _haloBrush = v is Color c ? new ImmutableSolidColorBrush(c) : null;
                ClearLabels();
            }
        );
        Watch(
            "Color.Accent",
            v =>
            {
                _ringPen = v is Color c ? new ImmutablePen(new ImmutableSolidColorBrush(c), 2) : null;
                _followedColour = v is Color a ? new SKColor(a.R, a.G, a.B, a.A) : SKColors.Transparent;
                _filterPen = v is Color f
                    ? new ImmutablePen(new ImmutableSolidColorBrush(f), 1.5, new ImmutableDashStyle([4, 4], 0))
                    : null;
                _destinationPen = v is Color d
                    ? new ImmutablePen(new ImmutableSolidColorBrush(d), 2, new ImmutableDashStyle([3, 3], 0))
                    : null;
                _destinationPolePen = v is Color p ? new ImmutablePen(new ImmutableSolidColorBrush(p), 2) : null;
                _flagBrush = v is Color b ? new ImmutableSolidColorBrush(b) : null;
            }
        );
        Watch("Map.ShowFilterCircles", v => ShowFilterCircles = v is true);
        Watch("Map.Tracks", v => Tracks = v is MapTracks tracks ? tracks : MapTracks.All);
        Watch("Map.Paths", v => Paths = v is MapPaths paths ? paths : MapPaths.SelectedStation);
        Watch(
            "Map.WeatherLabels",
            v => WeatherLabels = v is MapWeatherLabels labels ? labels : MapWeatherLabels.Temperature
        );
        Watch("Map.Wind", v => Wind = v is MapWind wind ? wind : MapWind.Off);
        Watch("Map.ShowAreas", v => ShowAreas = v is not false);
        Watch("Map.ShowAllLinks", v => ShowAllLinks = v is true);
        for (var i = 0; i < _areaColours.Length; i++)
        {
            var token = i;
            Watch(
                $"Color.Area{token + 1}",
                v =>
                {
                    _areaColours[token] = v is Color c ? new SKColor(c.R, c.G, c.B, c.A) : SKColors.Transparent;
                    InvalidateVisual();
                }
            );
        }
        Watch(
            "Color.Text",
            v =>
            {
                _textColour = v is Color c ? new SKColor(c.R, c.G, c.B, c.A) : SKColors.Transparent;
                InvalidateVisual();
            }
        );
        Watch(
            "Color.Track",
            v =>
            {
                _trackColour = v is Color c ? new SKColor(c.R, c.G, c.B, c.A) : SKColors.Transparent;
                InvalidateVisual();
            }
        );
        Watch(
            "Color.Surface",
            v =>
            {
                _casingColour = v is Color c ? new SKColor(c.R, c.G, c.B, c.A) : SKColors.Transparent;
                InvalidateVisual();
            }
        );
        Watch("Color.TextMuted", v => _dot = v is Color c ? new SKColor(c.R, c.G, c.B, c.A) : SKColors.Transparent);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>
    /// Releases the map's tiles. Not done on detaching: Dock moves views between parents, so the owner (the map panel)
    /// disposes the map when the panel itself goes.
    /// </summary>
    public void Dispose()
    {
        DisposeTiles();
        _stations.Dispose();
        _map.Dispose();
        ClearLabels();
    }

    /// <summary>Raised when the map is dragged by hand: following a station stops (F1.7 behavior 8).</summary>
    public event EventHandler? PannedByHand;

    /// <summary>Raised when a station is selected by click or tap, or the selection cleared on the background (behavior 18).</summary>
    public event EventHandler<string?>? SelectionChanged;

    /// <summary>The selected station's name, ringed in the accent colour; null for none.</summary>
    public string? Selected { get; private set; }

    /// <summary>The clock for "heard … ago" in the hover text.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>The unit the hover text gives distances from my location in (F1.13 behavior 4).</summary>
    public DistanceUnit DistanceUnit { get; set; }

    /// <summary>Whether the filter's range parts are drawn as dashed circles (F1.2 behavior 6).</summary>
    public bool ShowFilterCircles
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>The filter's range parts drawn, for tests.</summary>
    internal IReadOnlyList<FilterArea> FilterAreas => _filterAreas;

    private GeoPoint? _destination;
    private IReadOnlyList<GeoPoint> _destinationLine = [];

    /// <summary>The destination's flag and great-circle line (docs/features/F1.18-destination.md, behavior 3).</summary>
    public void SetDestination(GeoPoint? at, IReadOnlyList<GeoPoint> line)
    {
        _destination = at;
        _destinationLine = line;
        InvalidateVisual();
    }

    /// <summary>The destination drawn, for tests.</summary>
    internal (GeoPoint? At, int LinePoints) Destination => (_destination, _destinationLine.Count);

    /// <summary>
    /// The line from my location to the destination, dashed in the accent colour, and a flag at the destination. Drawn
    /// on the UI thread with the pens resolved from the theme, so the render thread reads nothing styled.
    /// </summary>
    private void DrawDestination(DrawingContext context, Viewport viewport)
    {
        if (_destination is not { } at || _destinationPen is null || _flagBrush is null)
        {
            return;
        }
        if (_destinationLine.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var previous = (Point?)null;
                foreach (var point in _destinationLine)
                {
                    var (x, y) = SphericalMercator.FromLonLat(point.Longitude, point.Latitude);
                    var s = ToScreen(viewport, new StationMark(string.Empty, x, y, null, false, default));
                    var here = new Point(s.X, s.Y);
                    // A line crossing the antimeridian jumps across the map: it's broken there rather than drawn back.
                    if (previous is not { } before || Math.Abs(here.X - before.X) > Bounds.Width)
                    {
                        if (previous is not null)
                        {
                            g.EndFigure(false);
                        }
                        g.BeginFigure(here, false);
                    }
                    else
                    {
                        g.LineTo(here);
                    }
                    previous = here;
                }
                g.EndFigure(false);
            }
            context.DrawGeometry(null, _destinationPen, geometry);
        }
        var (fx, fy) = SphericalMercator.FromLonLat(at.Longitude, at.Latitude);
        var flagAt = ToScreen(viewport, new StationMark(string.Empty, fx, fy, null, false, default));
        var size = _symbolDip * 0.6;
        var pole = new Point(flagAt.X, flagAt.Y);
        var flag = new StreamGeometry();
        using (var g = flag.Open())
        {
            g.BeginFigure(new Point(pole.X, pole.Y - size * 1.6), true);
            g.LineTo(new Point(pole.X + size, pole.Y - size * 1.3));
            g.LineTo(new Point(pole.X, pole.Y - size));
            g.EndFigure(true);
        }
        context.DrawLine(_destinationPolePen!, pole, new Point(pole.X, pole.Y - size * 1.6));
        context.DrawGeometry(_flagBrush, null, flag);
    }

    /// <summary>The APRS-IS filter's range parts, for <see cref="ShowFilterCircles"/>.</summary>
    public void SetFilterAreas(IReadOnlyList<FilterArea>? areas)
    {
        _filterAreas = areas ?? [];
        InvalidateVisual();
    }

    /// <summary>Which tracks are drawn (F1.5 behavior 8).</summary>
    public MapTracks Tracks
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = MapTracks.All;

    /// <summary>The station the map follows, whose track is drawn thicker in the accent colour (F1.5 behavior 6).</summary>
    public string? Followed
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>The station's units, for weather labels (F1.10 behavior 6).</summary>
    public UnitSettings Units
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = UnitSettings.Metric;

    /// <summary>What weather stations' labels add under the callsign (F1.10 behavior 4).</summary>
    public MapWeatherLabels WeatherLabels
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = MapWeatherLabels.Temperature;

    /// <summary>Whether weather stations get wind barbs (F1.10 behavior 5).</summary>
    public MapWind Wind
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>The label text for <paramref name="mark"/>: its name, and a weather line under it (F1.10 behavior 4).</summary>
    internal string LabelText(StationMark mark) =>
        WeatherLabels != MapWeatherLabels.Off
        && Localization.WeatherText.MapLine(
            mark.Weather,
            WeatherLabels == MapWeatherLabels.TemperatureAndWind,
            Units,
            Localization.Localizer.Instance
        )
            is { } line
            ? mark.Name + "\n" + line
            : mark.Name;

    /// <summary>This frame's wind barbs for the stations on screen; null with barbs off.</summary>
    internal WindBarbs? BuildBarbs(IReadOnlyList<StationMark> marks, SKPoint[] screen)
    {
        if (Wind != MapWind.Barbs)
        {
            return null;
        }
        var barbs = new WindBarbs();
        var visible = new Rect(Bounds.Size).Inflate(_symbolDip * 2);
        for (var i = 0; i < marks.Count; i++)
        {
            if (
                marks[i].Weather is { WindDirection: { } from, WindSpeedMs: { } speed }
                && visible.Contains(new Point(screen[i].X, screen[i].Y))
            )
            {
                barbs.Add(screen[i], from, speed, (float)(_symbolDip / 2));
            }
        }
        return barbs;
    }

    /// <summary>Whether area objects are drawn as their shapes (F1.12 behavior 7).</summary>
    public bool ShowAreas
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = true;

    /// <summary>This frame's area shapes; null with them off.</summary>
    internal AreaFrame? BuildAreas(Viewport viewport, IReadOnlyList<StationMark> marks) =>
        ShowAreas
            ? AreaFrame.Build(
                marks,
                (x, y) => ToScreen(viewport, new StationMark(string.Empty, x, y, null, false, default)),
                viewport.Resolution,
                [.. _areaColours]
            )
            : null;

    /// <summary>
    /// Each signpost's text on a small sign over its symbol (F1.12 behavior 6), in the theme's text and surface colours.
    /// </summary>
    private void DrawSignposts(DrawingContext context, IReadOnlyList<StationMark> marks, SKPoint[] screen)
    {
        if (_labelBrush is null || _haloBrush is null)
        {
            return;
        }
        var visible = new Rect(Bounds.Size);
        var pen = new Pen(_labelBrush, 1);
        for (var i = 0; i < marks.Count; i++)
        {
            if (marks[i].Signpost is not { Length: > 0 } sign || !visible.Contains(new Point(screen[i].X, screen[i].Y)))
            {
                continue;
            }
            var text = new FormattedText(
                sign,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.Bold),
                _labelSize,
                _labelBrush
            );
            var width = Math.Max(text.Width + 6, _symbolDip * 0.8);
            var box = new Rect(screen[i].X - width / 2, screen[i].Y - text.Height / 2 - 2, width, text.Height + 4);
            context.DrawRectangle(_haloBrush, pen, box, 3, 3);
            context.DrawText(text, new Point(box.X + (width - text.Width) / 2, box.Y + 2));
        }
    }

    /// <summary>Which packet paths are drawn (F1.21 behavior 6).</summary>
    public MapPaths Paths
    {
        get;
        set
        {
            var changed = field != value;
            field = value;
            InvalidateVisual();
            if (changed)
            {
                PathsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    } = MapPaths.SelectedStation;

    /// <summary>
    /// Raised when <see cref="Paths"/> or <see cref="ShowAllLinks"/> changes: the network's links are loaded unless
    /// paths are off.
    /// </summary>
    public event EventHandler? PathsChanged;

    /// <summary>Whether every RF link is drawn rather than only the selected station's (F1.21 behavior 5).</summary>
    public bool ShowAllLinks
    {
        get;
        set
        {
            var changed = field != value;
            field = value;
            InvalidateVisual();
            if (changed)
            {
                PathsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// The route of a packet or track point picked, drawn with <see cref="Paths"/> on (F1.21 behavior 2); a selected
    /// station shows its links instead.
    /// </summary>
    public PacketRoute? Route
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// This station's last position's routes out, drawn while no packet's route is picked (F3.3 behavior 3): solid to
    /// each digipeater that repeated it, dotted on to each IGate that gated it.
    /// </summary>
    public IReadOnlyList<PacketRoute> RoutesOut
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = [];

    /// <summary>Raised when a point of the selected station's track is clicked, with the station and the point's time.</summary>
    public event EventHandler<(string Name, DateTimeOffset Time)>? TrackPointPicked;

    /// <summary>
    /// The RF network's links (F1.21 behavior 5): the selected station's are drawn, or all of them with
    /// <see cref="ShowAllLinks"/>.
    /// </summary>
    public IReadOnlyList<NetworkLink> Links
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = [];

    /// <summary>This frame's network links: all of them, the selected station's, or null with none to draw.</summary>
    internal LinkFrame? BuildLinks(Viewport viewport)
    {
        if (Paths == MapPaths.Off || Links.Count == 0 || (!ShowAllLinks && Selected is null))
        {
            return null;
        }
        var links = ShowAllLinks ? Links : [.. Links.Where(l => l.From == Selected || l.To == Selected)];
        return links.Count == 0
            ? null
            : LinkFrame.Build(
                links,
                (x, y) => ToScreen(viewport, new StationMark(string.Empty, x, y, null, false, default)),
                _trackColour,
                _casingColour
            );
    }

    /// <summary>This frame's route drawing; null with nothing to draw.</summary>
    internal RouteFrame? BuildRoute(Viewport viewport) =>
        Paths != MapPaths.SelectedStation
            ? null
            : RouteFrame.Build(
                Route is { } picked ? [picked] : RoutesOut,
                (x, y) => ToScreen(viewport, new StationMark(string.Empty, x, y, null, false, default)),
                (float)(_symbolDip / 2),
                _followedColour,
                _casingColour
            );

    /// <summary>The tracks held, for tests.</summary>
    internal TrackLayer TrackLayer => _tracks;

    /// <summary>Every station's track, replacing those held (a panel shown again, or a resubscription).</summary>
    public void SetTracks(IEnumerable<KeyValuePair<string, IReadOnlyList<TrackPoint>>> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        _tracks.Clear();
        foreach (var (name, points) in tracks)
        {
            _tracks.Apply(new TrackUpdate(name, points, Reset: true));
        }
        InvalidateVisual();
    }

    /// <summary>Whether callsigns are drawn under the symbols, where they fit (behavior 17).</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>The stations drawn, as the station stream has them (behavior 14), replacing any drawn.</summary>
    public void SetStations(IEnumerable<StationState> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        _byName.Clear();
        Apply(new StationBatch([.. stations], []));
    }

    /// <summary>
    /// Applies one batch from the station stream: only the stations in it are projected again, so a busy map with a
    /// few changes per batch costs little.
    /// </summary>
    public void Apply(StationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var station in batch.Changed)
        {
            _byName[station.Name] = StationMark.From(station);
        }
        foreach (var name in batch.Removed)
        {
            _byName.Remove(name);
            _tracks.Remove(name);
        }
        foreach (var update in batch.Tracks ?? [])
        {
            _tracks.Apply(update);
        }
        _marks = [.. _byName.Values];
        _drawn = _me is null ? _marks : [.. _marks, _me];
        if (Selected is { } selected && !_byName.ContainsKey(selected))
        {
            Select(null);
        }
        InvalidateVisual();
    }

    /// <summary>
    /// Draws my location as a house labelled <paramref name="name"/> (F1.13 behavior 3), or nothing with null
    /// coordinates. It never fades, its label is placed first, and it can't be selected: it isn't a heard station.
    /// </summary>
    public void ShowMe(double? latitude, double? longitude, string? name)
    {
        if (latitude is { } lat && longitude is { } lon)
        {
            var (x, y) = SphericalMercator.FromLonLat(lon, lat);
            _me = new StationMark(name ?? string.Empty, x, y, House, false, DateTimeOffset.MaxValue);
        }
        else
        {
            _me = null;
        }
        _drawn = _me is null ? _marks : [.. _marks, _me];
        InvalidateVisual();
    }

    /// <summary>My location as drawn, for tests; null when not shown.</summary>
    internal StationMark? Me => _me;

    /// <summary>The house drawn at my location, APRS <c>/-</c>.</summary>
    private static readonly AprsSymbol House = AprsSymbol.TryCreate('/', '-', out var house) ? house : default;

    /// <summary>The place under <paramref name="point"/>, in degrees.</summary>
    internal (double Latitude, double Longitude) PlaceAt(Point point)
    {
        var viewport = _map.Navigator.Viewport;
        var x = viewport.CenterX + (point.X - viewport.Width / 2) * viewport.Resolution;
        var y = viewport.CenterY - (point.Y - viewport.Height / 2) * viewport.Resolution;
        var (lon, lat) = SphericalMercator.ToLonLat(x, y);
        return (Math.Clamp(lat, -85, 85), Math.Clamp(lon, -180, 180));
    }

    /// <summary>Selects a station by name (or clears with null), as a click would.</summary>
    public void Select(string? name)
    {
        if (Selected == name)
        {
            return;
        }
        Selected = name;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, name);
    }

    /// <summary>
    /// Fits every station shown, with a margin (behavior 4); a single station is shown at street level. Returns false
    /// when there is nothing to fit.
    /// </summary>
    public bool Fit()
    {
        if (_marks.Count == 0)
        {
            return false;
        }
        if (_marks.Count == 1)
        {
            _map.Navigator.CenterOnAndZoomTo(new MPoint(_marks[0].X, _marks[0].Y), SingleStationResolution);
            QueueRefresh();
            return true;
        }
        var box = new MRect(_marks.Min(m => m.X), _marks.Min(m => m.Y), _marks.Max(m => m.X), _marks.Max(m => m.Y));
        var margin = Math.Max(box.Width, box.Height) * 0.1 + 500;
        _map.Navigator.ZoomToBox(box.Grow(margin), MBoxFit.Fit);
        QueueRefresh();
        return true;
    }

    /// <summary>Raised when the view moves or zooms, so it can be remembered (behavior 2).</summary>
    public event EventHandler? ViewChanged;

    /// <summary>The provider shown, if any.</summary>
    public TileProviderInfo? Provider { get; private set; }

    /// <summary>How much the tiles are darkened, 0 to 1: in a dark theme they are dimmed by default (behavior 13).</summary>
    public double? Dimming { get; set; }

    /// <summary>The centre (degrees) and resolution (metres per pixel) of the view, once it has a size.</summary>
    public (double Latitude, double Longitude, double Resolution)? View
    {
        get
        {
            var viewport = _map.Navigator.Viewport;
            if (viewport.Width <= 0 || viewport.Resolution <= 0)
            {
                return null;
            }
            var (lon, lat) = SphericalMercator.ToLonLat(viewport.CenterX, viewport.CenterY);
            return (lat, lon, viewport.Resolution);
        }
    }

    /// <summary>Shows <paramref name="provider"/>'s tiles from <paramref name="tiles"/>, replacing any shown.</summary>
    public void ShowTiles(Platform.ITileSource tiles, TileProviderInfo provider)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(provider);
        DisposeTiles();
        _tiles = new TileLayer(new ProviderTileSource(tiles, provider)) { Name = provider.Key };
        _map.Layers.Insert(0, _tiles);
        Provider = provider;
        QueueRefresh();
    }

    /// <summary>Takes the tile layer off the map and releases its cached tiles.</summary>
    private void DisposeTiles()
    {
        if (_tiles is null)
        {
            return;
        }
        _map.Layers.Remove(_tiles);
        try
        {
            _tiles.Dispose();
        }
        catch (InvalidOperationException)
        {
            // BruTile's memory cache walks its entries on Dispose without the lock its Add takes, and Mapsui can't
            // cancel a fetch already running, so a tile landing now breaks the walk. What's left is plain memory.
        }
        _tiles = null;
    }

    /// <summary>Centres on a place at a resolution, as remembered (behavior 2).</summary>
    public void GoTo(double latitude, double longitude, double resolution)
    {
        var (x, y) = SphericalMercator.FromLonLat(longitude, latitude);
        _map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), resolution);
        QueueRefresh();
    }

    /// <summary>Shows about <paramref name="spanKm"/> across, centred on a place (behaviors 2 and 3).</summary>
    public void ShowAround(double latitude, double longitude, double spanKm)
    {
        var (x, y) = SphericalMercator.FromLonLat(longitude, latitude);
        // Web Mercator stretches distances by 1 / cos(latitude), so the box is widened to keep the span on the ground.
        var half = spanKm * 1000 / 2 / Math.Max(0.05, Math.Cos(latitude * Math.PI / 180));
        _map.Navigator.ZoomToBox(new MRect(x - half, y - half, x + half, y + half), MBoxFit.Fit);
        QueueRefresh();
    }

    /// <summary>Shows the whole world (behavior 2, when there's nothing better).</summary>
    public void ShowWorld()
    {
        const double edge = 20_037_508.34;
        _map.Navigator.ZoomToBox(new MRect(-edge, -edge * 0.75, edge, edge * 0.75), MBoxFit.Fit);
        QueueRefresh();
    }

    /// <summary>Zooms in one step about the centre.</summary>
    public void ZoomIn()
    {
        _map.Navigator.ZoomIn();
        QueueRefresh();
    }

    /// <summary>Zooms out one step about the centre.</summary>
    public void ZoomOut()
    {
        _map.Navigator.ZoomOut();
        QueueRefresh();
    }

    /// <summary>Pans by a share of the view's width and height (arrow keys).</summary>
    public void Pan(double shareX, double shareY)
    {
        var viewport = _map.Navigator.Viewport;
        _map.Navigator.CenterOn(
            viewport.CenterX + shareX * viewport.Width * viewport.Resolution,
            viewport.CenterY - shareY * viewport.Height * viewport.Resolution
        );
        QueueRefresh();
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _map.Navigator.SetSize(e.NewSize.Width, e.NewSize.Height);
        QueueRefresh();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var viewport = _map.Navigator.Viewport;
        var marks = _drawn;
        var screen = new SKPoint[marks.Count];
        for (var i = 0; i < marks.Count; i++)
        {
            screen[i] = ToScreen(viewport, marks[i]);
        }
        // The map and symbols first: the tiles would cover anything drawn before them.
        // Everything the render thread needs is read here, on the UI thread: styled properties such as the theme
        // variant can't be read from the render thread (on desktop that throws every frame and blanks the window).
        var tracks = _tracks.Build(
            viewport,
            TrackStyleFor,
            new TrackColours(_trackColour, _followedColour, _casingColour)
        );
        var route = BuildRoute(viewport);
        var barbs = BuildBarbs(marks, screen);
        var areas = BuildAreas(viewport, marks);
        var links = BuildLinks(viewport);
        context.Custom(
            new DrawOperation(
                new Rect(Bounds.Size),
                this,
                marks,
                screen,
                (float)_symbolDip,
                _dot,
                EffectiveDimming,
                tracks,
                route,
                barbs,
                areas,
                links
            )
        );
        DrawSignposts(context, marks, screen);
        if (route?.DirectLabel is var (at, km))
        {
            DrawRouteLabel(context, new Point(at.X, at.Y), km);
        }
        DrawFilterAreas(context, viewport);
        DrawDestination(context, viewport);
        DrawLabels(context, marks, screen);
        if (Selected is { } name && _ringPen is not null && FindIndex(marks, name) is { } selected)
        {
            var radius = _symbolDip * 0.75;
            context.DrawEllipse(null, _ringPen, new Point(screen[selected].X, screen[selected].Y), radius, radius);
        }
        if (_map.UpdateAnimations())
        {
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
    }

    /// <summary>The distance of a direct route, on the dotted line, in a box like a label's halo (F1.21 behavior 2).</summary>
    private void DrawRouteLabel(DrawingContext context, Point at, double km)
    {
        if (_labelBrush is null || _haloBrush is null)
        {
            return;
        }
        var text = new FormattedText(
            Localization.RangeText.Distance(km, DistanceUnit, Localization.Localizer.Instance),
            Localization.Localizer.Instance.Culture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            _labelSize,
            _labelBrush
        );
        var box = new Rect(at.X - (text.Width / 2) - 3, at.Y - (text.Height / 2) - 1, text.Width + 6, text.Height + 2);
        context.DrawRectangle(_haloBrush, null, box, 3, 3);
        context.DrawText(text, new Point(box.X + 3, box.Y + 1));
    }

    /// <summary>How a station's track is drawn now, or null for not at all (F1.5 behaviors 6–8).</summary>
    internal (TrackStyle, bool)? TrackStyleFor(string name)
    {
        var style =
            name == Followed ? TrackStyle.Followed
            : name == Selected ? TrackStyle.Selected
            : Tracks == MapTracks.All ? TrackStyle.Other
            : (TrackStyle?)null;
        if (Tracks == MapTracks.None || style is not { } drawn)
        {
            return null;
        }
        return (drawn, _byName.TryGetValue(name, out var mark) && mark.IsStale);
    }

    /// <summary>
    /// Each range part of the filter as a dashed circle in the accent colour (F1.2 behavior 6): a circle on the ground
    /// is a circle on a Web Mercator map, scaled by 1 / cos(latitude) at its centre.
    /// </summary>
    private void DrawFilterAreas(DrawingContext context, Viewport viewport)
    {
        if (!ShowFilterCircles || _filterPen is null)
        {
            return;
        }
        foreach (var area in _filterAreas)
        {
            var (x, y) = SphericalMercator.FromLonLat(area.Longitude, area.Latitude);
            var at = ToScreen(viewport, new StationMark(string.Empty, x, y, null, false, default));
            var metres = area.RadiusKm * 1000 / Math.Cos(area.Latitude * Math.PI / 180);
            var radius = metres / viewport.Resolution;
            context.DrawEllipse(null, _filterPen, new Point(at.X, at.Y), radius, radius);
        }
    }

    /// <summary>
    /// The callsign under each symbol, in a halo, where it fits (behavior 17): the most recently heard station keeps its
    /// label when two would overlap. Each label is drawn once into a bitmap and the bitmap reused every frame: drawn as
    /// text, the halo's nine passes cost more than everything else on the map together (2,000 stations in the browser:
    /// 35 frames a second as text, 60 without labels).
    /// </summary>
    private void DrawLabels(DrawingContext context, IReadOnlyList<StationMark> marks, SKPoint[] screen)
    {
        if (!ShowLabels || marks.Count == 0 || _labelBrush is null || _haloBrush is null)
        {
            return;
        }
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (scale != _labelScale)
        {
            ClearLabels(); // drawn for the old pixel density
            _labelScale = scale;
        }
        var order = new int[marks.Count];
        var heard = new DateTimeOffset[marks.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
            heard[i] = marks[i].LastHeard;
        }
        Array.Sort(heard, order);
        var placed = new LabelGrid(Math.Max(32, _labelSize * 6));
        var visible = new Rect(Bounds.Size).Inflate(_symbolDip);
        for (var n = order.Length - 1; n >= 0; n--) // most recently heard first
        {
            var i = order[n];
            var at = screen[i];
            if (!visible.Contains(new Point(at.X, at.Y)))
            {
                continue;
            }
            var label = Label(LabelText(marks[i]));
            var rect = new Rect(at.X - label.Width / 2, at.Y + _symbolDip / 2 + 1, label.Width, label.Height);
            if (!placed.TryAdd(rect.Inflate(1)))
            {
                continue;
            }
            var image = label.Image(scale);
            var bounds = rect.Inflate(MapLabel.Halo);
            if (marks[i].IsStale)
            {
                // A stale station's label fades with its symbol (behavior 19).
                using (context.PushOpacity(0.5))
                {
                    context.DrawImage(image, bounds);
                }
            }
            else
            {
                context.DrawImage(image, bounds);
            }
        }
    }

    /// <summary>A label laid out once per name, size and colours.</summary>
    private MapLabel Label(string name)
    {
        if (!_labels.TryGetValue(name, out var label))
        {
            var typeface = new Typeface(
                GetValue(TextElement.FontFamilyProperty),
                FontStyle.Normal,
                FontWeight.SemiBold
            );
            FormattedText Make(IBrush? brush) =>
                new(
                    name,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    _labelSize,
                    brush
                )
                {
                    TextAlignment = TextAlignment.Center, // a weather line centres under the callsign
                };
            if (_labels.Count >= MaxLabels)
            {
                ClearLabels(); // bounded: names come and go
            }
            label = new MapLabel(Make(_labelBrush), Make(_haloBrush));
            _labels[name] = label;
        }
        return label;
    }

    private const int MaxLabels = 5_000;

    private void ClearLabels()
    {
        foreach (var label in _labels.Values)
        {
            label.Dispose();
        }
        _labels.Clear();
    }

    private static SKPoint ToScreen(Viewport viewport, StationMark mark) =>
        // No rotation, so world to screen is a scale and an offset.
        new(
            (float)((mark.X - viewport.CenterX) / viewport.Resolution + viewport.Width / 2),
            (float)((viewport.CenterY - mark.Y) / viewport.Resolution + viewport.Height / 2)
        );

    private static int? FindIndex(IReadOnlyList<StationMark> marks, string name)
    {
        for (var i = 0; i < marks.Count; i++)
        {
            if (marks[i].Name == name)
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>Where a place is on screen now.</summary>
    internal Point ScreenPoint(double latitude, double longitude)
    {
        var (x, y) = SphericalMercator.FromLonLat(longitude, latitude);
        var at = ToScreen(_map.Navigator.Viewport, new StationMark(string.Empty, x, y, null, false, default));
        return new Point(at.X, at.Y);
    }

    /// <summary>The station under <paramref name="point"/>, nearest first, or null.</summary>
    internal StationMark? HitTest(Point point)
    {
        var viewport = _map.Navigator.Viewport;
        var reach = _symbolDip / 2 + HitSlop;
        StationMark? best = null;
        var bestDistance = double.MaxValue;
        foreach (var mark in _marks)
        {
            var at = ToScreen(viewport, mark);
            var distance = Math.Sqrt(Math.Pow(at.X - point.X, 2) + Math.Pow(at.Y - point.Y, 2));
            if (distance <= reach && distance < bestDistance)
            {
                best = mark;
                bestDistance = distance;
            }
        }
        return best;
    }

    private bool IsOverMe(Point point)
    {
        if (_me is null)
        {
            return false;
        }
        var at = ToScreen(_map.Navigator.Viewport, _me);
        return Math.Abs(at.X - point.X) <= _symbolDip / 2 + HitSlop
            && Math.Abs(at.Y - point.Y) <= _symbolDip / 2 + HitSlop;
    }

    private void Watch(string key, Action<object?> apply) =>
        this.GetResourceObservable(key).Subscribe(new ResourceObserver(this, apply));

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return; // the context menu opens on release (F1.13 behavior 2), without dragging the map
        }
        if (e.ClickCount == 2)
        {
            var at = e.GetPosition(this);
            _map.Navigator.ZoomIn(new ScreenPosition(at.X, at.Y)); // double-click or double-tap (behavior 5)
            QueueRefresh();
            return;
        }
        _dragFrom = e.GetPosition(this);
        _pressedAt = _dragFrom;
        e.Pointer.Capture(this);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
        {
            ShowHover(e.GetPosition(this));
            return;
        }
        var to = e.GetPosition(this);
        if (to != from)
        {
            PannedByHand?.Invoke(this, EventArgs.Empty);
        }
        _map.Navigator.Manipulate(
            new Manipulation(new ScreenPosition(to.X, to.Y), new ScreenPosition(from.X, from.Y), 1, 0, 0)
        );
        _dragFrom = to;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
        var at = e.GetPosition(this);
        if (_pressedAt is { } pressed && Math.Abs(at.X - pressed.X) + Math.Abs(at.Y - pressed.Y) < ClickDistance)
        {
            // A station, or this station's house: it selects too, for its details and its RF links (F3.1 behavior 8).
            var hit = HitTest(at)?.Name ?? (IsOverMe(at) && _me is { Name.Length: > 0 } me ? me.Name : null);
            if (
                hit is null
                && Selected is { } selected
                && _tracks.PointAt(selected, _map.Navigator.Viewport, new SKPoint((float)at.X, (float)at.Y), 6)
                    is { } time
            )
            {
                TrackPointPicked?.Invoke(this, (selected, time)); // the packet behind a point of the selected track
            }
            else
            {
                Select(hit); // a click on a station selects it; on the background, clears (behavior 18)
            }
        }
        _pressedAt = null;
        QueueRefresh();
    }

    /// <summary>Hovering a station shows its callsign and how long ago it was heard (behavior 18).</summary>
    private void ShowHover(Point at)
    {
        var mark = HitTest(at);
        var tip = mark is null
            ? IsOverMe(at)
                ? Localization.Localizer.Instance.Format(
                    "Map.MyLocation",
                    new Dictionary<string, object?> { ["name"] = _me!.Name }
                )
                : null
            : Localization.Localizer.Instance.Format(
                "Map.Hover",
                new Dictionary<string, object?>
                {
                    ["name"] = mark.Name,
                    ["minutes"] = (long)Math.Max(0, (Time.GetUtcNow() - mark.LastHeard).TotalMinutes),
                }
            );
        if (
            mark is not null
            && Localization.RangeText.Format(
                mark.DistanceKm,
                mark.BearingDegrees,
                DistanceUnit,
                Localization.Localizer.Instance
            )
                is { } range
        )
        {
            // How far, and which way, from my location (F1.13 behavior 4).
            tip = Localization.Localizer.Instance.Format(
                "Map.HoverRange",
                new Dictionary<string, object?> { ["hover"] = tip, ["range"] = range }
            );
        }
        if (!Equals(ToolTip.GetTip(this), tip))
        {
            ToolTip.SetTip(this, tip);
            ToolTip.SetIsOpen(this, tip is not null);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        // A wheel notch is a delta of 1; a touchpad sends a stream of small fractions, and sideways ones with no
        // vertical part. Zoom one step per whole notch, adding the fractions up, so a two-finger scroll zooms smoothly
        // and a sideways one doesn't zoom at all (behavior 5).
        _wheel += e.Delta.Y;
        if (!(Math.Abs(_wheel) >= 1))
        {
            return; // a fraction so far (or not a number)
        }
        // At most one step per event: a burst of momentum, or one huge delta, mustn't queue a run of zooms.
        var zoomIn = _wheel > 0;
        _wheel = 0;
        ZoomAt(e.GetPosition(this), zoomIn);
    }

    /// <summary>A touchpad pinch zooms about the fingers (behavior 5), one step per quarter of scale.</summary>
    private void OnPinch(object? sender, PointerDeltaEventArgs e)
    {
        e.Handled = true;
        _pinch += e.Delta.X;
        if (Math.Abs(_pinch) < PinchStep)
        {
            return;
        }
        ZoomAt(e.GetPosition(this), _pinch > 0);
        _pinch = 0;
    }

    /// <summary>One zoom step in or out, keeping the place under <paramref name="at"/> where it is.</summary>
    private void ZoomAt(Point at, bool zoomIn)
    {
        var position = new ScreenPosition(at.X, at.Y);
        if (zoomIn)
        {
            _map.Navigator.ZoomIn(position);
        }
        else
        {
            _map.Navigator.ZoomOut(position);
        }
        QueueRefresh();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                ZoomIn();
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomOut();
                break;
            case Key.Left:
                Pan(-ArrowPanShare, 0);
                break;
            case Key.Right:
                Pan(ArrowPanShare, 0);
                break;
            case Key.Up:
                Pan(0, -ArrowPanShare);
                break;
            case Key.Down:
                Pan(0, ArrowPanShare);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Asks the tile layer for what the view now needs, once per burst of changes.</summary>
    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }
        _refreshQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _refreshQueued = false;
                _map.RefreshData();
                InvalidateVisual();
            },
            DispatcherPriority.Background
        );
    }

    private double EffectiveDimming => Dimming ?? (ActualThemeVariant == ThemeVariant.Dark ? 0.35 : 0);

    private sealed class GroundObserver(MapView owner) : IObserver<object?>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(object? value)
        {
            if (value is Color color)
            {
                owner._ground = new SKColor(color.R, color.G, color.B, color.A);
                owner._map.BackColor = new Mapsui.Styles.Color(color.R, color.G, color.B, color.A);
                owner.InvalidateVisual();
            }
        }
    }

    private sealed class ResourceObserver(MapView owner, Action<object?> apply) : IObserver<object?>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(object? value)
        {
            apply(value);
            owner.InvalidateVisual();
        }
    }

    /// <summary>
    /// One station's label: its text in the text colour over a one-pixel halo in the surface colour, drawn into a bitmap
    /// the first time it's placed.
    /// </summary>
    private sealed class MapLabel(FormattedText text, FormattedText halo) : IDisposable
    {
        /// <summary>How far the halo reaches past the text, in DIPs.</summary>
        public const double Halo = 1;

        private static readonly (double, double)[] Offsets =
        [
            (-1, -1),
            (0, -1),
            (1, -1),
            (-1, 0),
            (1, 0),
            (-1, 1),
            (0, 1),
            (1, 1),
        ];

        private RenderTargetBitmap? _image;

        public double Width => text.Width;

        public double Height => text.Height;

        public RenderTargetBitmap Image(double scale)
        {
            if (_image is null)
            {
                var size = new Size(text.Width + 2 * Halo, text.Height + 2 * Halo);
                _image = new RenderTargetBitmap(
                    new PixelSize(
                        Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                        Math.Max(1, (int)Math.Ceiling(size.Height * scale))
                    ),
                    new Vector(96 * scale, 96 * scale)
                );
                using var context = _image.CreateDrawingContext();
                foreach (var (dx, dy) in Offsets)
                {
                    context.DrawText(halo, new Point(Halo + dx, Halo + dy));
                }
                context.DrawText(text, new Point(Halo, Halo));
            }
            return _image;
        }

        public void Dispose() => _image?.Dispose();
    }

    /// <summary>Label rectangles placed so far, bucketed by cell so each check looks at a few neighbours only.</summary>
    private sealed class LabelGrid(double cell)
    {
        private readonly Dictionary<(int, int), List<Rect>> _cells = [];

        public bool TryAdd(Rect rect)
        {
            var (x0, y0, x1, y1) = (Cell(rect.X), Cell(rect.Y), Cell(rect.Right), Cell(rect.Bottom));
            for (var x = x0; x <= x1; x++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    if (_cells.TryGetValue((x, y), out var list) && list.Any(r => r.Intersects(rect)))
                    {
                        return false;
                    }
                }
            }
            for (var x = x0; x <= x1; x++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    if (!_cells.TryGetValue((x, y), out var list))
                    {
                        _cells[(x, y)] = list = [];
                    }
                    list.Add(rect);
                }
            }
            return true;
        }

        private int Cell(double value) => (int)Math.Floor(value / cell);
    }

    private sealed class DrawOperation(
        Rect bounds,
        MapView owner,
        IReadOnlyList<StationMark> marks,
        SKPoint[] screen,
        float symbolDip,
        SKColor dot,
        double dimming,
        TrackLayer.TrackFrame tracks,
        RouteFrame? route,
        WindBarbs? barbs,
        AreaFrame? areas,
        LinkFrame? links
    ) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public void Dispose()
        {
            tracks.Dispose();
            route?.Dispose();
            barbs?.Dispose();
            areas?.Dispose();
            links?.Dispose();
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
            {
                return;
            }
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var map = owner._map;
            canvas.Save();
            using (var ground = new SKPaint { Color = owner._ground })
            {
                canvas.DrawRect(0, 0, (float)bounds.Width, (float)bounds.Height, ground);
            }
            owner._renderer.Render(
                canvas,
                map.Navigator.Viewport,
                map.Layers,
                map.GetWidgetsOfMapAndLayers(),
                map.RenderService,
                map.BackColor,
                null,
                default
            );
            if (dimming > 0)
            {
                using var dim = new SKPaint { Color = owner._ground.WithAlpha((byte)(dimming * 255)) };
                canvas.DrawRect(0, 0, (float)bounds.Width, (float)bounds.Height, dim);
            }
            canvas.Restore();
            // Tracks over the tiles and under the symbols (F1.5 behavior 5).
            areas?.Draw(canvas); // area shapes under tracks and symbols (F1.12 behavior 4)
            links?.Draw(canvas); // the RF network (F1.21 behavior 5)
            tracks.Draw(canvas);
            route?.Draw(canvas); // the selected packet's route over the tracks (F1.21 behavior 2)
            // Stations over the tiles (and over the dimming, so they keep their contrast).
            owner._stations.Draw(canvas, marks, screen, symbolDip, Math.Max(1f, canvas.TotalMatrix.ScaleX), dot);
            barbs?.Draw(canvas, owner._textColour, owner._casingColour); // wind over the symbols (F1.10 behavior 5)
        }
    }
}
