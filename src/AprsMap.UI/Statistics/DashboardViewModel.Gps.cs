// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsNet.Packets;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.Statistics;

/// <summary>The dashboard's GPS tiles (docs/features/F3.14-gps-dashboard.md, behaviors 7–9).</summary>
public sealed partial class DashboardViewModel
{
    /// <summary>Whether the profile had a GPS in the range; without one a tile says how to add one.</summary>
    [ObservableProperty]
    public partial bool HasGps { get; private set; }

    /// <summary>The GPS in use, and whether another stood in for part of the range (behavior 5).</summary>
    [ObservableProperty]
    public partial string? GpsSourceText { get; private set; }

    /// <summary>The share of the range with a fix, and the losses (behavior 7.1).</summary>
    public Tile GpsFix { get; } = new();

    /// <summary>3D, 2D, no fix and silent as shares and times.</summary>
    public ObservableCollection<CountRow> GpsFixRows { get; } = [];

    /// <summary>The losses, the longest outage and the last one.</summary>
    [ObservableProperty]
    public partial string? GpsOutageText { get; private set; }

    /// <summary>Satellites used of those in view (behavior 7.2).</summary>
    public Tile GpsSatellites { get; } = new();

    /// <summary>Each system seen in the range.</summary>
    public ObservableCollection<CountRow> GpsSystemRows { get; } = [];

    /// <summary>The dilutions of precision (behavior 7.3).</summary>
    public Tile GpsDilution { get; } = new();

    /// <summary>A row a dilution: now, average and worst.</summary>
    public ObservableCollection<ValueRow> GpsDilutionRows { get; } = [];

    /// <summary>The GPS's error estimates (behavior 7.4).</summary>
    public Tile GpsAccuracy { get; } = new();

    /// <summary>A row an axis: now, average and worst.</summary>
    public ObservableCollection<ValueRow> GpsErrorRows { get; } = [];

    /// <summary>The average signal of the satellites used and the wander while still.</summary>
    [ObservableProperty]
    public partial string? GpsSignalText { get; private set; }

    /// <summary>The wander while still (behavior 4), or that there aren't enough still fixes yet.</summary>
    [ObservableProperty]
    public partial string? GpsWanderText { get; private set; }

