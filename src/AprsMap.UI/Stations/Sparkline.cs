// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AprsMap.UI.Stations;

/// <summary>
/// A small line chart of one telemetry channel over the held reports (docs/features/F1.11-telemetry.md, behavior 4):
/// oldest on the left, scaled to the values' own range. The colour is a theme brush; missing values break the line.
/// </summary>
public sealed class Sparkline : Control
{
    /// <summary>The values, oldest first; null where a report left the channel out.</summary>
    public static readonly StyledProperty<IReadOnlyList<double?>?> ValuesProperty = AvaloniaProperty.Register<
        Sparkline,
        IReadOnlyList<double?>?
    >(nameof(Values));

    /// <summary>The line's brush.</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(
        nameof(Stroke)
    );

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, StrokeProperty);

    public IReadOnlyList<double?>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Values is not { Count: > 1 } values || Stroke is not { } stroke || Bounds.Width <= 2 || Bounds.Height <= 2)
        {
            return;
        }
        var known = values.OfType<double>().ToList();
        if (known.Count == 0)
        {
            return;
        }
        var (low, high) = (known.Min(), known.Max());
        var span = high - low;
        var pen = new Pen(stroke, 1.5);
        var width = Bounds.Width - 2;
        var height = Bounds.Height - 2;
        Point? last = null;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } value)
            {
                last = null; // a gap in the line where the channel is missing
                continue;
            }
            var at = new Point(
                1 + (width * i / (values.Count - 1)),
                1 + (span == 0 ? height / 2 : height * (1 - ((value - low) / span)))
            );
            if (last is { } from)
            {
                context.DrawLine(pen, from, at);
            }
            last = at;
        }
    }
}
