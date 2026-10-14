// SPDX-License-Identifier: MIT
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
        var stream = File.OpenRead(FilePath);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                settings = await JsonSerializer
                    .DeserializeAsync(stream, SettingsJsonContext.Default.AppSettings, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                settings = null;
            }
        }

        if (settings?.Profiles is null || settings.Units is null)
        {
            File.Move(FilePath, FilePath + ".invalid", overwrite: true);
            return null;
        }
        return settings;
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
    public const string VendorFolder = "aprsnet";

    /// <summary>The current user's settings folder for the product <paramref name="slug"/> on this machine.</summary>
    public static string UserFolder(string slug) =>
        UserFolder(
            slug,
            OperatingSystem.IsWindows() ? OSPlatform.Windows
                : OperatingSystem.IsMacOS() ? OSPlatform.MacOS
                : OSPlatform.Other,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        );

    internal static string UserFolder(string slug, OSPlatform platform, string home, string appData) =>
        platform switch
        {
            OSPlatform.Windows => Path.Combine(appData, VendorFolder, slug),
            OSPlatform.MacOS => Path.Combine(home, "Library", "Application Support", VendorFolder, slug),
            _ => Path.Combine(home, "." + VendorFolder, slug),
        };

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
    public static FileSettingsStore CreateStore(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.SettingsFolder))
        {
            return new FileSettingsStore(options.SettingsFolder);
        }

        var folder = UserFolder(Brand.Slug);
        MigrateIfMissing(folder, Brand.PreviousSlugs.Select(UserFolder));
        return new FileSettingsStore(folder);
    }

    internal enum OSPlatform
    {
        Windows,
        MacOS,
        Other,
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
