// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using Mapsui.Projections;
using SkiaSharp;

namespace AprsMap.UI.Map;

/// <summary>
/// The selected packet's route on the map (docs/features/F1.21-rf-paths.md, behavior 2): arrows from hop to hop
/// through the digipeaters, and a dotted last leg to the IGate, which heard that station or digipeater directly. The
/// line stops at the first hop whose position isn't known; the details name the rest. Built on the UI thread, drawn on
/// the render thread.
/// </summary>
internal sealed class RouteFrame : IDisposable
{
    private readonly SKPath _solid = new();
    private readonly SKPath _dotted = new();
    private readonly SKPath _arrows = new();

    private RouteFrame(SKColor line, SKColor casing)
    {
        Line = line;
        Casing = casing;
    }

    public SKColor Line { get; }

    public SKColor Casing { get; }

    /// <summary>The legs drawn, for tests: solid ones and dotted ones.</summary>
    public (int Solid, int Dotted) Legs { get; private set; }

    /// <summary>Where the direct line's distance label goes, and the distance in km; null without one.</summary>
    public (SKPoint At, double Km)? DirectLabel { get; private set; }

    /// <summary>The frame for <paramref name="route"/>, or null when there's nothing to draw.</summary>
    /// <param name="route">The route.</param>
    /// <param name="toScreen">Map coordinates to screen.</param>
    /// <param name="clearance">How far from a symbol's centre a line stops, so arrows don't hide under symbols.</param>
    /// <param name="line">The line's colour.</param>
    /// <param name="casing">The casing's colour, under the line.</param>
    public static RouteFrame? Build(
        PacketRoute? route,
        Func<double, double, SKPoint> toScreen,
        float clearance,
        SKColor line,
        SKColor casing
    )
    {
        if (route is not { Latitude: { } lat, Longitude: { } lon } || route.Hops.Count == 0)
        {
            return null;
        }
        var frame = new RouteFrame(line, casing);
        var from = Project(lat, lon);
        foreach (var hop in route.Hops)
        {
            if (hop is not { Latitude: { } hlat, Longitude: { } hlon })
            {
                break; // the line stops where positions run out
            }
            var to = Project(hlat, hlon);
            frame.Leg(from, to, dotted: hop.IsGateway, clearance);
            if (hop.IsGateway && route.Kind == ArrivalKind.Direct && hop.DistanceKm is { } km)
            {
                frame.DirectLabel = (new SKPoint((from.X + to.X) / 2, (from.Y + to.Y) / 2), km);
            }
            from = to;
        }
        if (frame.Legs == (0, 0))
        {
            frame.Dispose();
            return null;
        }
        return frame;

        SKPoint Project(double latitude, double longitude)
        {
            var (x, y) = SphericalMercator.FromLonLat(longitude, latitude);
            return toScreen(x, y);
        }
    }

    private void Leg(SKPoint from, SKPoint to, bool dotted, float clearance)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        if (length <= 2 * clearance)
        {
            return; // too short to show beside the symbols
        }
        var ux = dx / length;
        var uy = dy / length;
        var start = new SKPoint(from.X + (ux * clearance), from.Y + (uy * clearance));
        var end = new SKPoint(to.X - (ux * clearance), to.Y - (uy * clearance));
        var path = dotted ? _dotted : _solid;
        path.MoveTo(start);
        path.LineTo(end);
        if (!dotted)
        {
            // An arrowhead at the end: which way the packet went.
            const float Size = 8;
            _arrows.MoveTo(end.X - (ux * Size) - (uy * Size / 2), end.Y - (uy * Size) + (ux * Size / 2));
            _arrows.LineTo(end);
            _arrows.LineTo(end.X - (ux * Size) + (uy * Size / 2), end.Y - (uy * Size) - (ux * Size / 2));
            _arrows.Close();
        }
        Legs = dotted ? (Legs.Solid, Legs.Dotted + 1) : (Legs.Solid + 1, Legs.Dotted);
    }

    public void Draw(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        using var dots = SKPathEffect.CreateDash([0.1f, 6], 0);
        foreach (var casing in new[] { true, false })
        {
            paint.Color = casing ? Casing : Line;
            paint.PathEffect = null;
            paint.StrokeWidth = casing ? 5 : 2.5f;
            canvas.DrawPath(_solid, paint);
            paint.PathEffect = dots;
            paint.StrokeWidth = casing ? 6.5f : 3.5f;
            canvas.DrawPath(_dotted, paint);
        }
        paint.PathEffect = null;
        paint.Style = SKPaintStyle.StrokeAndFill;
        paint.StrokeWidth = 1;
        paint.Color = Line;
        canvas.DrawPath(_arrows, paint);
    }

    public void Dispose()
    {
        _solid.Dispose();
        _dotted.Dispose();
        _arrows.Dispose();
    }
}
