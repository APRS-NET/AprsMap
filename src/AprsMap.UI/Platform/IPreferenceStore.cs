// SPDX-License-Identifier: AGPL-3.0-only
namespace AprsMap.UI.Platform;

/// <summary>
/// Where one viewer's own preferences live (docs/features/F0.6-appearance.md, behavior 16): a file in the user's
/// settings folder on desktop, the browser's <c>localStorage</c> on the web. Never the engine, so on a gateway each
/// browser keeps its own. Implementations never throw for an unavailable store: they load nothing and save nowhere.
/// </summary>
public interface IPreferenceStore
{
    /// <summary>The value stored under <paramref name="key"/>, or null when there is none or it can't be read.</summary>
    Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, replacing it whole.</summary>
    Task SaveAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Moves a value that couldn't be understood out of the way (kept as <c>key.invalid</c>), so it is looked at, not lost.</summary>
    Task SetAsideAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Preferences in files, one <c>key.json</c> per key: the desktop app's store.</summary>
/// <param name="folder">The user's settings folder, the one <c>settings.json</c> is in.</param>
public sealed class FilePreferenceStore(string folder) : IPreferenceStore
{
    /// <summary>The file a key is stored in.</summary>
    public string PathOf(string key) => Path.Combine(folder, key + ".json");

    /// <inheritdoc />
    public async Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return File.Exists(PathOf(key)) ? await File.ReadAllTextAsync(PathOf(key), cancellationToken) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var temporary = PathOf(key) + ".tmp";
            await File.WriteAllTextAsync(temporary, value, cancellationToken);
            File.Move(temporary, PathOf(key), overwrite: true); // atomic: never a half-written file
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or full disk: the app keeps working with what it has.
        }
    }

    /// <inheritdoc />
    public Task SetAsideAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(PathOf(key)))
            {
                File.Move(PathOf(key), PathOf(key) + ".invalid", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }
}
