// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia;
using Avalonia.Controls;

namespace AprsMap.UI.Shell;

/// <summary>
/// The desktop main window's part of the layout (F1.0 behavior 15): placed where it was, if that is still on a screen,
/// and its place recorded as it moves. Only the size and place while not maximized are kept, so un-maximizing after a
/// restart goes back to them.
/// </summary>
internal sealed class MainWindowLayout
{
    private PixelPoint _position;
    private Size _size;

    public MainWindowLayout(Window window, string? saved)
    {
        Window = window;
        Saved = LayoutFile.ReadMainWindow(saved);
        _size = new Size(window.Width, window.Height);
        window.PositionChanged += (_, e) => Remember(() => _position = e.Point);
        window.Resized += (_, e) => Remember(() => _size = e.ClientSize);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public Window Window { get; }

    /// <summary>The place read from the saved layout, or null.</summary>
    public MainWindowPlacement? Saved { get; }

    /// <summary>Raised when the window moves or resizes, so the layout is saved.</summary>
    public event EventHandler? Changed;

    /// <summary>The screens as the placement check sees them.</summary>
    public IReadOnlyList<ScreenArea> Screens() =>
        [.. Window.Screens.All.Select(s => new ScreenArea(s.Bounds, s.WorkingArea, s.Scaling))];

    /// <summary>Puts the window where it was, fitted to <paramref name="screens"/>, before it shows.</summary>
    public void Restore(IReadOnlyList<ScreenArea> screens)
    {
        if (Saved is not { } saved)
        {
            return;
        }
        var (position, size) = WindowPlacement.Fit(saved.Position, saved.Size, screens);
        Window.WindowStartupLocation = WindowStartupLocation.Manual;
        Window.Position = position;
        Window.Width = size.Width;
        Window.Height = size.Height;
        _position = position;
        _size = size;
        if (saved.Maximized)
        {
            Window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Where the window is now, for saving.</summary>
    public MainWindowPlacement Placement() => new(_position, _size, Window.WindowState == WindowState.Maximized);

    private void Remember(Action record)
    {
        if (Window.WindowState == WindowState.Normal)
        {
            record();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
