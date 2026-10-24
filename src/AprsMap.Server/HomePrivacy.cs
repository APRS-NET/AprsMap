// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Options;

namespace AprsMap.Server;

/// <summary>How much of the station's location a viewer learns (docs/features/F1.26-public-console.md, behavior 11).</summary>
public enum HomePrivacy
{
    /// <summary>The exact location, as the station knows it.</summary>
    Exact,

    /// <summary>The centre of its 4-character Maidenhead square, about 110 × 220 km; everything measured from there.</summary>
    Grid,

    /// <summary>No location: no house, no distances or bearings from it, no filter circles.</summary>
    Hidden,
}

/// <summary>
/// Gives each viewer's connection its <see cref="HomeShield"/> (behavior 11): none for a signed-in operator or under
/// <see cref="HomePrivacy.Exact"/>. Unset, the setting is <see cref="HomePrivacy.Grid"/> with an operator port, a
/// public/private split, and <see cref="HomePrivacy.Exact"/> without one (decision 4). Done at the gateway, so the
/// engine and the desktop app keep the exact location.
/// </summary>
internal sealed class HomeShields(IEngineService engine, IOptionsMonitor<GatewayOptions> options)
{
    /// <summary>The setting in force.</summary>
    public HomePrivacy Effective =>
        options.CurrentValue.HomePrivacy
        ?? (options.CurrentValue.OperatorPort is null ? HomePrivacy.Exact : HomePrivacy.Grid);

    /// <summary>The shield for a call: null for an operator, or when nothing is hidden.</summary>
    public async Task<HomeShield?> ForAsync(bool isOperator, CancellationToken cancellationToken)
    {
        if (isOperator || Effective == HomePrivacy.Exact)
        {
            return null;
        }
        var settings = await engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var profile = settings.GetActiveProfile();
        AprsPosition? home = profile is { Latitude: { } lat, Longitude: { } lon } ? new AprsPosition(lat, lon) : null;
        return new HomeShield(Effective, home, profile?.Callsign);
    }
}

/// <summary>What a viewer is sent, with the station's location replaced or removed (behavior 11).</summary>
internal sealed class HomeShield(HomePrivacy mode, AprsPosition? home, string? homeName)
{
    /// <summary>A home is nearer than this to a point to count as it (a filter circle's centre, a route's end).</summary>
    private const double SameKm = 0.05;

    /// <summary>Where a viewer is told the station is: the centre of its 4-character square, or nowhere.</summary>
    public AprsPosition? Shown { get; } =
        mode == HomePrivacy.Grid && home is { } at ? MaidenheadLocator.FromPosition(at, 4).Center : null;

    public HomePrivacy Mode => mode;

    private bool IsHome(double latitude, double longitude) =>
        home is { } at && Geodesy.DistanceKm(at, new AprsPosition(latitude, longitude)) < SameKm;

