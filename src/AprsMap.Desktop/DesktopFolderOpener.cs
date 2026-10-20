// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace AprsMap.Desktop;

/// <summary>
/// Open folder for Help › About (docs/features/F1.31-files.md, behavior 4a), through Avalonia's launcher, which hands the
/// folder to Explorer, Finder or the desktop's file manager.
/// </summary>
internal sealed class DesktopFolderOpener : IFolderOpener
{
    public async Task<bool> OpenAsync(string path)
    {
        if (
            !Directory.Exists(path)
            || (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
                is not TopLevel window
        )
        {
            return false;
        }
        try
        {
            return await window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
