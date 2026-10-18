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

    /// <summary>Whether the scale starts at zero (counts) rather than at the lowest value (telemetry).</summary>
    public static readonly StyledProperty<bool> FromZeroProperty = AvaloniaProperty.Register<Sparkline, bool>(
        nameof(FromZero)
    );

    /// <summary>
    /// Each point in words, "12 at 14:05" (docs/features/F1.22-statistics.md, behavior 6); with them the line can be
    /// hovered and focused, and Left and Right step through the points.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty = AvaloniaProperty.Register<
        Sparkline,
        IReadOnlyList<string>?
    >(nameof(Labels));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, FromZeroProperty, LabelsProperty);
        LabelsProperty.Changed.AddClassHandler<Sparkline>((s, _) => s.Focusable = s.Labels is { Count: > 0 });
    }

    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    /// <summary>The point picked by the pointer or the keyboard, or null.</summary>
    internal int? Point { get; private set; }

    /// <summary>Picks the point nearest <paramref name="x"/> and says it.</summary>
    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Values is { Count: > 1 } values)
        {
            var x = e.GetPosition(this).X;
            Pick((int)Math.Round((x - 1) / Math.Max(1, Bounds.Width - 2) * (values.Count - 1)));
        }
    }

    /// <inheritdoc />
    protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!IsFocused)
        {
            Unpick();
        }
    }

    /// <inheritdoc />
    protected override void OnGotFocus(Avalonia.Input.FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        Pick(Point ?? (Values?.Count ?? 1) - 1); // the newest first
    }

    /// <inheritdoc />
    protected override void OnLostFocus(Avalonia.Input.FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        Unpick();
    }

    /// <summary>Left and Right step through the points (behavior 6); Home and End go to the first and last.</summary>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Values is not { Count: > 0 } values || Labels is null)
        {
            return;
        }
        var at = Point ?? values.Count - 1;
        int? next = e.Key switch
        {
            Avalonia.Input.Key.Left => Math.Max(0, at - 1),
            Avalonia.Input.Key.Right => Math.Min(values.Count - 1, at + 1),
            Avalonia.Input.Key.Home => 0,
            Avalonia.Input.Key.End => values.Count - 1,
            _ => null,
        };
        if (next is { } index)
        {
            Pick(index);
            e.Handled = true;
        }
    }

    private void Pick(int index)
    {
        if (Labels is not { Count: > 0 } labels)
        {
            return;
        }
        index = Math.Clamp(index, 0, labels.Count - 1);
        Point = index;
        ToolTip.SetTip(this, labels[index]);
        ToolTip.SetIsOpen(this, true);
        Avalonia.Automation.AutomationProperties.SetName(this, labels[index]);
        InvalidateVisual();
    }

    private void Unpick()
    {
        Point = null;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    public bool FromZero
    {
        get => GetValue(FromZeroProperty);
        set => SetValue(FromZeroProperty, value);
    }

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
        var (low, high) = (FromZero ? Math.Min(0, known.Min()) : known.Min(), known.Max());
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
            if (i == Point)
            {
                context.DrawEllipse(stroke, null, at, 3, 3); // the point picked (behavior 6)
            }
            last = at;
        }
    }
}
