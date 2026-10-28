// SPDX-License-Identifier: MIT
using System.Globalization;

namespace AprsMap.Contracts;

/// <summary>
/// A View filter saved under a name in a profile (docs/features/F1.42-saved-filters.md, behaviors 1–3), with the map
/// view it moves to, if any.
/// </summary>
/// <param name="Id">Its id, kept as it's renamed.</param>
/// <param name="Name">"Storm spotting": unique in the profile, up to <see cref="MaxNameLength"/> characters.</param>
/// <param name="Filter">The conditions; station lists by id, so editing a list changes what it shows (behavior 3).</param>
/// <param name="View">Where switching to it moves the map; null leaves the map where it is.</param>
public sealed record SavedFilter(string Id, string Name, StationFilter Filter, SavedMapView? View = null)
{
    /// <summary>The most saved filters a profile has (decision 5).</summary>
    public const int MaxFilters = 20;

    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 40;

    /// <summary>A new saved filter with a new id.</summary>
    public static SavedFilter Create(string name, StationFilter filter, SavedMapView? view = null) =>
        new(Guid.NewGuid().ToString("N"), name.Trim(), filter, view);
}

/// <summary>A map view kept with a saved filter (decision 2): its centre and zoom level.</summary>
/// <param name="Latitude">The centre's latitude.</param>
/// <param name="Longitude">The centre's longitude.</param>
/// <param name="Zoom">The zoom level, as tile maps number them (0 the whole world, 10 a county).</param>
public sealed record SavedMapView(double Latitude, double Longitude, double Zoom);

/// <summary>A saved filter's text read (behavior 9): what it says, and what couldn't be read.</summary>
/// <param name="Filter">The conditions read, lists by the ids of the lists named.</param>
/// <param name="View">The map view, if the text has one.</param>
/// <param name="Errors">The parts that couldn't be read, as written.</param>
/// <param name="MissingLists">Lists named that the profile doesn't have; left out of <paramref name="Filter"/>.</param>
public sealed record SavedFilterParse(
    StationFilter Filter,
    SavedMapView? View,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> MissingLists
);

