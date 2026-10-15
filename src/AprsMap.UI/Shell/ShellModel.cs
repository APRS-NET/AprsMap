// SPDX-License-Identifier: AGPL-3.0-only
using Dock.Model.Mvvm.Controls;

namespace AprsMap.UI.Shell;

/// <summary>
/// A panel in the shell: packets, messages, a direct-message thread, stations, details. Its <see cref="Kind"/> picks
/// the view; its Id is unique per instance (two message threads are two panels). Only Id, kind and title are saved
/// with a layout; the view model is attached again by the factory when a layout is loaded.
/// </summary>
public class PanelTool : Tool
{
    /// <summary>Which panel this is: <c>packets</c>, <c>messages</c>, <c>dm</c>, <c>stations</c>, <c>details</c>.</summary>
    public string Kind { get; set; } = string.Empty;
}

/// <summary>The map, the centre of the layout. A document, so tools dock around it and it can't be closed.</summary>
public class MapDocument : Document;
