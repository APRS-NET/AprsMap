// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.UI.ViewModels;

/// <summary>Stations heard in the last days, from the packet history (docs/features/F1.43-map-history-range.md).</summary>
public sealed partial class MainViewModel
{
    private HashSet<string> _alsoHeard = new(StringComparer.Ordinal);

    /// <summary>
    /// The stations to add to the map inside the box, for the days the View filter's choice says (behaviors 2, 3);
    /// none when it's off. The chip says how many were added.
    /// </summary>
    internal async Task<KeptStations> LoadAlsoHeardAsync(double south, double west, double north, double east)
    {
        if (ViewFilter.AlsoHeardDays is not { } days)
        {
            _alsoHeard = new(StringComparer.Ordinal);
            return KeptStations.None;
        }
        KeptStations kept;
        try
        {
            kept = await engine.GetKeptStationsAsync(
                new KeptStationsQuery(time.GetUtcNow().AddDays(-days), south, west, north, east)
            );
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return KeptStations.None; // the gateway is away: the next load tries again
        }
        if (ViewFilter.AlsoHeardDays != days)
        {
            return KeptStations.None; // the choice changed meanwhile: that load wins
        }
        _alsoHeard = new(kept.Stations.Select(s => s.Name), StringComparer.Ordinal);
        ViewFilter.ShowAlsoHeard(kept);
        return kept;
    }
}