/// <summary>
/// A saved filter as one line of plain text (docs/features/F1.42-saved-filters.md, behavior 9), read and written the
/// same way everywhere: parts as <c>key: value</c> separated by <c>;</c>, lists by name (decision 6), such as
/// <c>lists: Skywarn spotters; categories: Weather; heard: 30 min; view: 38.9 -94.7 z10</c>.
/// </summary>
public static class SavedFilterText
{
    /// <summary>The filter <paramref name="filter"/>'s line, with <paramref name="lists"/> to name its lists.</summary>
    public static string Format(StationFilter filter, SavedMapView? view, IReadOnlyList<StationList> lists)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(lists);
        var parts = new List<string>();
        if (filter.Lists is { Count: > 0 } ids)
        {
            parts.Add(
                "lists: " + string.Join(", ", ids.Select(id => lists.FirstOrDefault(l => l.Id == id)?.Name ?? id))
            );
        }
        if (filter.Kinds != StationKinds.All)
        {
            parts.Add("kinds: " + filter.Kinds);
        }
        Add("categories", filter.Categories);
        if (filter.HeardWithinMinutes is { } minutes)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"heard: {minutes} min"));
        }
        if (filter.Reception != Reception.Any)
        {
            parts.Add("reception: " + filter.Reception);
        }
        var with = new[]
        {
            (filter.MessageCapable, "messages"),
            (filter.WithWeather, "weather"),
            (filter.WithTrack, "track"),
            (filter.WithTelemetry, "telemetry"),
            (filter.HasProblems, "problems"),
        }
            .Where(w => w.Item1)
            .Select(w => w.Item2)
            .ToList();
        if (with.Count > 0)
        {
            parts.Add("with: " + string.Join(", ", with));
        }
        Add("devices", filter.DeviceClasses);
        Add("mic-e", filter.MicEStatuses);
        Add("tags", filter.Tags);
        if (view is not null)
        {
            parts.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"view: {Math.Round(view.Latitude, 4)} {Math.Round(view.Longitude, 4)} z{Math.Round(view.Zoom, 1)}"
                )
            );
        }
        return string.Join("; ", parts);

        void Add(string key, IReadOnlyList<string>? values)
        {
            if (values is { Count: > 0 })
            {
                parts.Add(key + ": " + string.Join(", ", values));
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="text"/>, finding the lists it names among <paramref name="lists"/> (case doesn't matter);
    /// a part that can't be read, and a list the profile doesn't have, are reported and left out.
    /// </summary>
    public static SavedFilterParse Parse(string? text, IReadOnlyList<StationList> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var filter = StationFilter.None;
        SavedMapView? view = null;
        var errors = new List<string>();
        var missing = new List<string>();
        foreach (var raw in (text ?? string.Empty).Split(';'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            var key = colon < 0 ? part.ToLowerInvariant() : part[..colon].Trim().ToLowerInvariant();
            var value = colon < 0 ? string.Empty : part[(colon + 1)..].Trim();
            var values = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            switch (key)
            {
                case "lists":
                    var ids = new List<string>();
                    foreach (var name in values)
                    {
                        if (
                            lists.FirstOrDefault(l =>
                                string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)
                            ) is
                            { } list
                        )
                        {
                            ids.Add(list.Id);
                        }
                        else
                        {
                            missing.Add(name);
                        }
                    }
                    filter = filter with { Lists = ids.Count > 0 ? ids : null };
                    break;
                case "kinds" when Enum.TryParse<StationKinds>(value, true, out var kinds):
                    filter = filter with { Kinds = kinds };
                    break;
                case "categories" when values.Count > 0:
                    filter = filter with { Categories = values };
                    break;
                case "heard" when Minutes(value) is { } minutes:
                    filter = filter with { HeardWithinMinutes = minutes };
                    break;
                case "reception" when Enum.TryParse<Reception>(value, true, out var reception):
                    filter = filter with { Reception = reception };
                    break;
                case "with" when values.All(v => v is "messages" or "weather" or "track" or "telemetry" or "problems"):
                    filter = filter with
                    {
                        MessageCapable = values.Contains("messages"),
                        WithWeather = values.Contains("weather"),
                        WithTrack = values.Contains("track"),
                        WithTelemetry = values.Contains("telemetry"),
                        HasProblems = values.Contains("problems"),
                    };
                    break;
                case "devices" when values.Count > 0:
                    filter = filter with { DeviceClasses = values };
                    break;
                case "mic-e" when values.Count > 0:
                    filter = filter with { MicEStatuses = values };
                    break;
                case "tags" when values.Count > 0:
                    filter = filter with { Tags = values };
                    break;
                case "view" when View(value) is { } read:
                    view = read;
                    break;
                default:
                    errors.Add(part);
                    break;
            }
        }
        return new SavedFilterParse(filter, view, errors, missing);
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> show the same stations: the same conditions.</summary>
    public static bool Same(StationFilter? a, StationFilter? b) =>
        Key(a ?? StationFilter.None) == Key(b ?? StationFilter.None);

    // The conditions as text, lists by id and every set in order, so equal filters give equal keys.
    private static string Key(StationFilter f) =>
        string.Join(
            "|",
            f.Kinds,
            Set(f.Categories),
            f.HeardWithinMinutes,
            f.Reception,
            f.MessageCapable,
            f.WithWeather,
            f.WithTrack,
            Set(f.DeviceClasses),
            Set(f.MicEStatuses),
            Set(f.Tags),
            f.WithTelemetry,
            f.HasProblems,
            Set(f.Lists)
        );

    private static string Set(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? string.Join(",", values.Order(StringComparer.Ordinal)) : string.Empty;

    private static int? Minutes(string value)
    {
        var number = value.EndsWith("min", StringComparison.OrdinalIgnoreCase) ? value[..^3].Trim() : value;
        return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? minutes
            : null;
    }

    private static SavedMapView? View(string value)
    {
        var bits = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return
            bits.Length == 3
            && double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            && bits[2].StartsWith('z')
            && double.TryParse(bits[2][1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom)
            && lat is >= -90 and <= 90
            && lon is >= -180 and <= 180
            && zoom is >= 0 and <= 22
            ? new SavedMapView(lat, lon, zoom)
            : null;
    }
}
