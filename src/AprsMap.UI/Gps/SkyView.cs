// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AprsMap.UI.Gps;

/// <summary>
/// The sky view (docs/features/F3.12-gps-status.md, behavior 4): a polar diagram, north up, the horizon outside and
/// 30° and 60° elevation inside, each satellite a mark at its azimuth and elevation: filled when used, hollow when not,
/// its shape its system, its number beside it. Drawn on the UI thread; the brushes come from theme tokens set in XAML.
/// </summary>
public sealed class SkyView : Control
{
    /// <summary>The satellites to draw.</summary>
    public static readonly StyledProperty<IReadOnlyList<GpsSkySatellite>?> SatellitesProperty =
        AvaloniaProperty.Register<SkyView, IReadOnlyList<GpsSkySatellite>?>(nameof(Satellites));

    /// <summary>The rings and compass points.</summary>
    public static readonly StyledProperty<IBrush?> RingBrushProperty = AvaloniaProperty.Register<SkyView, IBrush?>(
        nameof(RingBrush)
    );

    /// <summary>The compass letters.</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<SkyView, IBrush?>(
        nameof(LabelBrush)
    );

    /// <summary>A satellite the fix uses.</summary>
    public static readonly StyledProperty<IBrush?> UsedBrushProperty = AvaloniaProperty.Register<SkyView, IBrush?>(
        nameof(UsedBrush)
    );

    /// <summary>The numbers, and the outline of a satellite not used.</summary>
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<SkyView, IBrush?>(
        nameof(TextBrush)
    );

    /// <summary>The size of the numbers and compass points; the marks follow it.</summary>
    public static readonly StyledProperty<double> FontSizeProperty = AvaloniaProperty.Register<SkyView, double>(
        nameof(FontSize),
        12
    );

    /// <summary>The compass points' letters: north, east, south, west.</summary>
    public static readonly StyledProperty<string> CompassProperty = AvaloniaProperty.Register<SkyView, string>(
        nameof(Compass),
        "NESW"
    );

    static SkyView()
    {
        AffectsRender<SkyView>(
            SatellitesProperty,
            RingBrushProperty,
            LabelBrushProperty,
            UsedBrushProperty,
            TextBrushProperty,
            FontSizeProperty,
            CompassProperty
        );
        AffectsMeasure<SkyView>(FontSizeProperty);
    }

    /// <inheritdoc cref="SatellitesProperty" />
    public IReadOnlyList<GpsSkySatellite>? Satellites
    {
        get => GetValue(SatellitesProperty);
        set => SetValue(SatellitesProperty, value);
    }

    /// <inheritdoc cref="RingBrushProperty" />
    public IBrush? RingBrush
    {
        get => GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    /// <inheritdoc cref="LabelBrushProperty" />
    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <inheritdoc cref="UsedBrushProperty" />
    public IBrush? UsedBrush
    {
        get => GetValue(UsedBrushProperty);
        set => SetValue(UsedBrushProperty, value);
    }

    /// <inheritdoc cref="TextBrushProperty" />
    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    /// <inheritdoc cref="FontSizeProperty" />
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <inheritdoc cref="CompassProperty" />
    public string Compass
    {
        get => GetValue(CompassProperty);
        set => SetValue(CompassProperty, value);
    }

    /// <summary>Square, as wide as it may be up to 24 lines of text, so it grows with the text size.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var most = FontSize * 24;
        var side = Math.Min(double.IsFinite(availableSize.Width) ? availableSize.Width : most, most);
        return new Size(side, side);
    }

    /// <summary>Where a satellite is drawn, from the centre of a sky of <paramref name="radius"/>.</summary>
    internal static Point Place(Point centre, double radius, double elevation, double azimuth)
    {
        var r = radius * (90 - Math.Clamp(elevation, 0, 90)) / 90;
        var a = azimuth * Math.PI / 180;
        return new Point(centre.X + (r * Math.Sin(a)), centre.Y - (r * Math.Cos(a)));
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var size = FontSize;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var radius = (side / 2) - (size * 1.4); // room for the compass letters outside the horizon
        if (radius <= 0 || RingBrush is not { } ring || TextBrush is not { } text)
        {
            return;
        }
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var ringPen = new Pen(ring, 1);
        foreach (var elevation in (int[])[0, 30, 60])
        {
            var r = radius * (90 - elevation) / 90;
            context.DrawEllipse(null, ringPen, centre, r, r);
        }
        context.DrawLine(ringPen, new Point(centre.X, centre.Y - radius), new Point(centre.X, centre.Y + radius));
        context.DrawLine(ringPen, new Point(centre.X - radius, centre.Y), new Point(centre.X + radius, centre.Y));
        var compass = Compass.Length >= 4 ? Compass : "NESW";
        for (var i = 0; i < 4; i++)
        {
            var letter = Text(compass[i].ToString(), LabelBrush ?? text, size);
            var at = Place(centre, radius + (size * 0.8), 0, i * 90);
            context.DrawText(letter, new Point(at.X - (letter.Width / 2), at.Y - (letter.Height / 2)));
        }
        if (Satellites is not { Count: > 0 } satellites)
        {
            return;
        }
        var mark = size * 0.55;
        var outline = new Pen(text, 1.5);
        var used = UsedBrush ?? text;
        foreach (var s in satellites)
        {
            if (s is not { ElevationDegrees: { } elevation, AzimuthDegrees: { } azimuth })
            {
                continue; // seen, but not where
            }
            var at = Place(centre, radius, elevation, azimuth);
            DrawMark(context, s.System, at, mark, s.Used ? used : null, s.Used ? new Pen(used, 1.5) : outline);
            var number = Text(s.Id.ToString(CultureInfo.InvariantCulture), text, size * 0.85);
            context.DrawText(number, new Point(at.X + mark + 2, at.Y - (number.Height / 2)));
        }
    }

    /// <summary>A satellite's mark: its shape says its system (decision 2), fill whether it's used.</summary>
    private static void DrawMark(
        DrawingContext context,
        GnssConstellation system,
        Point at,
        double r,
        IBrush? fill,
        Pen pen
    )
    {
        switch (system)
        {
            case GnssConstellation.Gps:
                context.DrawEllipse(fill, pen, at, r, r);
                break;
            case GnssConstellation.Glonass:
                context.DrawRectangle(fill, pen, new Rect(at.X - r, at.Y - r, 2 * r, 2 * r));
                break;
            case GnssConstellation.Galileo:
                context.DrawGeometry(fill, pen, Polygon(at, r * 1.25, 4, 0));
                break;
            case GnssConstellation.BeiDou:
                context.DrawGeometry(fill, pen, Polygon(at, r * 1.25, 3, 0));
                break;
            default:
                var cross = new StreamGeometry();
                using (var g = cross.Open())
                {
                    var w = r * 0.45;
                    // A plus sign with width, so "used" can fill it.
                    g.BeginFigure(new Point(at.X - w, at.Y - r), true);
                    foreach (
                        var (x, y) in (ReadOnlySpan<(double, double)>)
                            [
                                (w, -r),
                                (w, -w),
                                (r, -w),
                                (r, w),
                                (w, w),
                                (w, r),
                                (-w, r),
                                (-w, w),
                                (-r, w),
                                (-r, -w),
                                (-w, -w),
                            ]
                    )
                    {
                        g.LineTo(new Point(at.X + x, at.Y + y));
                    }
                    g.EndFigure(true);
                }
                context.DrawGeometry(fill, pen, cross);
                break;
        }
    }

    private static StreamGeometry Polygon(Point at, double r, int sides, double start)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        for (var i = 0; i < sides; i++)
        {
            var a = start + (i * 2 * Math.PI / sides);
            var p = new Point(at.X + (r * Math.Sin(a)), at.Y - (r * Math.Cos(a)));
            if (i == 0)
            {
                g.BeginFigure(p, true);
            }
            else
            {
                g.LineTo(p);
            }
        }
        g.EndFigure(true);
        return geometry;
    }

    private static FormattedText Text(string value, IBrush brush, double size) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
}
