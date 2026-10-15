// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

    private readonly Mapsui.Map _map = new();
    private readonly MapRenderer _renderer = new();
    private TileLayer? _tiles;
    private Point? _dragFrom;
    private SKColor _ground = SKColors.Gray;
    private bool _refreshQueued;

    /// <summary>Creates the map, focusable so the keyboard can pan and zoom it (behavior 7).</summary>
    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
        _map.RefreshGraphicsRequest += (_, _) => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        // Tiles arrive after the frame that asked for them: draw again when they do (the browser draws only on demand).
        _map.DataChanged += (_, _) => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        _map.Navigator.ViewportChanged += (_, _) => ViewChanged?.Invoke(this, EventArgs.Empty);
        this.GetResourceObservable("Color.Ground").Subscribe(new GroundObserver(this));
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>
    /// Releases the map's tiles. Not done on detaching: Dock moves views between parents, so the owner (the map panel)
    /// disposes the map when the panel itself goes.
    /// </summary>
    public void Dispose()
    {
        _tiles?.Dispose();
        _map.Dispose();
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
        if (_tiles is not null)
        {
            _map.Layers.Remove(_tiles);
            _tiles.Dispose();
        }
        _tiles = new TileLayer(new ProviderTileSource(tiles, provider)) { Name = provider.Key };
        _map.Layers.Insert(0, _tiles);
        Provider = provider;
        QueueRefresh();
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
        context.Custom(new DrawOperation(new Rect(Bounds.Size), this));
        if (_map.UpdateAnimations())
        {
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.ClickCount == 2)
        {
            var at = e.GetPosition(this);
            _map.Navigator.ZoomIn(new ScreenPosition(at.X, at.Y)); // double-click or double-tap (behavior 5)
            QueueRefresh();
            return;
        }
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
        {
            return;
        }
        var to = e.GetPosition(this);
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
        QueueRefresh();
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var at = e.GetPosition(this);
        _map.Navigator.MouseWheelZoom(Math.Sign(e.Delta.Y), new ScreenPosition(at.X, at.Y));
        QueueRefresh();
        e.Handled = true;
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

    private sealed class DrawOperation(Rect bounds, MapView owner) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public void Dispose() { }

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
            if (owner.EffectiveDimming is > 0 and var dimming)
            {
                using var dim = new SKPaint { Color = owner._ground.WithAlpha((byte)(dimming * 255)) };
                canvas.DrawRect(0, 0, (float)bounds.Width, (float)bounds.Height, dim);
            }
            canvas.Restore();
        }
    }
}
