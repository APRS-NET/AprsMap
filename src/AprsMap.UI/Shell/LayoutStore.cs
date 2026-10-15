// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Serializer.SystemTextJson;

[assembly: DockJsonSourceGeneration]
// Dock's own types live in a referenced library, so each one a layout can contain is registered by hand.
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.DockDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Core.DockWindow))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.Document))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.DocumentDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.GridDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.GridDockSplitter))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.ProportionalDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.ProportionalDockSplitter))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.RootDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.SplitViewDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.StackDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.Tool))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.ToolDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.UniformGridDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Mvvm.Controls.WrapDock))]

namespace AprsMap.UI.Shell;

/// <summary>
/// Saves and loads layouts as text (System.Text.Json, source-generated so it survives trimming), and fits every
/// floating window of a loaded layout to the current screens before anything is shown.
/// </summary>
public static class LayoutStore
{
    private static readonly DockSerializer Serializer = DockSystemTextJsonGenerated.CreateSerializer();

    public static string Save(IRootDock layout) => Serializer.Serialize(layout);

    /// <summary>Loads a layout, or null when the text isn't a layout this version can read.</summary>
    public static IRootDock? Load(string text, IReadOnlyList<ScreenArea> screens)
    {
        IRootDock? layout;
        try
        {
            layout = Serializer.Deserialize<IRootDock>(text);
        }
        catch (JsonException)
        {
            return null;
        }
        if (layout is not null)
        {
            FitWindows(layout, screens);
        }
        return layout;
    }

    /// <summary>Moves every saved floating window (and their nested windows) onto a screen that exists.</summary>
    public static void FitWindows(IRootDock layout, IReadOnlyList<ScreenArea> screens)
    {
        foreach (var window in layout.Windows ?? [])
        {
            var (position, size) = WindowPlacement.Fit(
                new Avalonia.PixelPoint((int)window.X, (int)window.Y),
                new Avalonia.Size(window.Width, window.Height),
                screens
            );
            window.X = position.X;
            window.Y = position.Y;
            window.Width = size.Width;
            window.Height = size.Height;
            if (window.Layout is { } nested)
            {
                FitWindows(nested, screens);
            }
        }
    }

    /// <summary>Fits floating windows that are already open, after a monitor is added or removed.</summary>
    public static void FitOpenWindows(IRootDock layout, IReadOnlyList<ScreenArea> screens)
    {
        foreach (var window in layout.Windows ?? [])
        {
            if (window.Host is not { } host)
            {
                continue;
            }
            host.GetPosition(out var x, out var y);
            host.GetSize(out var width, out var height);
            var (position, size) = WindowPlacement.Fit(
                new Avalonia.PixelPoint((int)x, (int)y),
                new Avalonia.Size(width, height),
                screens
            );
            if (position.X != (int)x || position.Y != (int)y)
            {
                host.SetPosition(position.X, position.Y);
            }
            if (size.Width < width || size.Height < height)
            {
                host.SetSize(size.Width, size.Height);
            }
        }
    }
}