    private void ShowGps(Dashboard dashboard, string range, Func<DateTimeOffset, string> clock)
    {
        HasGps = dashboard.Gps is not null;
        if (dashboard.Gps is not { } gps)
        {
            return;
        }
        var units = _units();
        var source = _sources.NameOf(gps.SourceId);
        GpsSourceText = gps.OtherInUse
            ? _text.Format("Dashboard.Gps.SourceOther", new Dictionary<string, object?> { ["source"] = source })
            : source;

        var total = gps.Fix3D + gps.Fix2D + gps.NoFix + gps.Silent;
        var withFix = Percent(gps.Fix3D + gps.Fix2D, total);
        GpsFix.Set(
            _text,
            _text["Dashboard.Gps.Fix"],
            _text.Format("Dashboard.Percent", new Dictionary<string, object?> { ["percent"] = withFix }),
            _text.Format("Dashboard.Gps.Losses", new Dictionary<string, object?> { ["count"] = gps.Losses }),
            gps.FixShare,
            range
        );
        Replace(
            GpsFixRows,
            new[]
            {
                ("Dashboard.Gps.Fix3D", gps.Fix3D),
                ("Dashboard.Gps.Fix2D", gps.Fix2D),
                ("Dashboard.Gps.NoFix", gps.NoFix),
                ("Dashboard.Gps.Silent", gps.Silent),
            }
                .Where(r => r.Item2 > TimeSpan.Zero || r.Item1 == "Dashboard.Gps.Fix3D")
                .Select(r => new CountRow(
                    _text[r.Item1],
                    _text.Format(
                        "Dashboard.Gps.Share",
                        new Dictionary<string, object?>
                        {
                            ["percent"] = Percent(r.Item2, total),
                            ["time"] = Duration(r.Item2),
                        }
                    ),
                    total > TimeSpan.Zero ? r.Item2 / total : 0,
                    null
                ))
        );
        var outage = new List<string>();
        if (gps.LongestOutage is { } longest)
        {
            outage.Add(
                _text.Format("Dashboard.Gps.Longest", new Dictionary<string, object?> { ["time"] = Duration(longest) })
            );
        }
        if (gps.LastOutage is { } last)
        {
            outage.Add(
                _text.Format(
                    last.Ongoing ? "Dashboard.Gps.LostSince" : "Dashboard.Gps.LastLost",
                    new Dictionary<string, object?> { ["time"] = clock(last.Start), ["length"] = Duration(last.Length) }
                )
            );
        }
        GpsOutageText = outage.Count > 0 ? string.Join(" · ", outage) : null;

        var used = gps.SatellitesUsed;
        GpsSatellites.Set(
            _text,
            _text["Dashboard.Gps.Satellites"],
            used.Now is { } usedNow
                ? gps.SatellitesInView.Now is { } inViewNow
                    ? _text.Format(
                        "Dashboard.Gps.UsedOf",
                        new Dictionary<string, object?> { ["used"] = Whole(usedNow), ["inView"] = Whole(inViewNow) }
                    )
                    : Whole(usedNow)
                : NoValue,
            used.Average is null
                ? null
                : _text.Format(
                    "Dashboard.Gps.UsedRange",
                    new Dictionary<string, object?>
                    {
                        ["average"] = Decimal(used.Average),
                        ["lowest"] = Decimal(used.Lowest),
                        ["highest"] = Decimal(used.Highest),
                    }
                ),
            gps.SatellitesSeries,
            range,
            "0.#"
        );
        var mostUsed = gps.Constellations.Count > 0 ? gps.Constellations.Max(c => c.AverageUsed ?? 0) : 0;
        Replace(
            GpsSystemRows,
            gps.Constellations.Select(c => new CountRow(
                _text[$"Gps.System.{c.System}"],
                _text.Format(
                    "Dashboard.Gps.SystemRow",
                    new Dictionary<string, object?>
                    {
                        ["used"] = c.UsedNow,
                        ["inView"] = c.InViewNow,
                        ["average"] = Decimal(c.AverageUsed),
                    }
                ),
                mostUsed > 0 ? (c.AverageUsed ?? 0) / mostUsed : 0,
                null
            ))
        );

        var hdop = gps.Dilutions.FirstOrDefault(d => d.Key == "hdop")?.Stat;
        GpsDilution.Set(
            _text,
            _text["Dashboard.Gps.Dilution"],
            hdop?.Now is { } h
                ? _text.Format("Dashboard.Gps.Hdop", new Dictionary<string, object?> { ["value"] = Decimal(h, "0.0") })
                : NoValue,
            _text["Gps.DopHint"],
            gps.HdopSeries,
            range,
            "0.0"
        );
        Replace(
            GpsDilutionRows,
            gps.Dilutions.Select(d => new ValueRow(
                _text[$"Dashboard.Gps.Dop.{d.Key}"],
                StatRow(d.Stat, v => Decimal(v, "0.0"))
            ))
        );

        var feet = units.Altitude == AltitudeUnit.Feet;
        string Length(double? metres) =>
            metres is not { } m
                ? NoValue
                : _text.Format(
                    feet ? "Dashboard.Gps.Feet" : "Dashboard.Gps.Metres",
                    new Dictionary<string, object?>
                    {
                        ["value"] = (feet ? AprsUnits.Convert(m, LengthUnit.Meters, LengthUnit.Feet) : m).ToString(
                            "0.0",
                            _text.Culture
                        ),
                    }
                );
        var horizontal = gps.Errors.FirstOrDefault(e => e.Key == "horizontal")?.Stat;
        GpsAccuracy.Set(
            _text,
            _text["Dashboard.Gps.Accuracy"],
            horizontal?.Now is { } across ? Length(across) : NoValue,
            _text["Dashboard.Gps.ErrorNote"],
            [
                .. gps.ErrorSeries.Select(m =>
                    m is { } v && feet ? AprsUnits.Convert(v, LengthUnit.Meters, LengthUnit.Feet) : m
                ),
            ],
            range,
            "0.0"
        );
        Replace(
            GpsErrorRows,
            gps.Errors.Select(e => new ValueRow(_text[$"Dashboard.Gps.Error.{e.Key}"], StatRow(e.Stat, Length)))
        );
        GpsSignalText = gps.Signal.Average is { } signal
            ? _text.Format("Dashboard.Gps.Signal", new Dictionary<string, object?> { ["value"] = Decimal(signal, "0") })
            : null;
        GpsWanderText = gps.WanderMeters is { } wander
            ? _text.Format(
                "Dashboard.Gps.Wander",
                new Dictionary<string, object?>
                {
                    ["distance"] = Length(wander).TrimStart('±'),
                    ["count"] = gps.WanderFixes,
                }
            )
            : _text["Dashboard.Gps.WanderNone"];
    }

    private string NoValue => _text["Dashboard.Gps.NoValue"];

    /// <summary>"now · average · worst" for a row; the worst is the highest, since lower is better for each.</summary>
    private string StatRow(DashboardStat stat, Func<double?, string> write) =>
        _text.Format(
            "Dashboard.Gps.StatRow",
            new Dictionary<string, object?>
            {
                ["now"] = write(stat.Now),
                ["average"] = write(stat.Average),
                ["worst"] = write(stat.Highest),
            }
        );

    private string Whole(double value) => value.ToString("#,0", _text.Culture);

    private string Decimal(double? value, string format = "0.#") =>
        value is { } v ? v.ToString(format, _text.Culture) : NoValue;

    private static int Percent(TimeSpan part, TimeSpan total) =>
        total > TimeSpan.Zero ? (int)Math.Round(100 * (part / total)) : 0;

    /// <summary>"45 s", "12 min", "23 h 18 min".</summary>
    private string Duration(TimeSpan time) =>
        time < TimeSpan.FromMinutes(1)
            ? _text.Format(
                "Dashboard.Gps.Seconds",
                new Dictionary<string, object?> { ["value"] = (int)Math.Round(time.TotalSeconds) }
            )
        : time < TimeSpan.FromHours(1)
            ? _text.Format(
                "Dashboard.Gps.Minutes",
                new Dictionary<string, object?> { ["value"] = (int)time.TotalMinutes }
            )
        : _text.Format(
            "Dashboard.Gps.HoursMinutes",
            new Dictionary<string, object?> { ["hours"] = (int)time.TotalHours, ["minutes"] = time.Minutes }
        );
}

/// <summary>A row whose values are too long to share a line with its name: the name, and the values under it.</summary>
public sealed record ValueRow(string Name, string Value);
