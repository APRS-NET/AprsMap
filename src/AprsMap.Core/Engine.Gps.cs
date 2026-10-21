// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Filters;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// The active profile's GPS (docs/features/F3.6-gps.md): its latest fix, and the station's position while that fix is
/// fresh, for beacons, Send position now, the filter's range and the status (behavior 7).
/// </summary>
public sealed partial class Engine
{
    /// <summary>How long after the last position sent others are shown seeing it (docs/features/F2.3-seen-as.md, decision 3).</summary>
    public static readonly TimeSpan SeenFor = TimeSpan.FromMinutes(30);

    private AprsNet.Beacons.DeadReckoning? _sent; // the last own position sent, under _gate

    /// <summary>
    /// Keeps the station's own position as it went out, with its course and speed, so where others see it can be
    /// worked out (F2.3 behavior 1). Objects, statuses and messages aren't positions of the station.
    /// </summary>
    private void RememberSent(AprsPacket packet)
    {
        if (packet.Payload is PositionReport or MicEReport && packet.Payload is PositionedPayload sent)
        {
            lock (_gate)
            {
                _sent = new AprsNet.Beacons.DeadReckoning(
                    sent.Position,
                    sent.Course ?? 0,
                    sent is { Course: not null, SpeedKnots: { } knots } ? knots : 0,
                    _time.GetUtcNow()
                );
            }
        }
    }

    /// <summary>
    /// Where others see the station now, against where its GPS says it is (F2.3 behaviors 1, 2, 5); null without a
    /// fresh fix or a position sent in the last <see cref="SeenFor"/>. Call under <see cref="_gate"/>.
    /// </summary>
    private SeenPosition? SeenAtLocked()
    {
        var now = _time.GetUtcNow();
        if (
            _sent is not { } sent
            || now - sent.At > SeenFor
            || (_settings ?? AppSettings.Empty).GetActiveProfile() is not { } profile
            || FreshFixLocked(profile)?.Position is not { } here
        )
        {
            return null;
        }
        var error = sent.ErrorAt(here, now);
        return new SeenPosition(error.Assumed.Latitude, error.Assumed.Longitude, error.DistanceKm, sent.At);
    }

    /// <summary>How long a fix stands for the station's position (F3.6 decision 3).</summary>
    public static readonly TimeSpan GpsFreshFor = TimeSpan.FromSeconds(60);

    private (string SourceId, GpsFix Fix, DateTimeOffset At)? _gps; // the latest, under _gate

    // The latest with a position: a GPS builds each second's fix sentence by sentence, so the latest can be one that
    // hasn't said where yet. Under _gate.
    private (string SourceId, GpsFix Fix, DateTimeOffset At)? _gpsPositioned;
    private GpsFixMode _gpsMode; // the last 2D or 3D the GPS said, so the status doesn't flicker mid-second, under _gate
    private AprsPosition? _filterCentre; // where my range was centred in the filter last sent, under _gate

    // The GPS's latest quality and satellites in view (docs/features/F3.12-gps-status.md), under _gate.
    private (string SourceId, GpsAccuracy Accuracy, IReadOnlyList<GpsSkySatellite> Sky)? _gpsSky;

    /// <summary>A GPS said how good its fix is and what it can see (F3.12 behaviors 3–5): kept for the status.</summary>
    private void OnGpsSky(string sourceId, GpsQuality quality, IReadOnlyList<GpsSatellite> satellites)
    {
        var accuracy = new GpsAccuracy(
            quality.SatellitesUsed,
            quality.SatellitesInView,
            quality.Pdop,
            quality.Hdop,
            quality.Vdop,
            quality.HorizontalErrorMeters,
            quality.AltitudeErrorMeters,
            quality.SpeedErrorKnots
        );
        GpsSkySatellite[] sky =
        [
            .. satellites.Select(s => new GpsSkySatellite(
                ConstellationOf(s.System),
                s.Id,
                s.SignalId,
                s.ElevationDegrees,
                s.AzimuthDegrees,
                s.SnrDbHz,
                s.Used
            )),
        ];
        lock (_gate)
        {
            _gpsSky = (sourceId, accuracy, sky);
        }
    }

    private static GnssConstellation ConstellationOf(GnssSystem system) =>
        system switch
        {
            GnssSystem.Gps => GnssConstellation.Gps,
            GnssSystem.Glonass => GnssConstellation.Glonass,
            GnssSystem.Galileo => GnssConstellation.Galileo,
            GnssSystem.BeiDou => GnssConstellation.BeiDou,
            GnssSystem.Qzss => GnssConstellation.Qzss,
            GnssSystem.NavIC => GnssConstellation.NavIC,
            GnssSystem.Sbas => GnssConstellation.Sbas,
            _ => GnssConstellation.Unknown,
        };

