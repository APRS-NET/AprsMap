// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>A folder the app keeps something in (docs/features/F1.31-files.md, behavior 1).</summary>
public enum StorageFolderKind
{
    /// <summary>Profiles, sources, appearance and layouts.</summary>
    Settings,

    /// <summary>Message history and packet captures.</summary>
    Data,

    /// <summary>Downloaded map tiles.</summary>
    Tiles,

    /// <summary>The log files (F1.32), when the log file is on.</summary>
    Logs,

    /// <summary>Defaults for every user of the computer, read only.</summary>
    Machine,
}

/// <summary>One folder as About lists it.</summary>
/// <param name="Kind">What it holds.</param>
/// <param name="Path">Its full path, as the system writes it.</param>
/// <param name="Configured">Set in configuration rather than the system's usual place (behavior 2).</param>
/// <param name="Exists">Whether it's there yet (behavior 2).</param>
public sealed record StorageFolder(StorageFolderKind Kind, string Path, bool Configured, bool Exists);
