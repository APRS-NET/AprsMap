// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices.JavaScript;
using AprsMap.Contracts;
using AprsMap.UI.Platform;

namespace AprsMap.Browser;

/// <summary>
/// Panels in tabs of their own (docs/features/F1.24-web-pop-out.md): the query that names the panel a tab shows, opening
/// one, and the selection shared between the app's tabs with a <c>BroadcastChannel</c> (same origin only).
/// </summary>
internal static partial class BrowserPopOut
{
    /// <summary>The query parameter that names the panel a tab shows alone.</summary>
    public const string PanelParameter = "panel";

    /// <summary>The panel <paramref name="page"/> asks for, or null for the whole app.</summary>
    public static string? PanelOf(Uri page) =>
        System.Web.HttpUtility.ParseQueryString(page.Query)[PanelParameter] is { Length: > 0 } kind ? kind : null;

    /// <summary>The address of a tab showing <paramref name="kind"/> alone, on the gateway at <paramref name="gateway"/>.</summary>
    public static Uri PageFor(Uri gateway, string kind) =>
        new(gateway, $"?{PanelParameter}={Uri.EscapeDataString(kind)}");

    /// <summary>Opens <paramref name="page"/> in a new tab.</summary>
    [JSImport("openTab", "main.js")]
    public static partial void OpenTab(string page);

    [JSImport("selectionOpen", "main.js")]
    private static partial void SelectionOpen(
        string name,
        [JSMarshalAs<JSType.Function<JSType.String>>] Action<string> receive
    );

    [JSImport("selectionPublish", "main.js")]
    private static partial void SelectionPublish(string station);

    /// <summary>The selection shared between this browser's tabs of the app.</summary>
    public sealed class Channel : ISelectionChannel
    {
        public Channel() =>
            SelectionOpen($"{Brand.Slug}.selection", station => Received?.Invoke(station.Length > 0 ? station : null));

        public event Action<string?>? Received;

        public void Publish(string? station) => SelectionPublish(station ?? string.Empty);
    }
}
