// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using System.Text.Json;
using AprsMap.UI.Platform;

namespace AprsMap.UI.Appearance;

/// <summary>
/// Loads and saves one viewer's <see cref="AppearanceSettings"/> through an <see cref="IPreferenceStore"/>
/// (docs/features/F0.6-appearance.md, behaviors 16–19). Loading is lenient: each value is read on its own, and one
/// it doesn't know (from a newer version) is skipped, not fatal. Saving waits for a pause in changes.
/// </summary>
public sealed class AppearancePreferences(IPreferenceStore store, TimeProvider time) : IDisposable
{
    /// <summary>The key appearance is stored under.</summary>
    public const string Key = "appearance";

    /// <summary>The format version written; older and newer files are still read value by value.</summary>
    internal const int Version = 1;

    /// <summary>How long to wait after the last change before writing (behavior 17).</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private ITimer? _timer;
    private AppearanceSettings? _pending;

    /// <summary>The stored settings, or the defaults when there are none or they can't be read (never throws).</summary>
    public async Task<AppearanceSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        string? text;
        try
        {
            text = await store.LoadAsync(Key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AppearanceSettings(); // e.g. browser storage that refuses access
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return new AppearanceSettings();
        }
        if (Read(text) is { } settings)
        {
            return settings;
        }
        await store.SetAsideAsync(Key, cancellationToken);
        return new AppearanceSettings();
    }

    /// <summary>Saves <paramref name="settings"/> once changes pause for <see cref="SaveDelay"/>.</summary>
    public void Schedule(AppearanceSettings settings)
    {
        lock (_gate)
        {
            _pending = settings;
            _timer ??= time.CreateTimer(
                _ => _ = FlushAsync(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Saves whatever is waiting now (for shutdown and tests).</summary>
    public async Task FlushAsync()
    {
        AppearanceSettings? settings;
        lock (_gate)
        {
            settings = _pending;
            _pending = null;
        }
        if (settings is not null)
        {
            try
            {
                await store.SaveAsync(Key, Write(settings));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nowhere to save (private browsing): the settings still apply for this session.
            }
        }
    }

    /// <summary>Drops a save that is waiting, for Reset the app (F0.10), which deletes the stored value instead.</summary>
    public void Discard()
    {
        lock (_gate)
        {
            _pending = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    /// <summary>The stored form: readable JSON with enum names.</summary>
    internal static string Write(AppearanceSettings s)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("version", Version);
            json.WriteString("theme", s.Theme.ToString());
            if (s.Palette is { } palette)
            {
                json.WriteString("palette", palette.ToString());
            }
            json.WriteNumber("textScale", s.TextScale);
            json.WriteString("icons", s.Icons.ToString());
            json.WriteBoolean("iconsFollowText", s.IconsFollowText);
            json.WriteString("corners", s.Corners.ToString());
            json.WriteString("density", s.Density.ToString());
            json.WriteString("motion", s.Motion.ToString());
            json.WriteNumber("mapSymbolScale", s.MapSymbolScale);
            if (s.Language is { } language)
            {
                json.WriteString("language", language);
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads what it can; null only when it isn't a JSON object at all.</summary>
    internal static AppearanceSettings? Read(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var s = new AppearanceSettings();
            if (Enum<ThemeMode>(root, "theme") is { } theme)
            {
                s = s with { Theme = theme };
            }
            if (Enum<Palette>(root, "palette") is { } palette)
            {
                s = s with { Palette = palette };
            }
            if (Number(root, "textScale") is { } textScale)
            {
                s = s with { TextScale = textScale };
            }
            if (Enum<IconSize>(root, "icons") is { } icons)
            {
                s = s with { Icons = icons };
            }
            if (
                root.TryGetProperty("iconsFollowText", out var follow)
                && follow.ValueKind is JsonValueKind.True or JsonValueKind.False
            )
            {
                s = s with { IconsFollowText = follow.GetBoolean() };
            }
            if (Enum<CornerStyle>(root, "corners") is { } corners)
            {
                s = s with { Corners = corners };
            }
            if (Enum<Density>(root, "density") is { } density)
            {
                s = s with { Density = density };
            }
            if (Enum<MotionPreference>(root, "motion") is { } motion)
            {
                s = s with { Motion = motion };
            }
            if (Number(root, "mapSymbolScale") is { } mapSymbolScale)
            {
                s = s with { MapSymbolScale = mapSymbolScale };
            }
            if (root.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String)
            {
                s = s with { Language = language.GetString() };
            }
            return s;
        }
    }

    private static T? Enum<T>(JsonElement root, string name)
        where T : struct, Enum =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && System.Enum.TryParse<T>(value.GetString(), ignoreCase: false, out var parsed)
        && System.Enum.IsDefined(parsed)
            ? parsed
            : null;

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;
}
