// SPDX-License-Identifier: AGPL-3.0-only
namespace AprsMap.UI.Platform;

/// <summary>
/// The selected station shared between the app's tabs in one browser (docs/features/F1.24-web-pop-out.md, behavior 3):
/// what one tab selects, the others select too.
/// </summary>
public interface ISelectionChannel
{
    /// <summary>Tells the other tabs that <paramref name="station"/> is selected, or nothing (null).</summary>
    void Publish(string? station);

    /// <summary>Another tab selected a station, or nothing (null).</summary>
    event Action<string?>? Received;
}
