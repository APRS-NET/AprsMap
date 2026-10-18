// SPDX-License-Identifier: MIT
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>Where the engine keeps <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>The stored settings, or null when nothing has been stored yet (first run).</summary>
    Task<AppSettings?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="settings"/>, replacing what was there.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    /// <summary>Deletes everything stored, including copies set aside, so the next load is a first run (F0.10).</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Settings as one JSON file, <c>settings.json</c>, in a folder of its own. Writes go to a temporary file that then
/// replaces the old one, so a crash mid-write never leaves half a file. A file that can't be read (edited by hand, say)
/// is moved aside to <c>settings.json.invalid</c> and treated as a first run, rather than stopping the app.
/// </summary>
public sealed class FileSettingsStore(string folder) : ISettingsStore
{
    /// <summary>The settings file's name inside the folder.</summary>
    public const string FileName = "settings.json";

    /// <summary>What a settings file from before sources is kept as, beside the converted one.</summary>
    public const string VersionOneSuffix = ".v1";

    /// <summary>The settings file's full path.</summary>
    public string FilePath { get; } = Path.Combine(folder, FileName);

    /// <inheritdoc />
    public async Task<AppSettings?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        AppSettings? settings;
        var migrated = false;
        try
        {
            var text = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(text) as JsonObject;
            if (root is not null && SettingsMigration.IsBeforeSources(root))
            {
                SettingsMigration.ToSources(root); // F1.25: servers and filters move to sources
                migrated = true;
            }
            settings = root?.Deserialize(SettingsJsonContext.Default.AppSettings);
        }
        catch (JsonException)
        {
            settings = null;
        }

        if (settings?.Profiles is null || settings.Units is null)
        {
            File.Move(FilePath, FilePath + ".invalid", overwrite: true);
            return null;
        }
        if (migrated)
        {
            // The old file is kept beside the new one, once (F1.25 acceptance test 1).
            if (!File.Exists(FilePath + VersionOneSuffix))
            {
                File.Copy(FilePath, FilePath + VersionOneSuffix);
            }
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        return settings;
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { FilePath, FilePath + ".invalid", FilePath + ".tmp", FilePath + VersionOneSuffix })
        {
            File.Delete(path); // no error when it doesn't exist
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(folder);
        var temporary = FilePath + ".tmp";
        var stream = File.Create(temporary);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer
                .SerializeAsync(
                    stream,
                    settings with
                    {
                        ReadOnly = false,
                    },
                    SettingsJsonContext.Default.AppSettings,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        File.Move(temporary, FilePath, overwrite: true);
    }
}

/// <summary>The per-user settings folder for each operating system (docs/scope.md#settings-storage-and-profiles).</summary>
public static class SettingsLocation
{
    /// <summary>The vendor folder that groups the products' folders.</summary>
    public const string VendorFolder = AppFolders.VendorFolder;

    /// <summary>The current user's settings folder for the product <paramref name="slug"/> on this machine.</summary>
    public static string UserFolder(string slug) => AppFolders.For(slug).Settings;

    /// <summary>
    /// When <paramref name="folder"/> has no settings yet, copies them from the first folder of
    /// <paramref name="previousFolders"/> that has some, so a renamed product keeps the user's settings
    /// (docs/branding.md). The old folder is left in place. Returns the folder it copied from, if any.
    /// </summary>
    public static string? MigrateIfMissing(string folder, IEnumerable<string> previousFolders)
    {
        ArgumentNullException.ThrowIfNull(previousFolders);
        if (File.Exists(Path.Combine(folder, FileSettingsStore.FileName)))
        {
            return null;
        }

        foreach (var previous in previousFolders)
        {
            var file = Path.Combine(previous, FileSettingsStore.FileName);
            if (File.Exists(file))
            {
                Directory.CreateDirectory(folder);
                File.Copy(file, Path.Combine(folder, FileSettingsStore.FileName));
                return previous;
            }
        }
        return null;
    }

    /// <summary>
    /// The store for <paramref name="options"/>: <see cref="EngineOptions.SettingsFolder"/> when set, otherwise the
    /// user's folder, migrated from the folders of the product's previous names.
    /// </summary>
    public static FileSettingsStore CreateStore(EngineOptions options) => new(Folder(options));

    /// <summary>
    /// The settings folder for <paramref name="options"/>: <see cref="EngineOptions.SettingsFolder"/> when set,
    /// otherwise the user's folder (<see cref="UserFolders"/>). The desktop app keeps the viewer's own preferences here
    /// too (F0.6).
    /// </summary>
    public static string Folder(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.SettingsFolder) ? UserFolders().Settings : options.SettingsFolder;
    }

    /// <summary>
    /// Where message history and captures go for <paramref name="options"/>: <see cref="EngineOptions.DataFolder"/>
    /// when set; otherwise <see cref="EngineOptions.SettingsFolder"/> when that's set, so a gateway's volume holds
    /// everything; otherwise the user's data folder.
    /// </summary>
    public static string DataFolder(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !string.IsNullOrWhiteSpace(options.DataFolder) ? options.DataFolder
            : !string.IsNullOrWhiteSpace(options.SettingsFolder) ? options.SettingsFolder
            : UserFolders().Data;
    }

    /// <summary>The current user's folders. Only works them out; <see cref="PrepareUserFolders"/> makes them ready.</summary>
    public static AppFolders UserFolders() => AppFolders.For(Brand.Slug);

    /// <summary>
    /// Makes the current user's folders ready, once, as the desktop app starts: creates them, moves an earlier
    /// version's files in and copies a previous name's settings (docs/branding.md). Nothing else moves files, so
    /// looking a folder up, as the tests and the gateway do, never touches what's on the machine.
    /// </summary>
    public static AppFolders PrepareUserFolders() =>
        Prepare(UserFolders(), AppFolders.Legacy(Brand.Slug, AppFolders.FolderEnvironment.Current));

    internal static AppFolders Prepare(AppFolders folders, string? legacy)
    {
        folders.Create();
        folders.MoveFromLegacy(legacy);
        MigrateIfMissing(folders.Settings, Brand.PreviousSlugs.Select(UserFolder));
        return folders;
    }
}

/// <summary>The settings file's JSON: readable, with enum names rather than numbers.</summary>
[ExcludeFromCodeCoverage] // generated code
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
