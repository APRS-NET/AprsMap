// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>
/// What the engine logs in with (docs/features/F1.25-sources.md, behavior 5): the active profile, which says who the
/// station is, and its APRS-IS source, which says where to connect.
/// </summary>
internal sealed record Station(StationProfile Profile, SourceSettings Source)
{
    public string Name => Profile.Name;

    public string Server => Source.Server ?? string.Empty;

    /// <summary>The source's filter text, added to the automatic filter (F1.2).</summary>
    public string? Filter => Source.Filter;

    /// <summary>The active profile with its APRS-IS source, or null when there is no profile or it uses none.</summary>
    public static Station? Of(AppSettings? settings) =>
        settings?.GetActiveProfile() is { } profile && settings.AprsIsSourceOf(profile) is { } source
            ? new Station(profile, source)
            : null;

    /// <summary>Whether going from <paramref name="before"/> to this needs a new APRS-IS login.</summary>
    public bool NeedsNewLogin(Station? before) =>
        before is null
        || Profile.Id != before.Profile.Id
        || Profile.Callsign != before.Profile.Callsign
        || Profile.Passcode != before.Profile.Passcode
        || Server != before.Server;

    /// <summary>
    /// Whether going from <paramref name="before"/> to this changes the filter, which the live connection takes without
    /// a new login: the source's filter text, or the profile's range or location (F1.2).
    /// </summary>
    public bool NeedsNewFilter(Station? before) =>
        before is null
        || Filter != before.Filter
        || Profile.RangeKm != before.Profile.RangeKm
        || Profile.Latitude != before.Profile.Latitude
        || Profile.Longitude != before.Profile.Longitude;
}
