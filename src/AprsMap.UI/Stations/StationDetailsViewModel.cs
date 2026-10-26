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
        _text.LanguageChanged += (_, _) =>
        {
            Write();
            RouteText = Words(_route);
        };
    }

    /// <summary>Raised by Centre: the map centres on the station (behavior 4).</summary>
    public event EventHandler<(double Latitude, double Longitude)>? CentreRequested;

    /// <summary>A station in this station's heard lists was picked: select it (F3.1 behavior 8).</summary>
    public event EventHandler<string>? SelectRequested;

    /// <summary>Whether this is this station, the active profile's callsign (F3.1 behavior 8).</summary>
    [ObservableProperty]
    public partial bool IsMe { get; private set; }

    /// <summary>"Heard by this station in the last hour: 6 directly, 3 through digipeaters"; null when not this station.</summary>
    [ObservableProperty]
    public partial string? HeardHereText { get; private set; }

    /// <summary>
    /// How my last position got out (docs/features/F3.3-rf-paths.md, behavior 2), on this station's details; null
    /// otherwise.
    /// </summary>
    [ObservableProperty]
    public partial string? LastPositionText { get; private set; }

    /// <summary>The stations this station heard directly in the last hour, nearest first, each selectable.</summary>
    public System.Collections.ObjectModel.ObservableCollection<HeardRow> HeardDirectly { get; } = [];

    /// <summary>The stations this station heard through digipeaters in the last hour, each selectable.</summary>
    public System.Collections.ObjectModel.ObservableCollection<HeardRow> HeardThroughDigipeaters { get; } = [];

    /// <summary>Whether the heard-through-digipeaters list has anything.</summary>
    [ObservableProperty]
    public partial bool HasHeardThroughDigipeaters { get; private set; }

    private void WriteMe(ThisStation? me, UnitSettings units)
    {
        HeardDirectly.Clear();
        HeardThroughDigipeaters.Clear();
        IsMe = me is not null;
        HasHeardThroughDigipeaters = me is { HeardThroughDigipeaters.Count: > 0 };
        LastPositionText = me?.LastPosition is { } last
            ? Localization.HeardBackText.Describe(
                last,
                _text,
                at => TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("t", _text.Culture)
            )
            : null;
        if (me is null)
        {
            HeardHereText = null;
            return;
        }
        HeardHereText = _text.Format(
            "Details.Me.Heard",
            new Dictionary<string, object?>
            {
                ["direct"] = me.HeardDirectly.Count,
                ["digipeated"] = me.HeardThroughDigipeaters.Count,
            }
        );
        HeardRow Row(HeardStation h) =>
            new(
                h.Name,
                h.DistanceKm is { } km ? Localization.RangeText.Distance(km, units.Distance, _text) : null,
                new CommunityToolkit.Mvvm.Input.RelayCommand(() => SelectRequested?.Invoke(this, h.Name))
            );
        foreach (var heard in me.HeardDirectly)
        {
            HeardDirectly.Add(Row(heard));
        }
        foreach (var heard in me.HeardThroughDigipeaters)
        {
            HeardThroughDigipeaters.Add(Row(heard));
        }
    }

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

    /// <summary>
    /// The station lists it's in, with its notes there: "In Spotters: Sweep 2" (docs/features/F1.33-station-lists.md,
    /// behavior 2); null when it's in none.
    /// </summary>
    [ObservableProperty]
    public partial string? ListsText { get; private set; }

    /// <summary>The active profile's station lists; set by the main view model.</summary>
    internal Func<IReadOnlyList<StationList>>? StationLists { get; set; }

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

    /// <summary>
    /// "Probably 3.4 km on, along its course": where dead reckoning puts it now (docs/features/F1.17-dead-reckoning.md,
    /// behavior 4); null while dead reckoning is off or the station isn't estimated.
    /// </summary>
    [ObservableProperty]
    public partial string? EstimateText { get; private set; }

    /// <summary>Whether this viewer shows dead reckoning (Settings › Map), so the estimate is said here too.</summary>
    internal bool DeadReckoning { get; set; }

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

    /// <summary>The station's weather readings in words and the viewer's units (F1.10 behavior 3); empty without any.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DetailLine> Weather { get; } = [];

    /// <summary>The station's telemetry channels (F1.11 behavior 4): latest value and chart each; empty without any.</summary>
    public System.Collections.ObjectModel.ObservableCollection<TelemetryChannel> Telemetry { get; } = [];

    /// <summary>Whether the station has sent telemetry.</summary>
    [ObservableProperty]
    public partial bool HasTelemetry { get; private set; }

    /// <summary>"Report 123 · 2 minutes ago · 40 reports held".</summary>
    [ObservableProperty]
    public partial string? TelemetryText { get; private set; }

    /// <summary>The eight bits of the latest report, as 0s and 1s, for a station without definitions.</summary>
    [ObservableProperty]
    public partial string? TelemetryBits { get; private set; }

    /// <summary>Each named bit of the latest report, on or off (F1.11 behavior 2); empty without definitions.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DetailLine> TelemetryBitLines { get; } = [];

    /// <summary>The project name from the station's <c>BITS.</c>; null without one.</summary>
    [ObservableProperty]
    public partial string? TelemetryProject { get; private set; }

    /// <summary>Whether the values are raw: the station hasn't sent definitions (F1.11 behavior 3).</summary>
    [ObservableProperty]
    public partial bool IsTelemetryRaw { get; private set; }

    private TelemetryView? _telemetry;

    private async Task LoadTelemetryAsync(string name, int load)
    {
        TelemetryView? telemetry;
        try
        {
            telemetry = await _engine.GetTelemetryAsync(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return;
        }
        if (load == _loads && Name == name)
        {
            _telemetry = telemetry;
            WriteTelemetry();
        }
    }

    /// <summary>
    /// The latest values and a chart per analog channel over the held reports (F1.11 behaviors 3, 4), raw until
    /// APRS.NET applies the station's definitions (G09).
    /// </summary>
    private void WriteTelemetry()
    {
        Telemetry.Clear();
        TelemetryBitLines.Clear();
        var samples = _telemetry?.Samples;
        var definitions = _telemetry?.Definitions;
        HasTelemetry = samples is { Count: > 0 };
        IsTelemetryRaw = definitions is null;
        TelemetryProject = definitions?.ProjectName;
        if (samples is not { Count: > 0 })
        {
            TelemetryText = TelemetryBits = null;
            return;
        }
        var latest = samples[^1];
        TelemetryText = _text.Format(
            "Details.Telemetry.Latest",
            new Dictionary<string, object?>
            {
                ["sequence"] = latest.Sequence ?? "none",
                ["minutes"] = (long)Math.Max(0, (_time.GetUtcNow() - latest.At).TotalMinutes),
                ["count"] = samples.Count,
            }
        );
        var culture = _text.Culture;
        for (var channel = 0; channel < latest.Analog.Count; channel++)
        {
            var values = samples.Select(s => channel < s.Analog.Count ? s.Analog[channel] : null).ToList();
            if (values.All(v => v is null))
            {
                continue;
            }
            var defined = definitions?.Analog.ElementAtOrDefault(channel);
            var label = defined?.Name ?? $"A{channel + 1}";
            var unit = defined?.Unit is { } u ? " " + u : string.Empty;
            Telemetry.Add(
                new TelemetryChannel(
                    label,
                    latest.Analog[channel] is { } value ? value.ToString("0.###", culture) + unit : "–",
                    values,
                    _text.Format(
                        "Details.Telemetry.Chart",
                        new Dictionary<string, object?>
                        {
                            ["channel"] = label,
                            ["values"] = string.Join(", ", values.Select(v => v?.ToString("0.###", culture) ?? "–")),
                        }
                    )
                )
            );
        }
        if (definitions is null)
        {
            TelemetryBits = latest.Digital.Count > 0 ? string.Concat(latest.Digital.Select(b => b ? '1' : '0')) : null;
            return;
        }
        TelemetryBits = null;
        for (var bit = 0; bit < latest.Digital.Count; bit++)
        {
            var defined = definitions.Digital.ElementAtOrDefault(bit);
            if (defined?.Name is null && defined?.Label is null)
            {
                continue; // a bit the station didn't define says nothing to read
            }
            TelemetryBitLines.Add(
                new DetailLine(
                    defined.Name ?? $"B{bit + 1}",
                    latest.Digital[bit]
                        ? defined.Label ?? _text["Details.Telemetry.On"]
                        : _text["Details.Telemetry.Off"]
                )
            );
        }
    }

    /// <summary>"Invalid packets: 3 in the last hour · latest: …" for each problem (F1.20 behavior 5); empty without any.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DetailLine> Problems { get; } = [];

    /// <summary>Whether the station's packets had problems in the last hour.</summary>
    [ObservableProperty]
    public partial bool HasProblems { get; private set; }

    private void WriteProblems(IReadOnlyList<StationProblem>? problems)
    {
        Problems.Clear();
        HasProblems = problems is { Count: > 0 };
        foreach (var problem in problems ?? [])
        {
            Problems.Add(
                new DetailLine(
                    _text[$"Details.Problem.{problem.Kind}"],
                    _text.Format(
                        "Details.Problem.Count",
                        new Dictionary<string, object?>
                        {
                            ["count"] = problem.Count,
                            ["latest"] = problem.Latest is { } latest ? _text.Format(latest) : string.Empty,
                        }
                    )
                )
            );
        }
    }

    /// <summary>Whether the station has sent weather.</summary>
    [ObservableProperty]
    public partial bool HasWeather { get; private set; }

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

    /// <summary>
    /// The route of the packet shown on the map in words (F1.21 behavior 2): "Heard directly by N0GATE-1, 23 km away",
    /// or each hop with its distance; "Not heard on RF" for a packet from the internet. Null when the path says nothing.
    /// </summary>
    [ObservableProperty]
    public partial string? RouteText { get; private set; }

    private PacketRoute? _route;

    /// <summary>Whether the map draws this station's routes out, so the legend says which line is which (F3.3).</summary>
    [ObservableProperty]
    public partial bool HasRoutesOut { get; private set; }

    /// <summary>Notes whether this station's routes out are drawn.</summary>
    internal void ShowRoutesOut(IReadOnlyList<PacketRoute> routes) => HasRoutesOut = routes.Count > 0;

    /// <summary>Shows <paramref name="route"/> in words.</summary>
    internal void ShowRoute(PacketRoute? route)
    {
        _route = route;
        RouteText = Words(route);
    }

    private string? Words(PacketRoute? route)
    {
        if (route is null)
        {
            return null;
        }
        var unit = _units().Distance;
        // A route this station's receiver heard ends at "this station", not its callsign (F3.1 behavior 8).
        string NameOf(RouteHop hop) => route.HeardHere && hop.IsGateway ? _text["Details.Route.ThisStation"] : hop.Name;
        string Hop(RouteHop hop) =>
            hop.DistanceKm is { } km
                ? _text.Format(
                    "Details.Route.Hop",
                    new Dictionary<string, object?>
                    {
                        ["name"] = NameOf(hop),
                        ["distance"] = Localization.RangeText.Distance(km, unit, _text),
                    }
                )
                : _text.Format("Details.Route.HopUnknown", new Dictionary<string, object?> { ["name"] = NameOf(hop) });
        return route.Kind switch
        {
            ArrivalKind.Internet => _text["Details.Route.Internet"],
            ArrivalKind.Direct when route.HeardHere => route.Hops is [{ DistanceKm: { } here }]
                ? _text.Format(
                    "Details.Route.DirectHere",
                    new Dictionary<string, object?>
                    {
                        ["distance"] = Localization.RangeText.Distance(here, unit, _text),
                    }
                )
                : _text["Details.Arrival.HeardHere"],
            ArrivalKind.Direct when route.Hops is [{ } gateway] => gateway.DistanceKm is { } km
                ? _text.Format(
                    "Details.Route.Direct",
                    new Dictionary<string, object?>
                    {
                        ["gateway"] = gateway.Name,
                        ["distance"] = Localization.RangeText.Distance(km, unit, _text),
                    }
                )
                : _text.Format(
                    "Details.Route.DirectUnknown",
                    new Dictionary<string, object?> { ["gateway"] = gateway.Name }
                ),
            ArrivalKind.Digipeated when route.Hops.Count > 0 => string.Join(
                " → ",
                [route.Name, .. route.Hops.Select(Hop)]
            ),
            _ => null,
        };
    }

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
            _telemetry = null;
            WriteTelemetry();
        }
        Name = name;
        CanClearTrack = name is not null && _canClearTrack(name);
        if (name is null)
        {
            Write();
            return;
        }
        var load = ++_loads;
        _ = LoadTelemetryAsync(name, load);
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

    /// <summary>Opens a conversation with the station in the Messages panel (docs/features/F2.4-messaging.md, behavior 3).</summary>
    [RelayCommand]
    private void SendMessage()
    {
        if (Name is { } name)
        {
            MessageRequested?.Invoke(this, name);
        }
    }

    /// <summary>Asks for a conversation with the station named.</summary>
    public event EventHandler<string>? MessageRequested;

    /// <summary>Sets the station as my destination (docs/features/F1.18-destination.md, behavior 1).</summary>
    [RelayCommand]
    private void SetDestination()
    {
        if (Name is { } name && _detail is { Latitude: { } lat, Longitude: { } lon })
        {
            DestinationRequested?.Invoke(this, (name, lat, lon));
        }
    }

    /// <summary>Asks for the station named, at its position, to become the destination.</summary>
    public event EventHandler<(string Name, double Latitude, double Longitude)>? DestinationRequested;

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
            ListsText = null;
            EstimateText = null;
            Comment = Status = ArrivalText = Path = DeviceText = LastPacket = HeardViaText = null;
            WriteWeather(null, units: UnitSettings.Metric, lastHeard: default);
            WriteProblems(null);
            WriteMe(null, UnitSettings.Metric);
            return;
        }
        var culture = _text.Culture;
        var units = _units();
        Symbol = d.Symbol;
        var inLists = (StationLists?.Invoke() ?? [])
            .Select(l => (l.Name, Entry: l.Entries.FirstOrDefault(e => e.Matches(Name))))
            .Where(x => x.Entry is not null)
            .Select(x =>
                x.Entry!.Note is { } note
                    ? _text.Format(
                        "Details.InListWithNote",
                        new Dictionary<string, object?> { ["list"] = x.Name, ["note"] = note }
                    )
                    : _text.Format("Details.InList", new Dictionary<string, object?> { ["list"] = x.Name })
            )
            .ToList();
        ListsText = inLists.Count > 0 ? string.Join(" · ", inLists) : null;
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
            _ when d.Me is not null => _text["Details.Kind.Me"],
            _ => _text["Details.Kind.Station"],
        };
        var heardItself = d.Packets > 0 || d.LastHeard != DateTimeOffset.MinValue;
        var minutes = (long)Math.Max(0, (_time.GetUtcNow() - d.LastHeard).TotalMinutes);
        HeardText = heardItself
            ? _text.Format(
                "Details.Heard",
                new Dictionary<string, object?> { ["minutes"] = minutes, ["packets"] = d.Packets }
            )
            : null; // this station, not heard on its own yet: only its location and what it heard
        HeardTime = heardItself
            ? TimeZoneInfo.ConvertTime(d.LastHeard, _time.LocalTimeZone).ToString("F", culture)
            : null;
        WriteMe(d.Me, units);
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
        var age = _time.GetUtcNow() - d.LastHeard;
        EstimateText =
            DeadReckoning
            && d is { CourseDegrees: not null, SpeedKmh: >= Map.MapView.ReckonFromKmh and var kmh }
            && d.Weather is null
            && age > TimeSpan.Zero
            && age <= Map.MapView.ReckonFor
                ? _text.Format(
                    "Details.Estimate",
                    new Dictionary<string, object?>
                    {
                        ["distance"] = Localization.RangeText.Distance(kmh * age.TotalHours, units.Distance, _text),
                    }
                )
                : null;
        WriteWeather(d.Weather, units, d.LastHeard);
        WriteProblems(d.Problems);
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

    /// <summary>
    /// One line per reading the station has sent (F1.10 behavior 3), each followed by its age when it's older than the
    /// station's last packet: a station may send wind and rain in different packets.
    /// </summary>
    private void WriteWeather(StationWeather? w, UnitSettings units, DateTimeOffset lastHeard)
    {
        Weather.Clear();
        HasWeather = w is not null;
        if (w is null)
        {
            return;
        }
        if (w.WindDirection is { } from)
        {
            var wind = Localization.WeatherText.Compass(from.Value, _text);
            if (w.WindSpeed is { } speed)
            {
                wind += " " + Localization.WeatherText.Speed(speed.Value, units.Wind, _text);
            }
            if (w.WindGust is { } gust)
            {
                wind +=
                    " · "
                    + _text.Format(
                        "Details.Weather.Gust",
                        new Dictionary<string, object?>
                        {
                            ["speed"] = Localization.WeatherText.Speed(gust.Value, units.Wind, _text),
                        }
                    );
            }
            Add("Details.Weather.Wind", wind, from);
        }
        else if (w.WindSpeed is { } calmish)
        {
            Add("Details.Weather.Wind", Localization.WeatherText.Speed(calmish.Value, units.Wind, _text), calmish);
        }
        if (w.Temperature is { } t)
        {
            Add(
                "Details.Weather.Temperature",
                Localization.WeatherText.Temperature(t.Value, units.Temperature, _text),
                t
            );
        }
        if (w.Humidity is { } h)
        {
            Add(
                "Details.Weather.Humidity",
                _text.Format("Details.Weather.Percent", new Dictionary<string, object?> { ["value"] = h.Value }),
                h
            );
        }
        if (w.Pressure is { } p)
        {
            Add("Details.Weather.Pressure", Localization.WeatherText.Pressure(p.Value, units.Pressure, _text), p);
        }
        if (w.RainLastHour is { } r1)
        {
            Add("Details.Weather.RainHour", Localization.WeatherText.Rain(r1.Value, units.Rain, _text), r1);
        }
        if (w.RainLast24Hours is { } r24)
        {
            Add("Details.Weather.Rain24", Localization.WeatherText.Rain(r24.Value, units.Rain, _text), r24);
        }
        if (w.RainSinceMidnight is { } rm)
        {
            Add("Details.Weather.RainMidnight", Localization.WeatherText.Rain(rm.Value, units.Rain, _text), rm);
        }
        if (w.Snow is { } s)
        {
            Add("Details.Weather.Snow", Localization.WeatherText.Rain(s.Value, units.Rain, _text), s);
        }
        if (w.Luminosity is { } l)
        {
            Add(
                "Details.Weather.Luminosity",
                _text.Format(
                    "Details.Weather.WattsPerSquareMetre",
                    new Dictionary<string, object?> { ["value"] = l.Value }
                ),
                l
            );
        }

        void Add(string label, string value, WeatherReading reading)
        {
            var minutes = (long)(lastHeard - reading.At).TotalMinutes;
            if (minutes >= 1)
            {
                value +=
                    " · "
                    + _text.Format("Details.Weather.Age", new Dictionary<string, object?> { ["minutes"] = minutes });
            }
            Weather.Add(new DetailLine(_text[label], value));
        }
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
            // Heard by this station's own receiver, a TNC (F3.1 behavior 8): no IGate to name.
            { HeardHere: true, Kind: ArrivalKind.Direct } => _text["Details.Arrival.HeardHere"],
            { HeardHere: true, Kind: ArrivalKind.Digipeated } => _text.Format(
                "Details.Arrival.DigipeatedHere",
                new Dictionary<string, object?>
                {
                    ["count"] = arrival.Digipeaters.Count,
                    ["digipeaters"] = string.Join(", ", arrival.Digipeaters),
                }
            ),
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

/// <summary>One labelled line in the details, such as a weather reading.</summary>
/// <param name="Label">"Temperature".</param>
/// <param name="Value">"14 °C".</param>
public sealed record DetailLine(string Label, string Value);

/// <summary>One telemetry channel in the details: its latest value and its values over the held reports.</summary>
/// <param name="Name">"A1".</param>
/// <param name="Latest">The latest value, as written.</param>
/// <param name="Values">Its values, oldest first, for the chart.</param>
/// <param name="ChartName">The chart's accessible name, with every value, for screen readers.</param>
public sealed record TelemetryChannel(string Name, string Latest, IReadOnlyList<double?> Values, string ChartName);

/// <summary>A station this station heard: its name, its distance, and selecting it.</summary>
/// <param name="Name">Its callsign.</param>
/// <param name="Distance">How far away, in the viewer's units; null when its position isn't known.</param>
/// <param name="Open">Selects it.</param>
public sealed record HeardRow(string Name, string? Distance, CommunityToolkit.Mvvm.Input.IRelayCommand Open);
