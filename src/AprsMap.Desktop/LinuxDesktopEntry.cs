// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using AprsMap.Contracts;

namespace AprsMap.Desktop;

/// <summary>
/// Puts an installed AppImage in the applications menu (docs/features/F4.15-updates.md, behavior 12.3): a desktop entry
/// in <c>$XDG_DATA_HOME/applications</c> and its icon in the hicolor theme, refreshed on each start so a moved AppImage
/// is still found. Velopack updates the AppImage in place, so the entry's path stays right across updates.
/// </summary>
internal static class LinuxDesktopEntry
{
    /// <summary>The freedesktop.org menu categories: amateur radio, under Network.</summary>
    public const string Categories = "Network;HamRadio;";

    public static void Refresh()
    {
        if (
            !OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("APPIMAGE") is not { Length: > 0 } appImage
        )
        {
            return;
        }
        var dataHome =
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        try
        {
            using (var icon = typeof(LinuxDesktopEntry).Assembly.GetManifestResourceStream("icon-256.png"))
            {
                if (icon is not null)
                {
                    var iconFile = Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", Brand.Slug + ".png");
                    if (!File.Exists(iconFile))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(iconFile)!);
                        using var file = File.Create(iconFile);
                        icon.CopyTo(file);
                    }
                }
            }
            var entry = Entry(appImage);
            var entryFile = Path.Combine(dataHome, "applications", Brand.Slug + ".desktop");
            if (!File.Exists(entryFile) || File.ReadAllText(entryFile) != entry)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entryFile)!);
                File.WriteAllText(entryFile, entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the entry the AppImage still runs; it's only missing from the menu.
        }
    }

    /// <summary>The desktop entry, with the AppImage's path quoted as the Desktop Entry specification asks.</summary>
    internal static string Entry(string appImage)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in appImage)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }
            quoted.Append(c);
        }
        quoted.Append('"');
        return $"""
            [Desktop Entry]
            Type=Application
            Name={Brand.DisplayName}
            Exec={quoted}
            Icon={Brand.Slug}
            Categories={Categories}
            Terminal=false

            """;
    }
}
