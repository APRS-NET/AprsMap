// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsNet.Packets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Stations;

/// <summary>
/// The Details panel (docs/features/F1.6-station-details.md): everything the engine knows about the selected station,
/// each line only when known, written in the viewer's language and units, and refreshed while it's open.
/// </summary>
public sealed partial class StationDetailsViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly TimeProvider _time;
    private readonly Localizer _text;
    private readonly Func<UnitSettings> _units;
    private readonly ViewModels.SourceNames? _sources;
    private readonly Func<string, bool> _canClearTrack;
    private StationDetail? _detail;
    private int _loads;

    internal StationDetailsViewModel(
        IEngineService engine,
        TimeProvider time,
        Localizer text,
        Func<UnitSettings> units,
        ViewModels.SourceNames? sources = null,
        Func<string, bool>? canClearTrack = null
    )
    {
        _sources = sources;
        _canClearTrack = canClearTrack ?? (_ => false);
        _engine = engine;
        _time = time;
        _text = text;
        _units = units;
        _text.LanguageChanged += (_, _) => Write();
    }

    /// <summary>Raised by Centre: the map centres on the station (behavior 4).</summary>
    public event EventHandler<(double Latitude, double Longitude)>? CentreRequested;

    /// <summary>Raised by Close or Escape: the selection is cleared (behavior 1).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The station shown; null when nothing is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStation), nameof(IsEmpty))]
    public partial string? Name { get; private set; }

    /// <summary>Whether a station is shown.</summary>
    public bool HasStation => Name is not null;

    /// <summary>Whether the panel shows its "select a station" note.</summary>
    public bool IsEmpty => Name is null;

    /// <summary>The symbol as its two characters, for the icon.</summary>
    [ObservableProperty]
    public partial string? Symbol { get; private set; }

    /// <summary>Station, object or item, with the owner for objects and items (behavior 2.1).</summary>
    [ObservableProperty]
    public partial string? KindText { get; private set; }

    /// <summary>"Heard 4 minutes ago · 12 packets" (behavior 2.2).</summary>
    [ObservableProperty]
    public partial string? HeardText { get; private set; }

    /// <summary>When it was last heard, in full, for the tooltip.</summary>
    [ObservableProperty]
    public partial string? HeardTime { get; private set; }

    /// <summary>Latitude and longitude, in decimal degrees (behavior 2.3).</summary>
    [ObservableProperty]
    public partial string? PositionText { get; private set; }

    /// <summary>The 6-character locator, with the ambiguity when there is one.</summary>
    [ObservableProperty]
    public partial string? LocatorText { get; private set; }

    /// <summary>Distance and bearing from my location (F1.13).</summary>
    [ObservableProperty]
    public partial string? RangeText { get; private set; }

    /// <summary>Speed, course and altitude in the viewer's units (behavior 2.4).</summary>
    [ObservableProperty]
    public partial string? MotionText { get; private set; }

    /// <summary>The latest comment, control characters shown as symbols (behavior 2.5).</summary>
    [ObservableProperty]
    public partial string? Comment { get; private set; }

    /// <summary>The latest status text, control characters shown as symbols.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>How the last packet arrived (behavior 2.6).</summary>
    [ObservableProperty]
    public partial string? ArrivalText { get; private set; }

    /// <summary>The last packet's path as sent, shown on request.</summary>
    [ObservableProperty]
    public partial string? Path { get; private set; }

    /// <summary>The equipment that sent the last packet (behavior 2.7).</summary>
    [ObservableProperty]
    public partial string? DeviceText { get; private set; }

    /// <summary>"First heard via Direwolf · also rotate.aprs2.net", with several sources in use (F1.25 behavior 9).</summary>
    [ObservableProperty]
    public partial string? HeardViaText { get; private set; }

    /// <summary>The last packet as received (behavior 2.8).</summary>
    [ObservableProperty]
    public partial string? LastPacket { get; private set; }

    /// <summary>"No longer heard": the station left while the panel was open (behavior 3).</summary>
    [ObservableProperty]
    public partial bool IsGone { get; private set; }

    /// <summary>Whether the map keeps centring on the station as it moves (behavior 4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowingText))]
    public partial bool IsFollowing { get; set; }

    /// <summary>The map's chip while following: "Following N0CALL-9" (F1.7 behavior 8).</summary>
    public string? FollowingText =>
        IsFollowing ? _text.Format("Map.Following", new Dictionary<string, object?> { ["name"] = Name }) : null;

    /// <summary>Whether Clear track is offered: the station has a track, and this viewer may clear it (F1.5 behavior 10).</summary>
    [ObservableProperty]
    public partial bool CanClearTrack { get; private set; }

    /// <summary>Empties the station's track except its latest point, for every viewer (F1.5 behavior 10).</summary>
    [RelayCommand]
    private async Task ClearTrackAsync()
    {
        if (Name is not { } name)
        {
            return;
        }
        try
        {
            await _engine.ClearTrackAsync(name);
            CanClearTrack = false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            CanClearTrack = false; // not this viewer's to clear after all; the track stays
        }
    }

    /// <summary>Whether Centre, Follow and Copy position have a position to work with.</summary>
    public bool HasPosition => _detail is { Latitude: not null, Longitude: not null };

    /// <summary>Shows <paramref name="name"/>, or nothing with null.</summary>
    internal async Task ShowAsync(string? name)
    {
        if (name != Name)
        {
            IsFollowing = false;
            IsGone = false;
            _detail = null;
        }
        Name = name;
        CanClearTrack = name is not null && _canClearTrack(name);
        if (name is null)
        {
            Write();
            return;
        }
        var load = ++_loads;
        StationDetail? detail;
        try
        {
            detail = await _engine.GetStationAsync(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return; // the gateway is away; the next change tries again
        }
        if (load != _loads || name != Name)
        {
            return; // a later selection or refresh won
        }
        if (detail is null)
        {
            IsGone = _detail is not null;
            Write();
            return;
        }
        _detail = detail;
        IsGone = false;
        Write();
        if (IsFollowing && HasPosition)
        {
            CentreRequested?.Invoke(this, (detail.Latitude!.Value, detail.Longitude!.Value));
        }
    }

    /// <summary>A station stream batch: the shown station changed or left (behavior 3).</summary>
    internal void Changed(StationBatch batch)
    {
        if (Name is not { } name)
        {
            return;
        }
        CanClearTrack = _canClearTrack(name);
        if (batch.Removed.Contains(name))
        {
            IsGone = true;
            IsFollowing = false;
            Write();
        }
        else if (batch.Changed.Any(s => s.Name == name))
        {
            _ = ShowAsync(name);
        }
    }

    /// <summary>Writes every line again, as the language, units or clock moved on.</summary>
    internal void Refresh() => Write();

    /// <summary>Centres the map on the station.</summary>
    [RelayCommand]
    private void Centre()
    {
        if (_detail is { Latitude: { } lat, Longitude: { } lon })
        {
            CentreRequested?.Invoke(this, (lat, lon));
        }
    }

    /// <summary>Clears the selection.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The position as copied: decimal degrees with five places, the locator after it.</summary>
    internal string? PositionCopy() =>
        _detail is { Latitude: { } lat, Longitude: { } lon }
            ? string.Create(CultureInfo.InvariantCulture, $"{lat:0.00000}, {lon:0.00000}")
            : null;

    partial void OnIsFollowingChanged(bool value)
    {
        if (value)
        {
            Centre();
        }
    }

    private void Write()
    {
        OnPropertyChanged(nameof(HasPosition));
        var d = _detail;
        if (d is null || Name is null)
        {
            Symbol = KindText = HeardText = HeardTime = PositionText = LocatorText = RangeText = MotionText = null;
            Comment = Status = ArrivalText = Path = DeviceText = LastPacket = HeardViaText = null;
            return;
        }
        var culture = _text.Culture;
        var units = _units();
        Symbol = d.Symbol;
        KindText = d.Kind switch
        {
            StationKind.Object or StationKind.Item => _text.Format(
                d.PreviousOwner is null ? "Details.OwnedBy" : "Details.OwnedByBefore",
                new Dictionary<string, object?>
                {
                    ["kind"] = _text[$"Details.Kind.{d.Kind}"],
                    ["owner"] = d.Owner,
                    ["previous"] = d.PreviousOwner,
                }
            ),
            _ => _text["Details.Kind.Station"],
        };
        var minutes = (long)Math.Max(0, (_time.GetUtcNow() - d.LastHeard).TotalMinutes);
        HeardText = _text.Format(
            "Details.Heard",
            new Dictionary<string, object?> { ["minutes"] = minutes, ["packets"] = d.Packets }
        );
        HeardTime = TimeZoneInfo.ConvertTime(d.LastHeard, _time.LocalTimeZone).ToString("F", culture);
        PositionText = d is { Latitude: { } lat, Longitude: { } lon }
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
        LocatorText =
            d.Locator is null ? null
            : d.PositionAmbiguity > 0
                ? _text.Format(
                    "Details.LocatorAmbiguous",
                    new Dictionary<string, object?> { ["locator"] = d.Locator, ["digits"] = d.PositionAmbiguity }
                )
            : d.Locator;
        RangeText = Localization.RangeText.Format(d.DistanceKm, d.BearingDegrees, units.Distance, _text);
        MotionText = Motion(d, units, culture);
        Comment = Visible(d.Comment);
        Status = Visible(d.Status);
        ArrivalText = Arrival(d.Arrival);
        Path = string.IsNullOrEmpty(d.Path) ? null : d.Path;
        DeviceText = d.Device switch
        {
            null => null,
            { Vendor: { } vendor, Model: { } model } => $"{vendor} {model}",
            { Model: { } model } => model,
            { Vendor: { } vendor } => vendor,
            _ => null,
        };
        LastPacket = d.LastPacket;
        HeardViaText = _sources?.Describe(d.FirstHeardVia, d.SeenOn, _text);
    }

    private string? Motion(StationDetail d, UnitSettings units, CultureInfo culture)
    {
        var parts = new List<string>();
        if (d.SpeedKmh is { } kmh)
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
            var speed = AprsUnits.Convert(kmh, AprsNet.Packets.SpeedUnit.KilometersPerHour, to);
            parts.Add(_text.Format(key, new Dictionary<string, object?> { ["value"] = speed.ToString("0", culture) }));
        }
        if (d.CourseDegrees is { } course)
        {
            parts.Add(
                _text.Format(
                    "Details.Course",
                    new Dictionary<string, object?> { ["value"] = course.ToString("000", culture) }
                )
            );
        }
        if (d.AltitudeMetres is { } metres)
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

    private string? Arrival(StationArrival? arrival) =>
        arrival switch
        {
            null => null,
            { Kind: ArrivalKind.Direct, Gateway: { } gate } => _text.Format(
                "Details.Arrival.DirectVia",
                new Dictionary<string, object?> { ["gateway"] = gate }
            ),
            { Kind: ArrivalKind.Direct } => _text["Details.Arrival.Direct"],
            { Kind: ArrivalKind.Digipeated } => _text.Format(
                "Details.Arrival.Digipeated",
                new Dictionary<string, object?>
                {
                    ["count"] = arrival.Digipeaters.Count,
                    ["digipeaters"] = string.Join(", ", arrival.Digipeaters),
                    ["gateway"] = arrival.Gateway ?? "-",
                }
            ),
            { Kind: ArrivalKind.Internet } => _text.Format(
                "Details.Arrival.Internet",
                new Dictionary<string, object?> { ["gateway"] = arrival.Gateway ?? "-" }
            ),
            _ => null,
        };

    /// <summary>Text as sent, with control characters shown as their symbols, never acted on (behavior 2.5).</summary>
    internal static string? Visible(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var shown = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            shown.Append(
                c switch
                {
                    < ' ' => (char)(0x2400 + c),
                    '\u007f' => '␡',
                    _ => c,
                }
            );
        }
        return shown.ToString();
    }
}