    /// <summary>A GPS source read a fix: kept, and the filter re-sent once the station has moved far enough.</summary>
    private void OnGpsFix(string sourceId, GpsFix fix)
    {
        var now = _time.GetUtcNow();
        AprsPosition? centre;
        lock (_gate)
        {
            _gps = (sourceId, fix, now);
            if (fix.Mode is GpsFixMode.Fix2D or GpsFixMode.Fix3D)
            {
                _gpsMode = fix.Mode;
            }
            if (HasPosition(fix))
            {
                _gpsPositioned = (sourceId, fix, now);
            }
            centre = _filterCentre;
        }
        _beaconer.Moved(fix); // the moving schedule's speed and turns (#335)
        // My range follows the GPS once it has moved more than a quarter of the range (behavior 7.2).
        if (
            fix.Position is { } here
            && Station.Of(_settings) is { Profile.RangeKm: > 0 and var range }
            && (centre is null || Geodesy.DistanceKm(centre.Value, here) > range / 4.0)
        )
        {
            RefreshFilter();
        }
    }

    /// <summary>Forgets a GPS's fix when its source stops.</summary>
    private void ForgetGps(string sourceId)
    {
        lock (_gate)
        {
            if (_gps?.SourceId == sourceId)
            {
                _gps = null;
                _gpsPositioned = null;
            }
        }
    }

    /// <summary>The GPS <paramref name="profile"/> uses (one at most, behavior 2), or null.</summary>
    private static SourceSettings? GpsOf(AppSettings settings, StationProfile? profile) =>
        profile is null ? null : settings.SourceList.FirstOrDefault(s => s.Kind == SourceKind.Gps && profile.Uses(s));

    /// <summary>The fresh fix with a position from <paramref name="profile"/>'s GPS, or null. Call under <see cref="_gate"/>.</summary>
    private GpsFix? FreshFixLocked(StationProfile profile) =>
        _gpsPositioned is var (sourceId, fix, at)
        && _time.GetUtcNow() - at <= GpsFreshFor
        && GpsOf(_settings ?? AppSettings.Empty, profile)?.Id == sourceId
            ? fix
            : null;

    /// <summary>The fresh fix from <paramref name="profile"/>'s GPS, for course, speed and altitude; null without one.</summary>
    internal GpsFix? FreshFix(StationProfile profile)
    {
        lock (_gate)
        {
            return FreshFixLocked(profile);
        }
    }

    /// <summary>
    /// <paramref name="profile"/> where its GPS says while the fix is fresh (behavior 7), otherwise as it is: its fixed
    /// location is never changed (decision 4).
    /// </summary>
    internal StationProfile WithGps(StationProfile profile) =>
        FreshFix(profile)?.Position is { } here
            ? profile with
            {
                Latitude = here.Latitude,
                Longitude = here.Longitude,
            }
            : profile;

    /// <summary>Whether <paramref name="fix"/> says where the GPS is: a position, and not a GPS saying it has no fix.</summary>
    private static bool HasPosition(GpsFix fix) => fix.Position is not null && fix.Mode != GpsFixMode.NoFix;

    /// <summary>The active profile's GPS and its fix, for the status (behaviors 4, 5). Call under <see cref="_gate"/>.</summary>
    private GpsStatus? GpsStatusLocked()
    {
        var settings = _settings ?? AppSettings.Empty;
        if (GpsOf(settings, settings.GetActiveProfile()) is not { } gps)
        {
            return null;
        }
        // The latest fix that said where the GPS is, unless the GPS has said since that it has none.
        var latest = _gps is { Fix.Mode: GpsFixMode.NoFix } ? _gps : _gpsPositioned ?? _gps;
        var (accuracy, sky) = _gpsSky is var (skyFrom, a, s) && skyFrom == gps.Id ? (a, s) : (null, null);
        if (latest is not var (sourceId, fix, at) || sourceId != gps.Id)
        {
            return new GpsStatus(gps.Id, GpsFixKind.NoFix, Accuracy: accuracy, Sky: sky);
        }
        var kind =
            !HasPosition(fix) ? GpsFixKind.NoFix
            : fix.Mode == GpsFixMode.Fix3D || (fix.Mode != GpsFixMode.Fix2D && _gpsMode == GpsFixMode.Fix3D)
                ? GpsFixKind.Fix3D
            : GpsFixKind.Fix2D;
        return new GpsStatus(
            gps.Id,
            kind,
            at,
            _time.GetUtcNow() - at <= GpsFreshFor,
            fix.Position?.Latitude,
            fix.Position?.Longitude,
            kind == GpsFixKind.Fix3D ? fix.AltitudeMeters : null,
            fix.SpeedKnots,
            fix.Course,
            fix.Satellites,
            fix.Hdop,
            accuracy,
            sky
        );
    }
}