    private bool IsHomeName(string? name) =>
        homeName is not null && string.Equals(name, homeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Distance and bearing from the shown point, or nothing under <see cref="HomePrivacy.Hidden"/>.</summary>
    public (double? Km, int? Bearing) Measure(double? latitude, double? longitude)
    {
        if (Shown is not { } from || latitude is not { } lat || longitude is not { } lon)
        {
            return (null, null);
        }
        var to = new AprsPosition(lat, lon);
        return (Geodesy.DistanceKm(from, to), (int)Math.Round(Geodesy.InitialBearing(from, to)) % 360);
    }

    public StationState Apply(StationState station)
    {
        if (station.DistanceKm is null && station.BearingDegrees is null)
        {
            return station;
        }
        var (km, bearing) = Measure(station.Latitude, station.Longitude);
        return station with { DistanceKm = km, BearingDegrees = bearing };
    }

    public StationBatch Apply(StationBatch batch) => batch with { Changed = [.. batch.Changed.Select(Apply)] };

    public StationDetail Apply(StationDetail detail)
    {
        var (km, bearing) =
            detail.DistanceKm is null && detail.BearingDegrees is null
                ? (null, null)
                : Measure(detail.Latitude, detail.Longitude);
        return detail with
        {
            DistanceKm = km,
            BearingDegrees = bearing,
            // The stations this station heard, with distances from it: without them a viewer can't measure back.
            Me = detail.Me is { } me
                ? me with
                {
                    HeardDirectly = [.. me.HeardDirectly.Select(h => h with { DistanceKm = null })],
                    HeardThroughDigipeaters = [.. me.HeardThroughDigipeaters.Select(h => h with { DistanceKm = null })],
                }
                : null,
        };
    }

    /// <summary>A station list row: its distance from the shown point, from its position looked up.</summary>
    public StationRow Apply(StationRow row, (double Latitude, double Longitude)? position)
    {
        if (row.DistanceKm is null && row.BearingDegrees is null)
        {
            return row;
        }
        var (km, bearing) = Measure(position?.Latitude, position?.Longitude);
        return row with { DistanceKm = km, BearingDegrees = bearing };
    }

    /// <summary>A route ending at this station ends at the shown point, or has no last position (F3.1 behavior 8).</summary>
    public PacketRoute Apply(PacketRoute route)
    {
        if (!route.HeardHere || route.Hops.Count == 0 || !route.Hops[^1].IsGateway)
        {
            return route;
        }
        var last = route.Hops[^1];
        var before = route.Hops.Count > 1 ? route.Hops[^2] : null;
        (double? Lat, double? Lon) from = before is { Latitude: { } bl, Longitude: { } bo }
            ? (bl, bo)
            : (route.Latitude, route.Longitude);
        RouteHop end =
            Shown is { } shown && from is (double fl, double fo)
                ? last with
                {
                    Latitude = shown.Latitude,
                    Longitude = shown.Longitude,
                    DistanceKm = Geodesy.DistanceKm(new AprsPosition(fl, fo), shown),
                }
                : last with
                {
                    Latitude = null,
                    Longitude = null,
                    DistanceKm = null,
                };
        return route with { Hops = [.. route.Hops.Take(route.Hops.Count - 1), end] };
    }

    /// <summary>This station's routes out start at the shown point, or aren't sent (F3.3 behavior 3).</summary>
    public IReadOnlyList<PacketRoute> RoutesOut(IReadOnlyList<PacketRoute> routes)
    {
        if (Shown is not { } shown)
        {
            return [];
        }
        return
        [
            .. routes.Select(r =>
                r with
                {
                    Latitude = shown.Latitude,
                    Longitude = shown.Longitude,
                    Hops =
                    [
                        .. r.Hops.Select(
                            (h, i) =>
                                i == 0 && h is { Latitude: { } lat, Longitude: { } lon }
                                    ? h with
                                    {
                                        DistanceKm = Geodesy.DistanceKm(shown, new AprsPosition(lat, lon)),
                                    }
                                    : h
                        ),
                    ],
                }
            ),
        ];
    }

    /// <summary>Links to or from this station start at the shown point, or aren't sent.</summary>
    public IReadOnlyList<NetworkLink> Apply(IReadOnlyList<NetworkLink> links)
    {
        var shown = Shown;
        var kept = new List<NetworkLink>(links.Count);
        foreach (var link in links)
        {
            var from = IsHomeName(link.From) || IsHome(link.FromLatitude, link.FromLongitude);
            var to = IsHomeName(link.To) || IsHome(link.ToLatitude, link.ToLongitude);
            if (!from && !to)
            {
                kept.Add(link);
            }
            else if (shown is { } at)
            {
                kept.Add(
                    link with
                    {
                        FromLatitude = from ? at.Latitude : link.FromLatitude,
                        FromLongitude = from ? at.Longitude : link.FromLongitude,
                        ToLatitude = to ? at.Latitude : link.ToLatitude,
                        ToLongitude = to ? at.Longitude : link.ToLongitude,
                    }
                );
            }
        }
        return kept;
    }

    /// <summary>A filter circle around this station moves to the shown point, or isn't sent.</summary>
    public IReadOnlyList<FilterArea>? Apply(IReadOnlyList<FilterArea>? areas)
    {
        if (areas is null)
        {
            return null;
        }
        var shown = Shown;
        return
        [
            .. areas
                .Where(a => shown is not null || !IsHome(a.Latitude, a.Longitude))
                .Select(a =>
                    shown is { } at && IsHome(a.Latitude, a.Longitude)
                        ? a with
                        {
                            Latitude = at.Latitude,
                            Longitude = at.Longitude,
                        }
                        : a
                ),
        ];
    }

    /// <summary>Each profile's location: its square's centre, or none (behavior 11.4).</summary>
    public AppSettings Apply(AppSettings settings) =>
        settings with
        {
            Profiles =
            [
                .. settings.Profiles.Select(p =>
                    p is { Latitude: { } lat, Longitude: { } lon } && mode == HomePrivacy.Grid
                        ? p with
                        {
                            Latitude = MaidenheadLocator.FromPosition(lat, lon, 4).Center.Latitude,
                            Longitude = MaidenheadLocator.FromPosition(lat, lon, 4).Center.Longitude,
                        }
                        : p with
                        {
                            Latitude = null,
                            Longitude = null,
                        }
                ),
            ],
        };

    /// <summary>A viewer's destination, measured from the shown point (F1.18), or unknown.</summary>
    public DestinationInfo Destination(double latitude, double longitude, int segments)
    {
        if (Shown is not { } from)
        {
            return DestinationInfo.Unknown;
        }
        var to = new AprsPosition(latitude, longitude);
        return new DestinationInfo(
            Geodesy.DistanceKm(from, to),
            (int)Math.Round(Geodesy.InitialBearing(from, to)) % 360,
            [.. Geodesy.PointsAlong(from, to, segments).Select(p => new GeoPoint(p.Latitude, p.Longitude))]
        );
    }

    /// <summary>The dashboard without the direct hops this station heard: their distances are measured to it.</summary>
    public Dashboard Apply(Dashboard dashboard) =>
        dashboard with
        {
            FarthestDirect = dashboard.FarthestDirect is { } hops ? [.. hops.Where(h => !IsHomeName(h.Gateway))] : null,
        };
}
