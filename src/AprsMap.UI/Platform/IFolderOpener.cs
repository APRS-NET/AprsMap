// SPDX-License-Identifier: AGPL-3.0-only
namespace AprsMap.UI.Platform;

/// <summary>
/// Opens a folder in the system's file manager (docs/features/F1.31-files.md, behavior 4a): the desktop app's, absent in
/// the browser, which can't open a folder on the server or on its own computer.
/// </summary>
public interface IFolderOpener
{
    /// <summary>Opens <paramref name="path"/>; false when the system couldn't.</summary>
    Task<bool> OpenAsync(string path);
}
