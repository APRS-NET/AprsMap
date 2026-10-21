// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsNet.Packets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Gps;

/// <summary>
/// The GPS panel (docs/features/F3.12-gps-status.md): the active profile's GPS, its fix, how good it is, the sky and
/// the satellites, from the status poll.
/// </summary>
public sealed partial class GpsPanelViewModel : ObservableObject
{
    private readonly Localizer _text;
    private readonly TimeProvider _time;
    private readonly Func<string, Task> _openConsole;
    private string? _sourceId;

    internal GpsPanelViewModel(Localizer text, TimeProvider time, Func<string, Task> openConsole)
    {
        _text = text;
        _time = time;
        _openConsole = openConsole;
        OpenConsoleCommand = new AsyncRelayCommand(
            () => _sourceId is { } id ? _openConsole(id) : Task.CompletedTask,
            () => _sourceId is not null
        );
    }

    /// <summary>Whether the profile has a GPS; without one the panel says how to add one (behavior 1).</summary>
    [ObservableProperty]
    public partial bool HasGps { get; private set; }

    /// <summary>"3D fix", "2D fix", "No fix", with "not fresh" when it's old (behavior 2).</summary>
    [ObservableProperty]
    public partial string? FixText { get; private set; }

    /// <summary>"49.0583° N, 72.0292° W".</summary>
    [ObservableProperty]
    public partial string? PositionText { get; private set; }

    /// <summary>Speed, course and altitude in the viewer's units.</summary>
    [ObservableProperty]
    public partial string? MotionText { get; private set; }

    /// <summary>"12 seconds ago".</summary>
    [ObservableProperty]
    public partial string? AgeText { get; private set; }

    /// <summary>"±3.0 m across, ±4.5 m up", where the GPS estimates its error (behavior 3).</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>"PDOP 2.5 · HDOP 0.9 · VDOP 2.1".</summary>
    [ObservableProperty]
    public partial string? DopText { get; private set; }

    /// <summary>"3 of 5 satellites used".</summary>
    [ObservableProperty]
    public partial string? SatellitesText { get; private set; }

    /// <summary>The satellites in view, for the sky view (behavior 4).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpsSkySatellite>? Sky { get; private set; }

    /// <summary>The satellites, strongest first: the sky view's accessible form (behavior 5).</summary>
    public ObservableCollection<GpsSatelliteRow> Satellites { get; } = [];

    /// <summary>Opens the Source console on the GPS (behavior 6).</summary>
    public IAsyncRelayCommand OpenConsoleCommand { get; }

    /// <summary>Shows the GPS's status, in <paramref name="units"/>.</summary>
    internal void Show(GpsStatus? gps, UnitSettings units)
    {
        HasGps = gps is not null;
        if (_sourceId != gps?.SourceId)
        {
            _sourceId = gps?.SourceId;
            OpenConsoleCommand.NotifyCanExecuteChanged();
        }
        var culture = _text.Culture;
        FixText = gps is null
            ? null
            : _text[
                (gps.Fix, gps.IsFresh) switch
                {
                    (GpsFixKind.NoFix, _) => "Gps.Fix.None",
                    (_, false) => "Gps.Fix.Stale",
                    (GpsFixKind.Fix3D, _) => "Gps.Fix.3D",
                    _ => "Gps.Fix.2D",
                }
            ];
        PositionText = gps is { Fix: not GpsFixKind.NoFix, Latitude: { } lat, Longitude: { } lon }
            ? _text.Format(
                "Details.Position",
                new Dictionary<string, object?>
                {
                    ["latitude"] = Math.Abs(lat).ToString("0.0000", culture),
                    ["ns"] = _text[lat >= 0 ? "Details.North" : "Details.South"],
                    ["longitude"] = Math.Abs(lon).ToString("0.0000", culture),
                    ["ew"] = _text[lon >= 0 ? "Details.East" : "Details.West"],
                }
            )
            : null;
        MotionText = gps is { Fix: not GpsFixKind.NoFix } ? Motion(gps, units, culture) : null;
        AgeText = gps?.At is { } at
            ? _text.Format(
                "Gps.Age",
                new Dictionary<string, object?>
                {
                    ["seconds"] = (int)Math.Max(0, (_time.GetUtcNow() - at).TotalSeconds),
                }
            )
            : null;
        var accuracy = gps?.Accuracy;
        ErrorText = accuracy is { HorizontalErrorMeters: { } across }
            ? _text.Format(
                accuracy.VerticalErrorMeters is null ? "Gps.Error.Across" : "Gps.Error.AcrossAndUp",
                new Dictionary<string, object?>
                {
                    ["across"] = across.ToString("0.0", culture),
                    ["up"] = accuracy.VerticalErrorMeters?.ToString("0.0", culture),
                }
            )
            : null;
        var dops = new List<string>();
        foreach (
            var (name, value) in new[]
            {
                ("PDOP", accuracy?.Pdop),
                ("HDOP", accuracy?.Hdop ?? gps?.Hdop),
                ("VDOP", accuracy?.Vdop),
            }
        )
        {
            if (value is { } v)
            {
                dops.Add(string.Create(culture, $"{name} {v:0.0}"));
            }
        }
        DopText = dops.Count == 0 ? null : string.Join(" · ", dops);
        var used = accuracy?.SatellitesUsed ?? gps?.Satellites;
        var inView = accuracy?.SatellitesInView ?? gps?.Sky?.Count;
        SatellitesText = (used, inView) switch
        {
            ({ } u, { } v) => _text.Format(
                "Gps.Satellites.UsedOf",
                new Dictionary<string, object?> { ["used"] = u, ["inView"] = v }
            ),
            ({ } u, null) => _text.Format("Gps.Satellites.Used", new Dictionary<string, object?> { ["used"] = u }),
            _ => null,
        };
        var sky = Strongest(gps?.Sky);
        Sky = sky;
        Satellites.Clear();
        foreach (var s in sky.OrderByDescending(s => s.SnrDbHz ?? -1).ThenBy(s => s.System).ThenBy(s => s.Id))
        {
            Satellites.Add(new GpsSatelliteRow(s, _text, culture));
        }
    }

