// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using Mapsui.Projections;
using SkiaSharp;

namespace AprsMap.UI.Map;

/// <summary>
/// Area objects drawn as their shapes (docs/features/F1.12-area-objects.md, behaviors 1–5): built on the UI thread for
/// the stations on screen, drawn on the render thread under tracks and symbols. APRS's 16 colours fold onto 8 palette
/// tokens, a bright colour and its dark one sharing a token.
/// </summary>
internal sealed class AreaFrame : IDisposable
{
    /// <summary>How opaque a filled shape's inside is.</summary>
    private const byte FillAlpha = 64;

    private readonly List<(SKPath Path, int Token, bool Filled, bool Stale, float Band)> _shapes = [];
    private readonly SKColor[] _colours;

    private AreaFrame(SKColor[] colours) => _colours = colours;

    /// <summary>The shapes drawn, for tests.</summary>
    public int Count => _shapes.Count;

    /// <summary>The palette token (0–7) for an APRS area colour (0–15).</summary>
    public static int TokenOf(int color) => ((color % 16) + 16) % 16 % 8;

    /// <summary>The shapes of the area objects among <paramref name="marks"/>.</summary>
    /// <param name="marks">The stations drawn.</param>
    /// <param name="toScreen">Map coordinates to screen.</param>
    /// <param name="resolution">Map units per pixel, for a corridor's width.</param>
    /// <param name="colours">The eight area colours, from the palette.</param>
    public static AreaFrame Build(
        IReadOnlyList<StationMark> marks,
        Func<double, double, SKPoint> toScreen,
        double resolution,
        SKColor[] colours
    )
    {
        var frame = new AreaFrame(colours);
        foreach (var mark in marks)
        {
            if (mark.Area is { } area)
            {
                frame.Add(mark, area, toScreen, resolution);
            }
        }
        return frame;
    }

    private void Add(StationMark mark, AreaInfo area, Func<double, double, SKPoint> toScreen, double resolution)
    {
        var (lon, lat) = SphericalMercator.ToLonLat(mark.X, mark.Y);
        SKPoint At(double latitude, double longitude)
        {
            var (x, y) = SphericalMercator.FromLonLat(longitude, latitude);
            return toScreen(x, y);
        }
        var origin = toScreen(mark.X, mark.Y);
        var path = new SKPath();
        var band = 0f;
        switch (area.Kind)
        {
            case AreaKind.Circle:
            case AreaKind.Ellipse:
            {
                // A circle's radius is its latitude extent, the same distance east and west.
                var lonExtent =
                    area.Kind == AreaKind.Circle
                        ? area.LatitudeDegrees / Math.Max(0.01, Math.Cos(lat * Math.PI / 180))
                        : area.LongitudeDegrees;
                var north = At(lat + area.LatitudeDegrees, lon);
                var east = At(lat, lon + lonExtent);
                path.AddOval(
                    new SKRect(origin.X - (east.X - origin.X), north.Y, east.X, origin.Y + (origin.Y - north.Y))
                );
                break;
            }
            case AreaKind.Box:
            {
                var corner = At(lat - area.LatitudeDegrees, lon + area.LongitudeDegrees);
                path.AddRect(new SKRect(origin.X, origin.Y, corner.X, corner.Y));
                break;
            }
            case AreaKind.Triangle:
                path.MoveTo(origin);
                path.LineTo(At(lat - area.LatitudeDegrees, lon + area.LongitudeDegrees));
                path.LineTo(At(lat - area.LatitudeDegrees, lon - area.LongitudeDegrees));
                path.Close();
                break;
            default:
            {
                var sign = area.Kind == AreaKind.LineLeft ? -1 : 1;
                path.MoveTo(origin);
                path.LineTo(At(lat - area.LatitudeDegrees, lon + (sign * area.LongitudeDegrees)));
                if (area.CorridorKm is { } km)
                {
                    // On Web Mercator a ground distance grows by 1 / cos(latitude).
                    band = (float)(km * 1000 / Math.Max(0.01, Math.Cos(lat * Math.PI / 180)) / resolution);
                }
                break;
            }
        }
        _shapes.Add((path, TokenOf(area.Color), area.Filled, mark.IsStale, band));
    }

    public void Draw(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        foreach (var (path, token, filled, stale, band) in _shapes)
        {
            var colour = _colours[token];
            var fade = stale ? 0.5f : 1f; // shapes fade with their object (behavior 5)
            if (band > 0)
            {
                // A line's corridor as a translucent band (behavior 2).
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = band;
                paint.Color = colour.WithAlpha((byte)(FillAlpha * fade));
                canvas.DrawPath(path, paint);
            }
            if (filled)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = colour.WithAlpha((byte)(FillAlpha * fade));
                canvas.DrawPath(path, paint);
            }
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 2;
            paint.Color = colour.WithAlpha((byte)(255 * fade));
            canvas.DrawPath(path, paint);
        }
    }

    public void Dispose()
    {
        foreach (var shape in _shapes)
        {
            shape.Path.Dispose();
        }
        _shapes.Clear();
    }
}
