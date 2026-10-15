// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia;

namespace AprsMap.UI.Shell;

/// <summary>A screen as the placement check sees it: its bounds and usable area in pixels, and its scale.</summary>
public readonly record struct ScreenArea(PixelRect Bounds, PixelRect WorkingArea, double Scaling);

/// <summary>
/// Where a window may be restored. A saved position is only used when a usable piece of the window's title bar lands
/// inside a screen's working area; otherwise the window is moved onto the nearest screen. This is what keeps a window
/// saved on a second monitor from reopening in coordinates that no longer exist.
/// </summary>
public static class WindowPlacement
{
    /// <summary>The height of the strip that must be reachable, in device-independent pixels.</summary>
    public const double TitleStripHeight = 32;

    /// <summary>How much of the title strip's width must be inside a screen, in device-independent pixels.</summary>
    public const double MinimumVisibleWidth = 120;

    /// <summary>
    /// Fits a window's saved placement to the current screens. Position is in pixels (as Avalonia's
    /// <c>Window.Position</c>), size in device-independent pixels (as <c>Window.Width</c>/<c>Height</c>).
    /// </summary>
    public static (PixelPoint Position, Size Size) Fit(PixelPoint position, Size size, IReadOnlyList<ScreenArea> screens)
    {
        if (screens.Count == 0)
        {
            return (position, size);
        }

        foreach (var screen in screens)
        {
            if (TitleStripIsReachable(position, size, screen))
            {
                return (position, ShrinkToFit(size, screen));
            }
        }

        // Off every screen: move it to the nearest one, keeping it as close to where it was as the screen allows.
        var centre = new PixelPoint(
            position.X + (int)(size.Width * screens[0].Scaling / 2),
            position.Y + (int)(size.Height * screens[0].Scaling / 2)
        );
        var target = screens.MinBy(s => DistanceSquared(centre, s.WorkingArea));
        var fitted = ShrinkToFit(size, target);
        var work = target.WorkingArea;
        var width = (int)Math.Ceiling(fitted.Width * target.Scaling);
        var height = (int)Math.Ceiling(fitted.Height * target.Scaling);
        var x = Math.Clamp(position.X, work.X, Math.Max(work.X, work.Right - width));
        var y = Math.Clamp(position.Y, work.Y, Math.Max(work.Y, work.Bottom - height));
        return (new PixelPoint(x, y), fitted);
    }

    /// <summary>A key for the current monitor arrangement: the same screens in the same places give the same key.</summary>
    public static string Fingerprint(IEnumerable<ScreenArea> screens) =>
        string.Join(
            ";",
            screens
                .Select(s =>
                    FormattableString.Invariant(
                        $"{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width}x{s.Bounds.Height}@{s.Scaling:0.##}"
                    )
                )
                .Order(StringComparer.Ordinal)
        );

    private static bool TitleStripIsReachable(PixelPoint position, Size size, ScreenArea screen)
    {
        var strip = new PixelRect(
            position.X,
            position.Y,
            (int)(size.Width * screen.Scaling),
            (int)(TitleStripHeight * screen.Scaling)
        );
        var visible = strip.Intersect(screen.WorkingArea);
        return visible.Width >= MinimumVisibleWidth * screen.Scaling && visible.Height >= strip.Height / 2;
    }

    private static Size ShrinkToFit(Size size, ScreenArea screen) =>
        new(
            Math.Min(size.Width, screen.WorkingArea.Width / screen.Scaling),
            Math.Min(size.Height, screen.WorkingArea.Height / screen.Scaling)
        );

    private static long DistanceSquared(PixelPoint point, PixelRect rect)
    {
        long dx = Math.Max(0, Math.Max(rect.X - point.X, point.X - rect.Right));
        long dy = Math.Max(0, Math.Max(rect.Y - point.Y, point.Y - rect.Bottom));
        return dx * dx + dy * dy;
    }
}
