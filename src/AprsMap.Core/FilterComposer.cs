// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Filters;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// Builds the APRS-IS filter from its parts (docs/features/F1.2-automatic-filter.md, behavior 1), with APRS.NET's typed
/// filter terms: my range, the map views that show somewhere else, followed stations, and the APRS-IS source's own
/// text (F1.25).
/// </summary>
internal static class FilterComposer
{
    /// <summary>The widest map view that adds a range part; wider shows most of a continent (decision 2).</summary>
    public const double MaxViewKm = 1_000;

    /// <summary>The most viewers' map parts on a gateway: APRS-IS takes 9 range parts, and my range is one.</summary>
    public const int MaxViewerViews = 8;

    /// <summary>The range used for a followed station when my range is 0.</summary>
    public const int DefaultFriendKm = 150;

    /// <summary>
    /// The filter for <paramref name="profile"/> with <paramref name="views"/>, the map views reported (most recently
    /// moved first). A view's map part is left out when my range already covers it, or when it's wider than
    /// <see cref="MaxViewKm"/>. With no part at all and a range, the filter is my range around my last position on
    /// APRS-IS (<c>m/</c>, behavior 2).
    /// </summary>
    public static string Compose(StationProfile profile, string? ownFilter, IReadOnlyList<MapViewReport> views) =>
        ComposeParts(profile, ownFilter, views, _ => null).Text;

    /// <summary>
    /// The filter text and its range parts as circles (behavior 6); a followed station's circle is centred on its last
    /// position, from <paramref name="positionOf"/>, when known.
    /// </summary>
    public static (string Text, IReadOnlyList<FilterArea> Areas) ComposeParts(
        StationProfile profile,
        string? ownFilter,
        IReadOnlyList<MapViewReport> views,
        Func<string, (double Latitude, double Longitude)?> positionOf,
        IReadOnlyList<StationListEntry>? listed = null
    )
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(positionOf);
        var range = Math.Max(0, profile.RangeKm);
        AprsPosition? home = profile is { Latitude: { } lat, Longitude: { } lon } ? new AprsPosition(lat, lon) : null;
        var terms = new List<AprsFilterTerm>();
        var areas = new List<FilterArea>();
        if (range > 0 && home is { } me)
        {
            terms.Add(AprsFilterTerm.Range(me, range));
            areas.Add(new FilterArea(me.Latitude, me.Longitude, range));
        }

        var mapParts = 0;
        foreach (var view in views)
        {
            if (view is not { Latitude: { } vlat, Longitude: { } vlon } || mapParts >= MaxViewerViews)
            {
                continue;
            }
            var radius = Math.Round(view.RadiusKm);
            if (radius > MaxViewKm)
            {
                continue;
            }
            var centre = new AprsPosition(vlat, vlon);
            if (range > 0 && home is { } at && Geodesy.DistanceKm(at, centre) + radius <= range)
            {
                continue; // inside my range already
            }
            radius = Math.Max(1, radius);
            terms.Add(AprsFilterTerm.Range(centre, radius));
            areas.Add(new FilterArea(vlat, vlon, radius));
            mapParts++;
        }

        var friend = range > 0 ? range : DefaultFriendKm;
        // The followed and watched stations (F1.7 behavior 8, F1.16 behavior 6), wherever they go.
        foreach (var name in views.SelectMany(v => v.Tracked).Distinct(StringComparer.Ordinal))
        {
            terms.Add(AprsFilterTerm.FriendRange(name, friend));
            if (positionOf(name) is var (flat, flon))
            {
                areas.Add(new FilterArea(flat, flon, friend));
            }
        }

        // The station lists shown (F1.33 behavior 9): callsigns as a buddy list, every SSID of a bare callsign, and
        // object and item names, wherever they are; at most StationList.MaxFilterEntries of them.
        var entries = (listed ?? []).Take(StationList.MaxFilterEntries).ToList();
        string[] buddies =
        [
            .. entries
                .Where(e => e.IsCallsign)
                .Select(e => e.IsEverySsid ? e.Pattern + "*" : e.Pattern)
                .Distinct(StringComparer.Ordinal),
        ];
        string[] objects =
        [
            .. entries.Where(e => !e.IsCallsign).Select(e => e.Pattern).Distinct(StringComparer.Ordinal),
        ];
        if (buddies.Length > 0)
        {
            terms.Add(AprsFilterTerm.Buddies(buddies));
        }
        if (objects.Length > 0)
        {
            terms.Add(AprsFilterTerm.Objects(objects));
        }

        var own = string.IsNullOrWhiteSpace(ownFilter) ? null : ownFilter.Trim();
        if (terms.Count == 0 && own is null && range > 0)
        {
            terms.Add(AprsFilterTerm.MyRange(range)); // around my last position on APRS-IS
        }
        var text = string.Join(' ', terms.Select(t => t.ToString()));
        return (
            own is null ? text
            : string.IsNullOrEmpty(text) ? own
            : $"{text} {own}",
            areas
        );
    }

    /// <summary>
    /// Whether <paramref name="next"/> differs enough from <paramref name="last"/> to ask for a new filter (behavior 4):
    /// the centre moved more than a quarter of the radius, the radius changed by more than a quarter, or the followed or
    /// watched stations changed.
    /// </summary>
    public static bool Differs(MapViewReport? last, MapViewReport next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (last is null || !last.Tracked.SequenceEqual(next.Tracked, StringComparer.Ordinal))
        {
            return true;
        }
        if (last.Latitude is not { } llat || last.Longitude is not { } llon)
        {
            return next.Latitude is not null;
        }
        if (next.Latitude is not { } nlat || next.Longitude is not { } nlon)
        {
            return true;
        }
        var radius = Math.Max(1, last.RadiusKm);
        var moved = Geodesy.DistanceKm(new AprsPosition(llat, llon), new AprsPosition(nlat, nlon));
        return moved > radius / 4 || Math.Abs(next.RadiusKm - last.RadiusKm) > radius / 4;
    }
}
