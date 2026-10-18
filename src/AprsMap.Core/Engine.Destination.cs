// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>A destination's distance, bearing and line (docs/features/F1.18-destination.md).</summary>
public sealed partial class Engine
{
    /// <summary>How many segments the great-circle line has: smooth at any zoom, cheap to draw.</summary>
    internal const int DestinationSegments = 64;

    /// <inheritdoc />
    public Task<DestinationInfo> GetDestinationAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default
    )
    {
        StationProfile? profile;
        lock (_gate)
        {
            profile = _settings?.GetActiveProfile();
        }
        if (profile is not { Latitude: { } lat, Longitude: { } lon })
        {
            return Task.FromResult(DestinationInfo.Unknown); // behavior 5: no location to measure from
        }
        var from = new AprsPosition(lat, lon);
        var to = new AprsPosition(latitude, longitude);
        var bearing = (int)Math.Round(Geodesy.InitialBearing(from, to)) % 360;
        var line = Geodesy
            .PointsAlong(from, to, DestinationSegments)
            .Select(p => new GeoPoint(p.Latitude, p.Longitude));
        return Task.FromResult(new DestinationInfo(Geodesy.DistanceKm(from, to), bearing, [.. line]));
    }
}
