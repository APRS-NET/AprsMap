// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>
/// Converts settings stored before sources existed (docs/features/F1.25-sources.md, service surface): each profile had
/// its own APRS-IS server and filter. They become one APRS-IS source per server, or one per server and filter when the
/// profiles sharing a server disagree on the filter, and each profile ticks its own.
/// </summary>
internal static class SettingsMigration
{
    /// <summary>Whether <paramref name="root"/> is in the shape from before sources.</summary>
    public static bool IsBeforeSources(JsonObject root) =>
        root["sources"] is null
        && root["profiles"] is JsonArray profiles
        && profiles.OfType<JsonObject>().Any(p => p["server"] is not null);

    /// <summary>Rewrites <paramref name="root"/> in place into the shape with sources.</summary>
    public static void ToSources(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var profiles = root["profiles"] as JsonArray ?? [];
        var sources = new JsonArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byServer = profiles
            .OfType<JsonObject>()
            .Where(p => p["server"] is not null)
            .GroupBy(p => ((string?)p["server"] ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);
        foreach (var server in byServer)
        {
            // One source when the profiles agree on the filter; otherwise one per filter, so nothing changes.
            foreach (var filter in server.GroupBy(p => Filter(p), StringComparer.Ordinal))
            {
                var source = SourceSettings.AprsIsFor(server.Key, filter.Key);
                var name = source.Name;
                for (var n = 2; !names.Add(name); n++)
                {
                    name = $"{source.Name} ({n})";
                }
                var entry = new JsonObject
                {
                    ["id"] = source.Id,
                    ["name"] = name,
                    ["kind"] = nameof(SourceKind.AprsIs),
                    ["server"] = server.Key,
                };
                if (filter.Key is not null)
                {
                    entry["filter"] = filter.Key;
                }
                sources.Add(entry);
                foreach (var profile in filter)
                {
                    profile["sourceIds"] = new JsonArray(source.Id);
                }
            }
        }
        foreach (var profile in profiles.OfType<JsonObject>())
        {
            profile["sourceIds"] ??= new JsonArray();
            profile.Remove("server");
            profile.Remove("filter");
        }
        root["sources"] = sources;
    }

    private static string? Filter(JsonObject profile) =>
        (string?)profile["filter"] is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
