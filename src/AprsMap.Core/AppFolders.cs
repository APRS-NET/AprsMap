// SPDX-License-Identifier: MIT
namespace AprsMap.Core;

/// <summary>
/// Where a desktop install keeps its files, following each operating system's conventions (docs/scope.md#settings-storage-and-profiles,
/// docs/features/F4.15-updates.md behavior 13). Nothing lives beside the program, so an update, an uninstall or a
/// read-only Program Files leaves it alone.
/// </summary>
/// <param name="Settings">Settings, profiles and the viewer's preferences: small, and roaming on Windows.</param>
/// <param name="Data">Message history and packet captures: they grow, so they stay on this machine.</param>
/// <param name="Cache">What can be fetched again, such as map tiles.</param>
/// <param name="Machine">
/// Folders an administrator may put an <c>appsettings.json</c> in for every user of the machine, most important first;
/// the app only reads them.
/// </param>
public sealed record AppFolders(string Settings, string Data, string Cache, IReadOnlyList<string> Machine)
{
    /// <summary>The vendor folder that groups the products' folders.</summary>
    public const string VendorFolder = "aprsnet";

    /// <summary>The current user's folders for the product <paramref name="slug"/> on this machine.</summary>
    public static AppFolders For(string slug) => For(slug, FolderEnvironment.Current);

    internal static AppFolders For(string slug, FolderEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        string Under(string root) => Path.Combine(root, VendorFolder, slug);
        switch (env.Platform)
        {
            case OSPlatform.Windows:
                // %APPDATA% roams with the user; %LOCALAPPDATA% doesn't, so what grows stays there.
                return new(
                    Under(env.RoamingAppData),
                    Under(env.LocalAppData),
                    Under(env.LocalAppData),
                    [Under(env.CommonAppData)]
                );
            case OSPlatform.MacOS:
                var library = Path.Combine(env.Home, "Library");
                var support = Under(Path.Combine(library, "Application Support"));
                return new(
                    support,
                    support,
                    Under(Path.Combine(library, "Caches")),
                    [Under(Path.Combine(Path.DirectorySeparatorChar + "Library", "Application Support"))]
                );
            default:
                // The XDG Base Directory specification: a variable that's unset, empty or relative is ignored.
                var configDirs = (env.Variable("XDG_CONFIG_DIRS") ?? string.Empty)
                    .Split(':', StringSplitOptions.RemoveEmptyEntries)
                    .Where(Path.IsPathRooted)
                    .ToList();
                return new(
                    Under(Xdg(env, "XDG_CONFIG_HOME", ".config")),
                    Under(Xdg(env, "XDG_DATA_HOME", Path.Combine(".local", "share"))),
                    Under(Xdg(env, "XDG_CACHE_HOME", ".cache")),
                    [
                        .. (
                            configDirs.Count > 0
                                ? configDirs
                                : [Path.DirectorySeparatorChar + Path.Combine("etc", "xdg")]
                        ).Select(Under),
                    ]
                );
        }
    }

    /// <summary>
    /// Where earlier versions kept their files, to move from (<see cref="MoveFromLegacy"/>): one folder,
    /// <c>~/.aprsnet/&lt;slug&gt;</c>, on Linux; on Windows message history and captures sat with the settings in
    /// %APPDATA%. macOS already used its own folders.
    /// </summary>
    internal static string? Legacy(string slug, FolderEnvironment env) =>
        env.Platform switch
        {
            OSPlatform.Windows => Path.Combine(env.RoamingAppData, VendorFolder, slug),
            OSPlatform.MacOS => null,
            _ => Path.Combine(env.Home, "." + VendorFolder, slug),
        };

    private static string Xdg(FolderEnvironment env, string variable, string fallback) =>
        env.Variable(variable) is { Length: > 0 } value && Path.IsPathRooted(value)
            ? value
            : Path.Combine(env.Home, fallback);

    /// <summary>
    /// Creates the settings, data and cache folders, readable only by their owner on Linux and macOS: the settings
    /// hold passcodes.
    /// </summary>
    public void Create()
    {
        foreach (var folder in new[] { Settings, Data, Cache }.Distinct(StringComparer.Ordinal))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                Directory.CreateDirectory(
                    folder,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
        }
    }

    /// <summary>
    /// Moves what an earlier version kept in <paramref name="legacy"/> to these folders: message history
    /// (<c>messages-*.json</c>) and captures to <see cref="Data"/>, everything else to <see cref="Settings"/>. Nothing
    /// already in place is replaced, and the old folder goes once it's empty. Returns how many entries were moved.
    /// </summary>
    public int MoveFromLegacy(string? legacy)
    {
        if (legacy is null || !Directory.Exists(legacy))
        {
            return 0;
        }
        var moved = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(legacy).ToList())
        {
            var name = Path.GetFileName(entry);
            var isData =
                name.StartsWith("messages-", StringComparison.Ordinal)
                || string.Equals(name, PacketCaptureFolder, StringComparison.Ordinal);
            var target = Path.Combine(isData ? Data : Settings, name);
            if (SamePath(entry, target) || Path.Exists(target))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Move(entry, target);
            moved++;
        }
        RemoveIfEmpty(legacy);
        RemoveIfEmpty(Path.GetDirectoryName(legacy));
        return moved;
    }

    /// <summary>The captures' folder name in <see cref="Data"/> (docs/features/F1.27-packet-capture.md).</summary>
    public const string PacketCaptureFolder = "capture";

    private static void Move(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Move(from, to); // copies and deletes across volumes
            return;
        }
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException)
        {
            // Another volume: copy, then delete the original.
            CopyTree(from, to);
            Directory.Delete(from, recursive: true);
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            CopyTree(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }

    private static void RemoveIfEmpty(string? folder)
    {
        if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        );

    internal enum OSPlatform
    {
        Windows,
        MacOS,
        Other,
    }

    /// <summary>What the folders are worked out from; the real machine's, or a test's.</summary>
    internal sealed record FolderEnvironment(
        OSPlatform Platform,
        string Home,
        string RoamingAppData,
        string LocalAppData,
        string CommonAppData,
        Func<string, string?> Variable
    )
    {
        public static FolderEnvironment Current =>
            new(
                OperatingSystem.IsWindows() ? OSPlatform.Windows
                    : OperatingSystem.IsMacOS() ? OSPlatform.MacOS
                    : OSPlatform.Other,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetEnvironmentVariable
            );
    }
}
