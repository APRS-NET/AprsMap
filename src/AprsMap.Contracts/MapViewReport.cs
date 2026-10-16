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
public sealed record MapViewReport(double? Latitude, double? Longitude, double RadiusKm, string? Following = null);
