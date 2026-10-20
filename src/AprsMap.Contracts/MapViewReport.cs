// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// What a map shows, for the APRS-IS filter (docs/features/F1.2-automatic-filter.md): the view's centre and the radius
/// that covers it, and the station the map follows.
/// </summary>
/// <param name="Latitude">The view's centre, or null when the map hasn't been placed.</param>
/// <param name="Longitude">The view's centre, or null when the map hasn't been placed.</param>
/// <param name="RadiusKm">Half the view's diagonal, in km.</param>
/// <param name="Following">The station the map keeps centred (F1.7 behavior 8), or null.</param>
/// <param name="Watched">
/// The stations extra map views follow (docs/features/F1.16-extra-map-views.md, behavior 6), or null for none.
/// </param>
public sealed record MapViewReport(
    double? Latitude,
    double? Longitude,
    double RadiusKm,
    string? Following = null,
    IReadOnlyList<string>? Watched = null
)
{
    /// <summary>The stations this map keeps in the filter: the followed one and the watched ones, once each.</summary>
    public IEnumerable<string> Tracked =>
        new[] { Following }
            .Concat(Watched ?? [])
            .OfType<string>()
            .Select(n => n.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal);

    /// <summary>Equal when every field is, the watched stations in order.</summary>
    public bool Equals(MapViewReport? other) =>
        other is not null
        && Latitude == other.Latitude
        && Longitude == other.Longitude
        && RadiusKm.Equals(other.RadiusKm)
        && Following == other.Following
        && (Watched ?? []).SequenceEqual(other.Watched ?? []);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Latitude, Longitude, RadiusKm, Following, string.Join(',', Watched ?? []));
}