    /// <summary>One mark for each satellite: a satellite seen on two signals, the stronger (decision 4).</summary>
    internal static IReadOnlyList<GpsSkySatellite> Strongest(IReadOnlyList<GpsSkySatellite>? sky) =>
        sky is null
            ? []
            : [.. sky.GroupBy(s => (s.System, s.Id)).Select(g => g.OrderByDescending(s => s.SnrDbHz ?? -1).First())];

    private string? Motion(GpsStatus gps, UnitSettings units, CultureInfo culture)
    {
        var parts = new List<string>();
        if (gps.SpeedKnots is { } knots)
        {
            var (to, key) = units.Wind switch
            {
                Contracts.SpeedUnit.MilesPerHour => (
                    AprsNet.Packets.SpeedUnit.MilesPerHour,
                    "Details.Speed.MilesPerHour"
                ),
                Contracts.SpeedUnit.Knots => (AprsNet.Packets.SpeedUnit.Knots, "Details.Speed.Knots"),
                Contracts.SpeedUnit.MetresPerSecond => (
                    AprsNet.Packets.SpeedUnit.MetersPerSecond,
                    "Details.Speed.MetresPerSecond"
                ),
                _ => (AprsNet.Packets.SpeedUnit.KilometersPerHour, "Details.Speed.KilometresPerHour"),
            };
            var speed = AprsUnits.Convert(knots, AprsNet.Packets.SpeedUnit.Knots, to);
            parts.Add(_text.Format(key, new Dictionary<string, object?> { ["value"] = speed.ToString("0", culture) }));
        }
        if (gps.Course is { } course)
        {
            parts.Add(
                _text.Format(
                    "Details.Course",
                    new Dictionary<string, object?> { ["value"] = course.ToString("000", culture) }
                )
            );
        }
        if (gps.AltitudeMeters is { } metres)
        {
            var feet = units.Altitude == AltitudeUnit.Feet;
            var value = feet ? AprsUnits.Convert(metres, LengthUnit.Meters, LengthUnit.Feet) : metres;
            parts.Add(
                _text.Format(
                    feet ? "Details.Altitude.Feet" : "Details.Altitude.Metres",
                    new Dictionary<string, object?> { ["value"] = value.ToString("#,0", culture) }
                )
            );
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

/// <summary>One satellite in the GPS panel's list (docs/features/F3.12-gps-status.md, behavior 5).</summary>
public sealed class GpsSatelliteRow
{
    internal GpsSatelliteRow(GpsSkySatellite satellite, Localizer text, CultureInfo culture)
    {
        Satellite = satellite;
        SystemText = text[$"Gps.System.{satellite.System}"];
        IdText = satellite.Id.ToString(culture);
        ElevationText = satellite.ElevationDegrees is { } e ? string.Create(culture, $"{e:0}°") : "–";
        AzimuthText = satellite.AzimuthDegrees is { } a ? string.Create(culture, $"{a:000}°") : "–";
        SignalText = satellite.SnrDbHz is { } snr ? string.Create(culture, $"{snr:0}") : "–";
        UsedText = text[satellite.Used ? "Gps.Used" : "Gps.NotUsed"];
        Description = text.Format(
            "Gps.Satellite.Description",
            new Dictionary<string, object?>
            {
                ["system"] = SystemText,
                ["id"] = IdText,
                ["elevation"] = ElevationText,
                ["azimuth"] = AzimuthText,
                ["signal"] = SignalText,
                ["used"] = UsedText,
            }
        );
    }

    /// <summary>The satellite.</summary>
    public GpsSkySatellite Satellite { get; }

    /// <summary>"GPS", "GLONASS"…</summary>
    public string SystemText { get; }

    /// <summary>Its number.</summary>
    public string IdText { get; }

    /// <summary>"45°", or a dash.</summary>
    public string ElevationText { get; }

    /// <summary>"120°", or a dash.</summary>
    public string AzimuthText { get; }

    /// <summary>Signal-to-noise in dB-Hz, or a dash when not tracked.</summary>
    public string SignalText { get; }

    /// <summary>"Used" or "Not used".</summary>
    public string UsedText { get; }

    /// <summary>The whole row in words, for screen readers.</summary>
    public string Description { get; }
}
