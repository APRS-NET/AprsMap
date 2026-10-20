// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>
/// A part of the app that keeps files somewhere and says where (docs/features/F1.31-files.md): the tile service its
/// cache, the host its logs and the defaults for every user. The engine collects them with its own folders.
/// </summary>
public interface IStorageFolderSource
{
    /// <summary>The folders, as they are now.</summary>
    IEnumerable<StorageFolder> Folders();
}

/// <summary>A folder source from a function, for a host to register.</summary>
public sealed class StorageFolderSource(Func<IEnumerable<StorageFolder>> folders) : IStorageFolderSource
{
    /// <inheritdoc />
    public IEnumerable<StorageFolder> Folders() => folders();

    /// <summary>A folder at <paramref name="path"/>, with whether it's there now.</summary>
    public static StorageFolder At(StorageFolderKind kind, string path, bool configured) =>
        new(kind, path, configured, Directory.Exists(path));
}
