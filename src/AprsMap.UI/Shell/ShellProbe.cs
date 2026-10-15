// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Controls;
using Avalonia.Threading;
using Dock.Model.Core;

namespace AprsMap.UI.Shell;

/// <summary>
/// Spike only (SPIKE_DOCK_PROBE=1): reports the screens, restores a layout whose floating panel was saved on a monitor
/// that no longer exists, and prints where the OS actually put the window. Then quits.
/// </summary>
public static class ShellProbe
{
    private static void Log(string line) =>
        File.AppendAllText(
            Environment.GetEnvironmentVariable("SPIKE_DOCK_PROBE_OUT") ?? "probe.log",
            line + Environment.NewLine
        );

    public static void Run(TopLevel topLevel, ShellViewModel shell)
    {
        if (Environment.GetEnvironmentVariable("SPIKE_DOCK_PROBE") != "1" || topLevel.Screens is not { } screens)
        {
            return;
        }
        var areas = screens.All.Select(s => new ScreenArea(s.Bounds, s.WorkingArea, s.Scaling)).ToList();
        foreach (var a in areas)
        {
            Log($"PROBE screen bounds={a.Bounds} work={a.WorkingArea} scale={a.Scaling}");
        }
        Log($"PROBE fingerprint={WindowPlacement.Fingerprint(areas)}");
        screens.Changed += (_, _) => Log("PROBE screens changed");

        // Float a panel, pretend it was saved on a monitor far to the right, save and reload the layout.
        var dm = shell.Factory.FindDockable(shell.Layout, d => d.Id == "dm:W1AW-9")!;
        shell.Factory.FloatDockable(dm);
        var window = shell.Layout.Windows![0];
        window.Save();
        Log($"PROBE floated at x={window.X} y={window.Y} w={window.Width} h={window.Height}");
        window.X = 9000;
        window.Y = 300;
        var text = LayoutStore.Save(shell.Layout);
        window.Exit();
        var loaded = LayoutStore.Load(text, areas)!;
        var fitted = loaded.Windows![0];
        Log($"PROBE fitted to x={fitted.X} y={fitted.Y} w={fitted.Width} h={fitted.Height}");
        shell.Use(loaded);

        DispatcherTimer.RunOnce(
            () =>
            {
                foreach (var w in shell.Layout.Windows ?? [])
                {
                    if (w.Host is { } host)
                    {
                        host.GetPosition(out var x, out var y);
                        host.GetSize(out var width, out var height);
                        var visible = areas.Any(a =>
                            a.WorkingArea.Contains(new Avalonia.PixelPoint((int)x + 10, (int)y + 10))
                        );
                        Log($"PROBE shown at x={x} y={y} w={width} h={height} onScreen={visible}");
                    }
                    else
                    {
                        Log("PROBE window has no host (not shown)");
                    }
                }
                Environment.Exit(0);
            },
            TimeSpan.FromSeconds(3)
        );
    }
}
