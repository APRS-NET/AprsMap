// SPDX-License-Identifier: AGPL-3.0-only
using SkiaSharp;

namespace AprsMap.UI.Map;

/// <summary>
/// Wind barbs (docs/features/F1.10-weather.md, behavior 5): a shaft from the station toward where the wind blows from,
/// with a flag for each 50 knots, a long feather for each 10 and a short one for 5, rounded to the nearest 5 knots.
/// Built on the UI thread, drawn on the render thread.
/// </summary>
internal sealed class WindBarbs : IDisposable
{
    private const double KnotsPerMetrePerSecond = 1.943844;
    private const float Shaft = 28;
    private const float Feather = 11;
    private const float Spacing = 4.5f;

    private readonly SKPath _lines = new();
    private readonly SKPath _flags = new();

    /// <summary>The barbs drawn, for tests.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// How a speed is drawn: flags of 50 knots, long feathers of 10, and at most one short one of 5, after rounding to
    /// the nearest 5 knots.
    /// </summary>
    public static (int Flags, int Long, int Short) Parts(double knots)
    {
        var rounded = (int)(Math.Round(knots / 5, MidpointRounding.AwayFromZero) * 5);
        return (rounded / 50, rounded % 50 / 10, rounded % 10 / 5);
    }

    /// <summary>Adds a barb at <paramref name="at"/>, starting <paramref name="clearance"/> from it.</summary>
    public void Add(SKPoint at, int fromDegrees, double metresPerSecond, float clearance)
    {
        var (flags, longs, shorts) = Parts(metresPerSecond * KnotsPerMetrePerSecond);
        if (flags + longs + shorts == 0)
        {
            return; // calm: no shaft to point
        }
        // Screen y grows downwards: the direction the wind comes from, as a unit vector on screen.
        var radians = fromDegrees * Math.PI / 180;
        var ux = (float)Math.Sin(radians);
        var uy = (float)-Math.Cos(radians);
        // The feathers lean back from the tip, on the shaft's clockwise side.
        var fx = -uy;
        var fy = ux;
        var start = new SKPoint(at.X + (ux * clearance), at.Y + (uy * clearance));
        var tip = new SKPoint(start.X + (ux * Shaft), start.Y + (uy * Shaft));
        _lines.MoveTo(start);
        _lines.LineTo(tip);
        var along = 0f;
        for (var i = 0; i < flags; i++)
        {
            var a = Along(along);
            var b = Along(along + Spacing * 1.6f);
            _flags.MoveTo(a);
            _flags.LineTo(a.X + (fx * Feather), a.Y + (fy * Feather));
            _flags.LineTo(b);
            _flags.Close();
            along += Spacing * 2;
        }
        for (var i = 0; i < longs; i++)
        {
            Barb(along, Feather);
            along += Spacing;
        }
        if (shorts > 0)
        {
            // A lone short feather stands back from the tip, so it doesn't read as a long one.
            Barb(flags + longs == 0 ? along + Spacing : along, Feather / 2);
        }
        Count++;

        SKPoint Along(float back) => new(tip.X - (ux * back), tip.Y - (uy * back));

        void Barb(float back, float length)
        {
            var root = Along(back);
            _lines.MoveTo(root);
            _lines.LineTo(root.X + (fx * length) + (ux * length / 2), root.Y + (fy * length) + (uy * length / 2));
        }
    }

    /// <summary>Draws the barbs in <paramref name="colour"/> over a <paramref name="casing"/> halo, like the labels.</summary>
    public void Draw(SKCanvas canvas, SKColor colour, SKColor casing)
    {
        if (Count == 0)
        {
            return;
        }
        using var paint = new SKPaint
        {
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        foreach (var halo in new[] { true, false })
        {
            paint.Color = halo ? casing : colour;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = halo ? 4 : 1.5f;
            canvas.DrawPath(_lines, paint);
            paint.Style = SKPaintStyle.StrokeAndFill;
            canvas.DrawPath(_flags, paint);
        }
    }

    public void Dispose()
    {
        _lines.Dispose();
        _flags.Dispose();
    }
}
