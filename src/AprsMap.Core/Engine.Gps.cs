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

    /// <summary>What one GPS source has said (F3.6, F3.12): its latest fix, the latest with a position, and its sky.</summary>
    private sealed class GpsTrack
    {
        public (GpsFix Fix, DateTimeOffset At)? Latest;

        // The latest with a position: a GPS builds each second's fix sentence by sentence, so the latest can be one
        // that hasn't said where yet.
        public (GpsFix Fix, DateTimeOffset At)? Positioned;

        public GpsFixMode Mode; // the last 2D or 3D it said, so the status doesn't flicker mid-second
        public GpsAccuracy? Accuracy;
        public IReadOnlyList<GpsSkySatellite>? Sky;
    }

    // Each GPS source's track, by source id: a GPS source, or a radio's GPS on its TNC port (F3.13). Under _gate.
    private readonly Dictionary<string, GpsTrack> _gpsTracks = new(StringComparer.Ordinal);
    private AprsPosition? _filterCentre; // where my range was centred in the filter last sent, under _gate

    private GpsTrack TrackLocked(string sourceId) =>
        _gpsTracks.TryGetValue(sourceId, out var track) ? track : _gpsTracks[sourceId] = new GpsTrack();

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
            quality.SpeedErrorKnots,
            quality.Tdop,
            quality.Gdop,
            quality.LatitudeErrorMeters,
            quality.LongitudeErrorMeters
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
            var track = TrackLocked(sourceId);
            track.Accuracy = accuracy;
            track.Sky = sky;
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

    /// <summary>
    /// A GPS source read a fix: kept on its track. When it's the GPS in use (F3.13 behavior 4), the moving schedule
    /// hears it and the filter is re-sent once the station has moved far enough.
    /// </summary>
    private void OnGpsFix(string sourceId, GpsFix fix)
    {
        var now = _time.GetUtcNow();
        AprsPosition? centre;
        bool inUse;
        lock (_gate)
        {
            var track = TrackLocked(sourceId);
            track.Latest = (fix, now);
            if (fix.Mode is GpsFixMode.Fix2D or GpsFixMode.Fix3D)
            {
                track.Mode = fix.Mode;
            }
            if (HasPosition(fix))
            {
                track.Positioned = (fix, now);
            }
            centre = _filterCentre;
            var settings = _settings ?? AppSettings.Empty;
            inUse = GpsInUseLocked(settings, settings.GetActiveProfile()).Source?.Id == sourceId;
        }
        if (!inUse)
        {
            return; // a GPS lower in priority, kept for when the one above has no fix
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
            _gpsTracks.Remove(sourceId);
        }
    }

    /// <summary>
    /// The GPS sources <paramref name="profile"/> may be placed by, in priority (docs/features/F3.13-tnc-gps.md,
    /// behavior 4): its GPS source, then radios whose GPS is On, then radios on Auto, each in the profile's order.
    /// </summary>
    private static IEnumerable<SourceSettings> GpsCandidates(AppSettings settings, StationProfile? profile)
    {
        if (profile is null)
        {
            return [];
        }
        var used = profile
            .SourceIds.Select(id => settings.SourceList.FirstOrDefault(s => s.Id == id))
            .OfType<SourceSettings>()
            .ToList();
        return used.Where(s => s.Kind == SourceKind.Gps)
            .Concat(used.Where(s => s.EffectiveTncGps == TncGps.On))
            .Concat(used.Where(s => s.EffectiveTncGps == TncGps.Auto));
    }

    private bool IsFresh(DateTimeOffset at) => _time.GetUtcNow() - at <= GpsFreshFor;

    /// <summary>
    /// The GPS in use (behavior 4): the first in priority with a fresh fix; without one, the first that's expected (a
    /// GPS source, or a radio set to On) or a radio on Auto that has said something, so the status shows it; null with
    /// none. Call under <see cref="_gate"/>.
    /// </summary>
    private (SourceSettings? Source, GpsTrack? Track) GpsInUseLocked(AppSettings settings, StationProfile? profile)
    {
        var candidates = GpsCandidates(settings, profile).ToList();
        foreach (var source in candidates)
        {
            if (_gpsTracks.TryGetValue(source.Id, out var track) && track.Positioned is { At: var at } && IsFresh(at))
            {
                return (source, track);
            }
        }
        foreach (var source in candidates)
        {
            var track = _gpsTracks.GetValueOrDefault(source.Id);
            if (source.Kind == SourceKind.Gps || source.EffectiveTncGps == TncGps.On || track?.Latest is not null)
            {
                return (source, track);
            }
        }
        return (null, null);
    }

    /// <summary>The fresh fix with a position from <paramref name="profile"/>'s GPS in use, or null. Call under <see cref="_gate"/>.</summary>
    private GpsFix? FreshFixLocked(StationProfile profile) =>
        GpsInUseLocked(_settings ?? AppSettings.Empty, profile) is { Track.Positioned: var (fix, at) } && IsFresh(at)
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

    /// <summary>The active profile's GPS in use and its fix, for the status (behaviors 4, 5). Call under <see cref="_gate"/>.</summary>
    private GpsStatus? GpsStatusLocked()
    {
        var settings = _settings ?? AppSettings.Empty;
        if (GpsInUseLocked(settings, settings.GetActiveProfile()) is not ({ } gps, var track))
        {
            return null;
        }
        // The latest fix that said where the GPS is, unless the GPS has said since that it has none.
        var latest = track?.Latest is { Fix.Mode: GpsFixMode.NoFix }
            ? track.Latest
            : track?.Positioned ?? track?.Latest;
        if (latest is not var (fix, at))
        {
            return new GpsStatus(gps.Id, GpsFixKind.NoFix, Accuracy: track?.Accuracy, Sky: track?.Sky);
        }
        var kind =
            !HasPosition(fix) ? GpsFixKind.NoFix
            : fix.Mode == GpsFixMode.Fix3D || (fix.Mode != GpsFixMode.Fix2D && track!.Mode == GpsFixMode.Fix3D)
                ? GpsFixKind.Fix3D
            : GpsFixKind.Fix2D;
        return new GpsStatus(
            gps.Id,
            kind,
            at,
            IsFresh(at),
            fix.Position?.Latitude,
            fix.Position?.Longitude,
            kind == GpsFixKind.Fix3D ? fix.AltitudeMeters : null,
            fix.SpeedKnots,
            fix.Course,
            fix.Satellites,
            fix.Hdop,
            track!.Accuracy,
            track.Sky
        );
    }

    private readonly GpsHistory _gpsHistory = new(); // F3.14

    /// <summary>Samples the GPS in use once a second for the dashboard and the metrics (F3.14 behaviors 1, 10).</summary>
    private async Task RunGpsHistoryAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(GpsHistory.SampleInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                SampleGps();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One sample of the GPS in use (F3.14 behavior 1); nothing with no GPS in the profile.</summary>
    internal void SampleGps()
    {
        var now = _time.GetUtcNow();
        GpsSample? sample;
        lock (_gate)
        {
            sample = GpsSampleLocked(now);
        }
        if (sample is null)
        {
            return;
        }
        var lost = _gpsHistory.Record(sample, now, GpsHistory.SampleInterval);
        EngineTelemetry.GpsFixTime.Add(
            GpsHistory.SampleInterval.TotalSeconds,
            new KeyValuePair<string, object?>(
                "fix",
                sample.State switch
                {
                    GpsSampleState.Fix3D => "3d",
                    GpsSampleState.Fix2D => "2d",
                    GpsSampleState.NoFix => "none",
                    _ => "silent",
                }
            )
        );
        if (lost)
        {
            EngineTelemetry.GpsFixLosses.Add(1);
        }
    }

    /// <summary>The GPS in use as one sample, or null without one. Call under <see cref="_gate"/>.</summary>
    private GpsSample? GpsSampleLocked(DateTimeOffset now)
    {
        var settings = _settings ?? AppSettings.Empty;
        if (GpsInUseLocked(settings, settings.GetActiveProfile()) is not ({ } source, var track))
        {
            return null;
        }
        if (track?.Latest is not { At: var at } || now - at > GpsHistory.SilentAfter)
        {
            return new GpsSample(source.Id, GpsSampleState.Silent);
        }
        var status = GpsStatusLocked();
        var state =
            status is not { HasPosition: true } ? GpsSampleState.NoFix
            : status.Fix == GpsFixKind.Fix3D ? GpsSampleState.Fix3D
            : GpsSampleState.Fix2D;
        return new GpsSample(
            source.Id,
            state,
            track.Accuracy,
            track.Sky,
            status?.Latitude,
            status?.Longitude,
            status?.SpeedKnots
        );
    }
}
