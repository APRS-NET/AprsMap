// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>A point on the earth, in degrees.</summary>
public sealed record GeoPoint(double Latitude, double Longitude);

/// <summary>
/// How far a destination is from the station, which way, and the great-circle line to draw to it
/// (docs/features/F1.18-destination.md, behaviors 2, 3, 5).
/// </summary>
/// <param name="DistanceKm">The great-circle distance; null without the station's location.</param>
/// <param name="Bearing">The initial bearing in whole degrees, 0 to 359; null without the station's location.</param>
/// <param name="Line">Points along the great circle from the station to the destination; empty without a location.</param>
public sealed record DestinationInfo(double? DistanceKm, int? Bearing, IReadOnlyList<GeoPoint> Line)
{
    /// <summary>No location to measure from.</summary>
    public static readonly DestinationInfo Unknown = new(null, null, []);
}
