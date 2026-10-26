// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace AprsMap.UI.Statistics;

/// <summary>
/// A tile's series as a large plot (docs/features/F1.22-statistics.md, behavior 6.1): a value axis with round ticks, a
/// time axis labelled at even steps, faint grid lines, and the line, broken where a point has no value. A cursor, a
/// vertical line with a dot on the nearest point, follows the pointer or Left, Right, Home and End; a tooltip and
/// <see cref="Readout"/> say that point's value and time. Colours and text size come from theme tokens through styles.
/// </summary>
public sealed class TimePlot : Control, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>The values, oldest first; null where a point has none.</summary>
    public static readonly StyledProperty<IReadOnlyList<double?>?> ValuesProperty = AvaloniaProperty.Register<
        TimePlot,
        IReadOnlyList<double?>?
    >(nameof(Values));

    /// <summary>Each point in words, "12 at 14:05".</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty = AvaloniaProperty.Register<
        TimePlot,
        IReadOnlyList<string>?
    >(nameof(Labels));

    /// <summary>Each point's time, for the time axis.</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> TimesProperty = AvaloniaProperty.Register<
        TimePlot,
        IReadOnlyList<string>?
    >(nameof(Times));

    /// <summary>How the value axis writes its numbers.</summary>
    public static readonly StyledProperty<string> FormatProperty = AvaloniaProperty.Register<TimePlot, string>(
        nameof(Format),
        "#,0"
    );

    /// <summary>The line's brush.</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<TimePlot, IBrush?>(
        nameof(Stroke)
    );

    /// <summary>The axes' text.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<TimePlot, IBrush?>(
        nameof(Foreground)
    );

    /// <summary>The grid lines.</summary>
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<TimePlot, IBrush?>(
        nameof(GridBrush)
    );

    /// <summary>The cursor's line.</summary>
    public static readonly StyledProperty<IBrush?> CursorBrushProperty = AvaloniaProperty.Register<TimePlot, IBrush?>(
        nameof(CursorBrush)
    );

    /// <summary>The point the cursor is on, in words; the newest before the cursor moves.</summary>
    public static readonly DirectProperty<TimePlot, string?> ReadoutProperty = AvaloniaProperty.RegisterDirect<
        TimePlot,
        string?
    >(nameof(Readout), p => p.Readout);

    private const double Gap = 8;
    private string? _readout;

    static TimePlot()
    {
        AffectsRender<TimePlot>(
            ValuesProperty,
            TimesProperty,
            FormatProperty,
            StrokeProperty,
            ForegroundProperty,
            GridBrushProperty,
            CursorBrushProperty
        );
        FocusableProperty.OverrideDefaultValue<TimePlot>(true);
        LabelsProperty.Changed.AddClassHandler<TimePlot>((p, _) => p.Say());
    }

    public IReadOnlyList<double?>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    public IReadOnlyList<string>? Times
    {
        get => GetValue(TimesProperty);
        set => SetValue(TimesProperty, value);
    }

    public string Format
    {
        get => GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? CursorBrush
    {
        get => GetValue(CursorBrushProperty);
        set => SetValue(CursorBrushProperty, value);
    }

    /// <summary>The point the cursor is on, in words.</summary>
    public string? Readout
    {
        get => _readout;
        private set => SetAndRaise(ReadoutProperty, ref _readout, value);
    }

    /// <summary>The point the cursor is on; null before it moves, which reads the newest.</summary>
    internal int? Point { get; private set; }

    /// <summary>The whole plot takes the pointer, not only its lines.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Where the plot's area is, inside the axes' labels.</summary>
    private Rect Area(double labelWidth, double textHeight) =>
        new(
            labelWidth + Gap,
            Gap,
            Math.Max(1, Bounds.Width - labelWidth - (Gap * 2)),
            Math.Max(1, Bounds.Height - textHeight - (Gap * 2.5))
        );

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Values is not { Count: > 1 } values || _area is not { } area)
        {
            return;
        }
        var x = e.GetPosition(this).X;
        Pick((int)Math.Round((x - area.Left) / area.Width * (values.Count - 1)), tooltip: true);
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
    }

    /// <summary>Left and Right move the cursor; Home and End go to the first and newest points.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Values is not { Count: > 0 } values)
        {
            return;
        }
        var at = Point ?? values.Count - 1;
        int? next = e.Key switch
        {
            Key.Left => Math.Max(0, at - 1),
            Key.Right => Math.Min(values.Count - 1, at + 1),
            Key.Home => 0,
            Key.End => values.Count - 1,
            _ => null,
        };
        if (next is { } index)
        {
            Pick(index, tooltip: true);
            e.Handled = true;
        }
    }

    private void Pick(int index, bool tooltip)
    {
        if (Values is not { Count: > 0 } values)
        {
            return;
        }
        Point = Math.Clamp(index, 0, values.Count - 1);
        Say();
        if (tooltip && Readout is { } words)
        {
            ToolTip.SetTip(this, words);
            ToolTip.SetIsOpen(this, true);
        }
        InvalidateVisual();
    }

    /// <summary>Puts the cursor's point in words, the newest while the cursor hasn't moved.</summary>
    private void Say()
    {
        if (Labels is not { Count: > 0 } labels)
        {
            Readout = null;
            return;
        }
        var index = Math.Clamp(Point ?? labels.Count - 1, 0, labels.Count - 1);
        Readout = labels[index];
        Avalonia.Automation.AutomationProperties.SetName(this, Readout);
        if (ToolTip.GetIsOpen(this))
        {
            ToolTip.SetTip(this, Readout);
        }
    }

    private Rect? _area;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _area = null;
        if (Values is not { Count: > 1 } values || Stroke is not { } stroke || Bounds.Width < 40 || Bounds.Height < 40)
        {
            return;
        }
        var known = values.OfType<double>().ToList();
        var culture = CultureInfo.CurrentCulture;
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        var size = TextElement.GetFontSize(this);
        var text = Foreground ?? stroke;
        FormattedText Write(string s) => new(s, culture, FlowDirection.LeftToRight, typeface, size, text);

        var (low, high) = known.Count == 0 ? (0, 1) : (Math.Min(0, known.Min()), known.Max());
        var ticks = Ticks(low, high);
        (low, high) = (ticks[0], ticks[^1]);
        var tickTexts = ticks.Select(t => Write(t.ToString(Format, culture))).ToList();
        var labelWidth = tickTexts.Max(t => t.Width);
        var area = Area(labelWidth, Write("0").Height);
        _area = area;
        double Y(double value) => area.Bottom - (area.Height * (value - low) / (high - low));
        double X(int i) => area.Left + (area.Width * i / (values.Count - 1));

        // The value axis: a grid line and its number at each tick.
        var grid = new Pen(GridBrush ?? text, 1);
        for (var i = 0; i < ticks.Count; i++)
        {
            var y = Y(ticks[i]);
            context.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
            context.DrawText(tickTexts[i], new Point(labelWidth - tickTexts[i].Width, y - (tickTexts[i].Height / 2)));
        }
        // The time axis: a label at about six even steps, kept inside the plot.
        if (Times is { Count: > 0 } times)
        {
            var steps = Math.Min(6, values.Count - 1);
            for (var s = 0; s <= steps; s++)
            {
                var i = (int)Math.Round((double)s * (values.Count - 1) / steps);
                if (i >= times.Count)
                {
                    continue;
                }
                var label = Write(times[i]);
                var x = Math.Clamp(X(i) - (label.Width / 2), area.Left, area.Right - label.Width);
                context.DrawText(label, new Point(x, area.Bottom + (Gap / 2)));
                context.DrawLine(grid, new Point(X(i), area.Bottom), new Point(X(i), area.Bottom + (Gap / 2)));
            }
        }

        // The line, broken where a point has no value.
        var pen = new Pen(stroke, 2);
        Point? last = null;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } value)
            {
                last = null;
                continue;
            }
            var at = new Point(X(i), Y(value));
            if (last is { } from)
            {
                context.DrawLine(pen, from, at);
            }
            else if (i + 1 >= values.Count || values[i + 1] is null)
            {
                context.DrawEllipse(stroke, null, at, 1.5, 1.5); // a point on its own still shows
            }
            last = at;
        }

        // The cursor.
        if (Point is { } picked && picked < values.Count)
        {
            var x = X(picked);
            context.DrawLine(new Pen(CursorBrush ?? text, 1), new Point(x, area.Top), new Point(x, area.Bottom));
            if (values[picked] is { } value)
            {
                context.DrawEllipse(stroke, null, new Point(x, Y(value)), 4, 4);
            }
        }
    }

    /// <summary>Round ticks from <paramref name="low"/> to <paramref name="high"/>: steps of 1, 2 or 5 times a power of ten.</summary>
    internal static IReadOnlyList<double> Ticks(double low, double high)
    {
        if (high <= low)
        {
            high = low + 1;
        }
        var raw = (high - low) / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step =
            (raw / magnitude) switch
            {
                <= 1 => 1,
                <= 2 => 2,
                <= 5 => 5,
                _ => 10,
            } * magnitude;
        var first = Math.Floor(low / step) * step;
        // Up to the first tick at or above the highest value, so the line stays inside the axis.
        var ticks = new List<double> { first };
        while (ticks.Count < 2 || ticks[^1] < high - (step * 1e-9))
        {
            ticks.Add(Math.Round((ticks[^1] + step) / step) * step);
        }
        return ticks;
    }
}
