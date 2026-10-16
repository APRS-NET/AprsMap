// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Dock.Model.Controls;
using Dock.Serializer.SystemTextJson;

[assembly: DockJsonSourceGeneration]
// Dock's own types live in a referenced library, so each one a layout can contain is registered by hand. A missing one
// fails loudly when saving (a test saves every kind of layout).
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
[assembly: DockJsonSerializable(typeof(AprsMap.UI.Shell.PanelTool))]
[assembly: DockJsonSerializable(typeof(AprsMap.UI.Shell.MapDocument))]
[assembly: DockJsonSerializable(typeof(AprsMap.UI.Shell.DashboardDocument))]

namespace AprsMap.UI.Shell;

/// <summary>The main window's place: position in pixels, size in device-independent pixels (behavior 15).</summary>
public sealed record MainWindowPlacement(PixelPoint Position, Size Size, bool Maximized);

/// <summary>
/// The saved layout (behaviors 8–11): a format version, the panels this version knew when it saved (so a closed panel
/// stays closed and a new one appears), the main window's place, and Dock's own layout. Only the arrangement is
/// stored; panel content always comes from the engine. Read with <see cref="Read"/>; null means it couldn't be read.
/// </summary>
public sealed record LayoutFile(IReadOnlyList<string> Panels, MainWindowPlacement? MainWindow, IRootDock Dock)
{
    /// <summary>The format written. A file with a newer format is treated as unreadable.</summary>
    public const int Format = 1;

    private static readonly DockSerializer Serializer = DockSystemTextJsonGenerated.CreateSerializer();

    /// <summary>The stored text.</summary>
    public string Write()
    {
        var file = new JsonObject
        {
            ["format"] = Format,
            ["panels"] = new JsonArray([.. Panels.Select(p => (JsonNode?)JsonValue.Create(p))]),
            ["dock"] = JsonNode.Parse(Serializer.Serialize(Dock)),
        };
        if (MainWindow is { } main)
        {
            file["mainWindow"] = new JsonObject
            {
                ["x"] = main.Position.X,
                ["y"] = main.Position.Y,
                ["width"] = main.Size.Width,
                ["height"] = main.Size.Height,
                ["maximized"] = main.Maximized,
            };
        }
        return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads stored text, or returns null when it isn't a layout this version can read.</summary>
    public static LayoutFile? Read(string text)
    {
        try
        {
            if (
                JsonNode.Parse(text) is not JsonObject file
                || file["format"]?.GetValue<int>() is not (>= 1 and <= Format)
            )
            {
                return null;
            }
            var dock = file["dock"] is JsonObject dockNode
                ? Serializer.Deserialize<IRootDock>(dockNode.ToJsonString())
                : null;
            if (dock is null)
            {
                return null;
            }
            var panels = file["panels"] is JsonArray list
                ? list.Select(p => p?.GetValue<string>()).OfType<string>().ToList()
                : [];
            return new LayoutFile(panels, ReadMainWindow(file["mainWindow"]), dock);
        }
        catch (Exception ex)
            when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            return null; // not JSON, or a hand-edited file with a type Dock can't make
        }
    }

    /// <summary>Just the main window's place, for placing the window before the rest of the app starts.</summary>
    public static MainWindowPlacement? ReadMainWindow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(text) is JsonObject file ? ReadMainWindow(file["mainWindow"]) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static MainWindowPlacement? ReadMainWindow(JsonNode? node)
    {
        if (node is not JsonObject main)
        {
            return null;
        }
        var width = main["width"]?.GetValue<double>() ?? 0;
        var height = main["height"]?.GetValue<double>() ?? 0;
        if (width < 1 || height < 1 || double.IsNaN(width) || double.IsNaN(height))
        {
            return null;
        }
        return new MainWindowPlacement(
            new PixelPoint(main["x"]?.GetValue<int>() ?? 0, main["y"]?.GetValue<int>() ?? 0),
            new Size(width, height),
            main["maximized"]?.GetValue<bool>() ?? false
        );
    }

    /// <summary>For logs: the format and panel count.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"layout v{Format}, {Panels.Count} panels");
}
