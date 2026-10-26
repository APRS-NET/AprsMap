// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>Station lists (docs/features/F1.33-station-lists.md): showing only them, and receiving them.</summary>
public sealed partial class Engine
{
    // The active profile's lists read once a settings change, by the ids a filter names. Under _listGate.
    private readonly Lock _listGate = new();
    private AppSettings? _listsFor;
    private readonly Dictionary<string, Func<string, bool>?> _listTests = new(StringComparer.Ordinal);

    /// <summary>
    /// A test of whether a name is in the lists <paramref name="ids"/> of the active profile (behavior 6); null when
    /// none of them exists, so the condition is ignored rather than hiding everything.
    /// </summary>
    private Func<string, bool>? ResolveLists(IReadOnlyList<string> ids)
    {
        var settings = _settings;
        var key = string.Join('\n', ids.Order(StringComparer.Ordinal));
        lock (_listGate)
        {
            if (!ReferenceEquals(settings, _listsFor))
            {
                _listsFor = settings;
                _listTests.Clear();
            }
            if (_listTests.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var entries = Lists(settings?.GetActiveProfile(), ids);
            Func<string, bool>? test =
                entries.Count == 0 && !ListsExist(settings?.GetActiveProfile(), ids)
                    ? null
                    : name => entries.Any(e => e.Matches(name));
            _listTests[key] = test;
            return test;
        }
    }

    /// <summary>The entries of the lists the viewers show now (behavior 9), for the APRS-IS filter.</summary>
    private List<StationListEntry> ListsInUseEntries(StationProfile profile) => Lists(profile, _stations.ListsInUse());

    private static List<StationListEntry> Lists(StationProfile? profile, IEnumerable<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return [.. (profile?.StationListSet ?? []).Where(l => wanted.Contains(l.Id)).SelectMany(l => l.Entries)];
    }

    private static bool ListsExist(StationProfile? profile, IReadOnlyList<string> ids) =>
        (profile?.StationListSet ?? []).Any(l => ids.Contains(l.Id, StringComparer.Ordinal));
}
