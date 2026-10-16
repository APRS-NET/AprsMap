// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Statistics;

/// <summary>
/// The dashboard tab (docs/features/F1.22-statistics.md, behaviors 1–9): the engine's numbers over the chosen range,
/// asked for every 10 seconds while the tab is shown, as tiles of text and sparklines. Every number is written out,
/// never only drawn.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the numbers are asked for while the tab is shown (behavior 2).</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    /// <summary>A connected source silent for longer than this is quiet (behavior 4.7, decision 5).</summary>
    internal static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(2);

    private readonly IEngineService _engine;
    private readonly TimeProvider _time;
    private readonly Localizer _text;
    private readonly Func<EngineStatus?> _status;
    private readonly SourceNames _sources;
    private readonly SourceSwitch _aprsIs;
    private readonly Func<UnitSettings> _units;
    private readonly Action<StationFilter> _useFilter;
    private readonly Action<string> _select;
    private readonly Action _showProblems;
    private CancellationTokenSource? _running;
    private Dashboard? _last;

    internal DashboardViewModel(
        IEngineService engine,
        TimeProvider time,
        Localizer text,
        Func<EngineStatus?> status,
        SourceNames sources,
        SourceSwitch aprsIs,
        Action<string> select,
        Action showProblems,
        Func<UnitSettings>? units = null,
        Action<StationFilter>? useFilter = null
    )
    {
        _units = units ?? (() => UnitSettings.Metric);
        _useFilter = useFilter ?? (_ => { });
        Platforms = new StatTable(text, "Dashboard.Table.Platforms");
        Symbols = new StatTable(text, "Dashboard.Table.Symbols");
        StationKindsTable = new StatTable(text, "Dashboard.Table.Kinds");
        Reception = new StatTable(text, "Dashboard.Table.Reception");
        _engine = engine;
        _time = time;
        _text = text;
        _status = status;
        _sources = sources;
        _aprsIs = aprsIs;
        _select = select;
        _showProblems = showProblems;
        Ranges = [.. Enum.GetValues<DashboardRange>().Select(r => new RangeOption(r, this))];
        _text.LanguageChanged += (_, _) => Retranslate();
    }

    /// <summary>The range buttons: last hour, 6 hours, 24 hours.</summary>
    public IReadOnlyList<RangeOption> Ranges { get; }

    /// <summary>The range shown (behavior 3), per viewer; the last hour by default.</summary>
    [ObservableProperty]
    public partial DashboardRange Range { get; set; }

    /// <summary>"As of 14:32:10", or "Waiting for the engine" before the first answer.</summary>
    [ObservableProperty]
    public partial string AsOfText { get; private set; } = string.Empty;

    /// <summary>"Counting since 06:01" while the range reaches back before the counts began; null otherwise.</summary>
    [ObservableProperty]
    public partial string? HistoryText { get; private set; }

    /// <summary>Whether nothing has arrived in the range yet (behavior 9).</summary>
    [ObservableProperty]
    public partial bool IsWaiting { get; private set; } = true;

    /// <summary>Stations heard (behavior 4.1).</summary>
    public Tile Stations { get; } = new();

    /// <summary>New stations (behavior 4.2).</summary>
    public Tile NewStations { get; } = new();

    /// <summary>The newest stations, newest first, each selectable.</summary>
    public ObservableCollection<CountRow> Newest { get; } = [];

    /// <summary>Packets received (behavior 4.3).</summary>
    public Tile Packets { get; } = new();

    /// <summary>Packets by source, with more than one source.</summary>
    public ObservableCollection<CountRow> BySource { get; } = [];

    /// <summary>How packets arrive (behavior 4.4): the RF share's sparkline and a row a kind.</summary>
    public Tile Arrivals { get; } = new();

    /// <summary>The arrival kinds, with counts and shares.</summary>
    public ObservableCollection<CountRow> ArrivalRows { get; } = [];

    /// <summary>Packet types (behavior 4.5), most first.</summary>
    public ObservableCollection<CountRow> Kinds { get; } = [];

    /// <summary>Problems (behavior 4.6).</summary>
    public Tile Problems { get; } = new();

    /// <summary>Each source's connection (behavior 4.7).</summary>
    public ObservableCollection<SourceRow> Connections { get; } = [];

    /// <summary>Reconnects since the engine started.</summary>
    [ObservableProperty]
    public partial string? ReconnectsText { get; private set; }

    /// <summary>The busiest stations (behavior 4.8), each selectable.</summary>
    public ObservableCollection<CountRow> BusiestStations { get; } = [];

    /// <summary>The busiest digipeaters and IGates (behavior 4.9), each selectable.</summary>
    public ObservableCollection<CountRow> BusiestRelays { get; } = [];

    /// <summary>The farthest direct RF hops (behavior 4.10): each station, its distance and the IGate.</summary>
    public ObservableCollection<CountRow> FarthestDirect { get; } = [];

    /// <summary>"8 weather stations reporting"; null before the first numbers.</summary>
    [ObservableProperty]
    public partial string? WeatherText { get; private set; }

    /// <summary>The coldest, warmest and gustiest weather stations (behavior 4.11), each selectable.</summary>
    public ObservableCollection<CountRow> WeatherExtremes { get; } = [];

    /// <summary>"Of the 120 stations held now", above the tables (behavior 10).</summary>
    [ObservableProperty]
    public partial string? TablesText { get; private set; }

    /// <summary>Devices by class, vendor and model (behavior 10.1).</summary>
    public StatTable Platforms { get; }

    /// <summary>Symbols (behavior 10.2).</summary>
    public StatTable Symbols { get; }

    /// <summary>Stations, objects and items (behavior 10.3).</summary>
    public StatTable StationKindsTable { get; }

    /// <summary>How stations arrived (behavior 10.4).</summary>
    public StatTable Reception { get; }

    /// <summary>The four tables, in order.</summary>
    public IReadOnlyList<StatTable> Tables => [Platforms, Symbols, StationKindsTable, Reception];

    /// <summary>Starts asking for numbers, now and every 10 seconds, until <see cref="Stop"/> (the tab hidden).</summary>
    public void Start()
    {
        if (_running is not null)
        {
            return;
        }
        _running = new CancellationTokenSource();
        _ = RefreshLoopAsync(_running.Token);
    }

    /// <summary>Stops asking: the tab is hidden (behavior 2).</summary>
    public void Stop()
    {
        _running?.Cancel();
        _running?.Dispose();
        _running = null;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>Whether the numbers are being asked for, for tests.</summary>
    internal bool IsRunning => _running is not null;

    /// <summary>Asks for the numbers once and shows them.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Show(await _engine.GetDashboardAsync(Range, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            // The engine went away; the next refresh asks again and the last numbers stay.
        }
    }

    partial void OnRangeChanged(DashboardRange value)
    {
        foreach (var option in Ranges)
        {
            option.Refresh();
        }
        if (_running is not null)
        {
            _ = RefreshAsync(_running.Token);
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval, _time);
        try
        {
            do
            {
                await RefreshAsync(cancellationToken);
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped: the tab was hidden.
        }
    }

    private void Retranslate()
    {
        foreach (var option in Ranges)
        {
            option.Refresh();
        }
        if (_last is not null)
        {
            Show(_last);
        }
    }

    /// <summary>Turns the engine's numbers into the tiles' text and series.</summary>
    internal void Show(Dashboard dashboard)
    {
        _last = dashboard;
        var zone = _time.LocalTimeZone;
        string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).ToString("t", _text.Culture);
        AsOfText = _text.Format(
            "Dashboard.AsOf",
            new Dictionary<string, object?>
            {
                ["time"] = TimeZoneInfo.ConvertTime(dashboard.At, zone).ToString("T", _text.Culture),
            }
        );
        HistoryText =
            dashboard.HistoryStart > dashboard.Start
                ? _text.Format(
                    "Dashboard.CountingSince",
                    new Dictionary<string, object?> { ["time"] = Clock(dashboard.HistoryStart) }
                )
                : null;
        var range = _text[RangeOption.KeyOf(dashboard.Range)];
        var packets = dashboard.Packets.Sum();
        IsWaiting = packets == 0 && dashboard.ProblemsTotal == 0;

        Stations.Set(
            _text,
            "Dashboard.Stations",
            dashboard.StationsHeardTotal,
            _text.Format(
                "Dashboard.Stations.Now",
                new Dictionary<string, object?> { ["count"] = _status()?.Stations ?? 0 }
            ),
            dashboard.StationsHeard,
            range
        );
        NewStations.Set(_text, "Dashboard.NewStations", dashboard.NewStationsTotal, null, dashboard.NewStations, range);
        Replace(Newest, dashboard.Newest.Select(n => new CountRow(n.Key, Clock(n.At), 0, Select(n.Key))));

        Packets.Set(
            _text,
            "Dashboard.Packets",
            packets,
            _text.Format(
                "Dashboard.Packets.Rate",
                new Dictionary<string, object?> { ["rate"] = Math.Round(dashboard.PacketsPerMinute, 1) }
            ),
            dashboard.Packets,
            range
        );
        Replace(
            BySource,
            dashboard.PacketsBySource.Select(s =>
            {
                var count = s.Values.Sum();
                return new CountRow(SourceName(s.Key), Count(count), Fraction(count, packets), null);
            })
        );

        var byKind = dashboard.Arrivals.ToDictionary(a => a.Key, a => a.Values);
        int[] rf =
        [
            .. Enumerable
                .Range(0, dashboard.Packets.Count)
                .Select(i =>
                    Value(byKind, nameof(ArrivalKind.Direct), i) + Value(byKind, nameof(ArrivalKind.Digipeated), i)
                ),
        ];
        var rfTotal = rf.Sum();
        Arrivals.Set(
            _text,
            "Dashboard.Arrivals",
            null,
            _text.Format(
                "Dashboard.Arrivals.Rf",
                new Dictionary<string, object?> { ["percent"] = Percent(rfTotal, packets) }
            ),
            [.. rf.Select((count, i) => dashboard.Packets[i] == 0 ? 0 : 100 * count / dashboard.Packets[i])],
            range,
            Percent(rfTotal, packets)
        );
        Replace(
            ArrivalRows,
            new[] { ArrivalKind.Direct, ArrivalKind.Digipeated, ArrivalKind.Internet, ArrivalKind.Unknown }
                .Select(kind => (kind, count: byKind.TryGetValue(kind.ToString(), out var v) ? v.Sum() : 0))
                .Where(a => a.kind != ArrivalKind.Unknown || a.count > 0)
                .Select(a => new CountRow(
                    _text[ArrivalKey(a.kind)],
                    ShareText(a.count, packets),
                    Fraction(a.count, packets),
                    null
                ))
        );
        Replace(
            Kinds,
            dashboard.Kinds.Select(k => new CountRow(
                _text[KindKey(k.Key)],
                ShareText(k.Count, packets),
                Fraction(k.Count, packets),
                null
            ))
        );

        Problems.Set(_text, "Dashboard.Problems", dashboard.ProblemsTotal, null, dashboard.Problems, range);

        var status = _status();
        var lastBySource = dashboard.Sources.ToDictionary(s => s.Key, s => s.At);
        Replace(
            Connections,
            (status?.Sources ?? []).Select(source =>
                SourceRow.Of(
                    _text,
                    source,
                    Last(lastBySource, source.Id),
                    dashboard.At,
                    Clock,
                    source.Kind == SourceKind.AprsIs ? _aprsIs : null
                )
            )
        );
        ReconnectsText = status is null
            ? null
            : _text.Format("Dashboard.Reconnects", new Dictionary<string, object?> { ["count"] = status.Reconnects });

        ShowExtras(dashboard);

        var top = dashboard.BusiestStations.Count > 0 ? dashboard.BusiestStations[0].Count : 0;
        Replace(
            BusiestStations,
            dashboard.BusiestStations.Select(s => new CountRow(
                s.Key,
                Count(s.Count),
                Fraction(s.Count, top),
                Select(s.Key)
            ))
        );
        var topRelay = dashboard.BusiestRelays.Count > 0 ? dashboard.BusiestRelays[0].Count : 0;
        Replace(
            BusiestRelays,
            dashboard.BusiestRelays.Select(r => new CountRow(
                r.Key,
                Count(r.Count),
                Fraction(r.Count, topRelay),
                Select(r.Key)
            ))
        );
    }

    private void ShowExtras(Dashboard dashboard)
    {
        var units = _units();
        Replace(
            FarthestDirect,
            (dashboard.FarthestDirect ?? []).Select(h => new CountRow(
                h.Station,
                _text.Format(
                    "Dashboard.Farthest.Row",
                    new Dictionary<string, object?>
                    {
                        ["distance"] = Localization.RangeText.Distance(h.DistanceKm, units.Distance, _text),
                        ["gateway"] = h.Gateway,
                    }
                ),
                0,
                Select(h.Station)
            ))
        );
        var weather = dashboard.Weather;
        WeatherText = weather is null
            ? null
            : _text.Format("Dashboard.Weather.Count", new Dictionary<string, object?> { ["count"] = weather.Stations });
        var extremes = new List<CountRow>();
        if (weather?.Coldest is { } cold)
        {
            extremes.Add(
                Extreme(
                    "Dashboard.Weather.Coldest",
                    cold,
                    Localization.WeatherText.Temperature(cold.Value, units.Temperature, _text)
                )
            );
        }
        if (weather?.Warmest is { } warm)
        {
            extremes.Add(
                Extreme(
                    "Dashboard.Weather.Warmest",
                    warm,
                    Localization.WeatherText.Temperature(warm.Value, units.Temperature, _text)
                )
            );
        }
        if (weather?.Gustiest is { } gust)
        {
            extremes.Add(
                Extreme(
                    "Dashboard.Weather.Gustiest",
                    gust,
                    Localization.WeatherText.Speed(gust.Value, units.Wind, _text)
                )
            );
        }
        Replace(WeatherExtremes, extremes);

        if (dashboard.Tables is not { } tables)
        {
            return;
        }
        TablesText = _text.Format("Dashboard.Tables.Of", new Dictionary<string, object?> { ["count"] = tables.Total });
        Platforms.Show(
            tables.Platforms.Select(p =>
                (
                    p.Vendor is null && p.Model is null
                        ? _text["Dashboard.Table.UnknownDevice"]
                        : string.Join(' ', new[] { p.Vendor, p.Model }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    p.Class is { } c ? Label($"Filter.DeviceClass.{c}", c) : null,
                    p.Count,
                    p.Class is { } cls ? new StationFilter(DeviceClasses: [cls]) : (StationFilter?)null
                )
            ),
            _useFilter
        );
        Symbols.Show(
            tables.Symbols.Select(s =>
                (
                    s.Name,
                    (string?)Label($"Filter.Category.{s.Category}", s.Category),
                    s.Count,
                    (StationFilter?)new StationFilter(Categories: [s.Category])
                )
            ),
            _useFilter
        );
        StationKindsTable.Show(
            tables.Kinds.Select(k =>
                (
                    _text[StationKindKey(k.Key)],
                    (string?)null,
                    k.Count,
                    (StationFilter?)
                        new StationFilter(
                            Kinds: k.Key == nameof(StationKind.Station)
                                ? StationKinds.Stations
                                : StationKinds.ObjectsAndItems
                        )
                )
            ),
            _useFilter
        );
        Reception.Show(
            tables.Reception.Select(r =>
                (
                    _text[ArrivalKey(Enum.TryParse<ArrivalKind>(r.Key, out var kind) ? kind : ArrivalKind.Unknown)],
                    (string?)null,
                    r.Count,
                    r.Key switch
                    {
                        nameof(ArrivalKind.Direct) => new StationFilter(Reception: Contracts.Reception.Direct),
                        nameof(ArrivalKind.Digipeated) => new StationFilter(Reception: Contracts.Reception.Digipeated),
                        nameof(ArrivalKind.Internet) => new StationFilter(Reception: Contracts.Reception.Internet),
                        _ => (StationFilter?)null,
                    }
                )
            ),
            _useFilter
        );
    }

    /// <summary>"KE0PVI · Coldest 44 °F": the station is the link.</summary>
    private CountRow Extreme(string key, DashboardReading reading, string value) =>
        new(
            reading.Station,
            _text.Format(
                "Dashboard.Weather.Row",
                new Dictionary<string, object?> { ["what"] = _text[key], ["value"] = value }
            ),
            0,
            Select(reading.Station)
        );

    /// <summary>A label from the strings when there is one, the value as it is otherwise.</summary>
    private string Label(string key, string fallback) => Localizer.Exists(key) ? _text[key] : fallback;

    private static string StationKindKey(string kind) =>
        kind switch
        {
            nameof(StationKind.Object) => "Dashboard.Table.Kind.Object",
            nameof(StationKind.Item) => "Dashboard.Table.Kind.Item",
            _ => "Dashboard.Table.Kind.Station",
        };

    /// <summary>Opens the packet list with Issues only on (behavior 5).</summary>
    [RelayCommand]
    private void ShowProblems() => _showProblems();

    private RelayCommand Select(string name) => new RelayCommand(() => _select(name));

    // Written out key by key, so the localization guard sees every string.
    private static string ArrivalKey(ArrivalKind kind) =>
        kind switch
        {
            ArrivalKind.Direct => "Dashboard.Arrival.Direct",
            ArrivalKind.Digipeated => "Dashboard.Arrival.Digipeated",
            ArrivalKind.Internet => "Dashboard.Arrival.Internet",
            _ => "Dashboard.Arrival.Unknown",
        };

    private static string KindKey(string kind) =>
        kind switch
        {
            nameof(PacketKind.Position) => "Dashboard.Kind.Position",
            nameof(PacketKind.MicE) => "Dashboard.Kind.MicE",
            nameof(PacketKind.ObjectOrItem) => "Dashboard.Kind.ObjectOrItem",
            nameof(PacketKind.Message) => "Dashboard.Kind.Message",
            nameof(PacketKind.Status) => "Dashboard.Kind.Status",
            nameof(PacketKind.Weather) => "Dashboard.Kind.Weather",
            nameof(PacketKind.Telemetry) => "Dashboard.Kind.Telemetry",
            _ => "Dashboard.Kind.Other",
        };

    private string SourceName(string id) => id.Length == 0 ? _text["Dashboard.Source.Unnamed"] : _sources.NameOf(id);

    /// <summary>A source's last packet; a feed without source ids stands for the only source.</summary>
    private static DateTimeOffset? Last(Dictionary<string, DateTimeOffset> last, string id) =>
        last.TryGetValue(id, out var at) ? at
        : last.Count == 1 && last.TryGetValue(string.Empty, out var only) ? only
        : null;

    private string Count(int count) =>
        _text.Format("Dashboard.Count", new Dictionary<string, object?> { ["count"] = count });

    private string ShareText(int count, int total) =>
        _text.Format(
            "Dashboard.Share",
            new Dictionary<string, object?> { ["count"] = count, ["percent"] = Percent(count, total) }
        );

    private static int Percent(int count, int total) =>
        total == 0 ? 0 : (int)Math.Round(100.0 * count / total, MidpointRounding.AwayFromZero);

    private static double Fraction(int count, int total) => total == 0 ? 0 : (double)count / total;

    private static int Value(Dictionary<string, IReadOnlyList<int>> series, string key, int i) =>
        series.TryGetValue(key, out var values) && i < values.Count ? values[i] : 0;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>One range button.</summary>
    public sealed partial class RangeOption(DashboardRange range, DashboardViewModel owner) : ObservableObject
    {
        /// <summary>The range.</summary>
        public DashboardRange Range => range;

        /// <summary>"Last hour", "6 hours", "24 hours".</summary>
        public string Label => owner._text[KeyOf(range)];

        /// <summary>Whether this is the range shown; setting it picks it.</summary>
        public bool IsSelected
        {
            get => owner.Range == range;
            set
            {
                if (value)
                {
                    owner.Range = range;
                }
            }
        }

        internal static string KeyOf(DashboardRange range) =>
            range switch
            {
                DashboardRange.SixHours => "Dashboard.Range.SixHours",
                DashboardRange.Day => "Dashboard.Range.Day",
                _ => "Dashboard.Range.Hour",
            };

        internal void Refresh()
        {
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(Label));
        }
    }
}

/// <summary>
/// A tile's headline: its number, a line under it, and a sparkline over the range, with the whole read as one sentence
/// to a screen reader (behavior 4).
/// </summary>
public sealed partial class Tile : ObservableObject
{
    /// <summary>The tile's title.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    /// <summary>The big number, written for the culture; empty for a tile without one.</summary>
    [ObservableProperty]
    public partial string Number { get; private set; } = string.Empty;

    /// <summary>The line under the number; null for none.</summary>
    [ObservableProperty]
    public partial string? Detail { get; private set; }

    /// <summary>The sparkline's values, oldest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<double?> Series { get; private set; } = [];

    /// <summary>The tile in words, for a screen reader.</summary>
    [ObservableProperty]
    public partial string Spoken { get; private set; } = string.Empty;

    internal void Set(
        Localizer text,
        string key,
        int? number,
        string? detail,
        IReadOnlyList<int> series,
        string range,
        int? percent = null
    )
    {
        Title = text[key];
        Number =
            percent is { } p ? text.Format("Dashboard.Percent", new Dictionary<string, object?> { ["percent"] = p })
            : number is { } n ? text.Format("Dashboard.Number", new Dictionary<string, object?> { ["count"] = n })
            : string.Empty;
        Detail = detail;
        Series = [.. series.Select(v => (double?)v)];
        Spoken = text.Format(
            "Dashboard.Spoken",
            new Dictionary<string, object?>
            {
                ["title"] = Title,
                ["number"] = Number,
                ["range"] = range,
                ["detail"] = detail ?? string.Empty,
            }
        );
    }
}

/// <summary>A row in a tile's list: a name, its count or share, a bar's length, and what clicking it does.</summary>
/// <param name="Name">The station, source or kind.</param>
/// <param name="Value">Its count, share or time, written out.</param>
/// <param name="Fraction">The bar's length, 0 to 1, against the tile's largest or total.</param>
/// <param name="Open">Selects the station; null where a row isn't a station.</param>
public sealed record CountRow(string Name, string Value, double Fraction, IRelayCommand? Open)
{
    /// <summary>Whether the row selects a station.</summary>
    public bool CanOpen => Open is not null;
}

/// <summary>
/// One source's connection: its name, state and the time since its last packet, quiet in words too, and its on/off
/// switch where it has one (APRS-IS; KISS sources gain theirs with F3.1).
/// </summary>
public sealed record SourceRow(string Name, string State, string? Last, bool IsQuiet, SourceSwitch? Switch = null)
{
    /// <summary>Whether the row has an on/off switch.</summary>
    public bool HasSwitch => Switch is not null;

    internal static SourceRow Of(
        Localizer text,
        SourceStatus source,
        DateTimeOffset? last,
        DateTimeOffset now,
        Func<DateTimeOffset, string> clock,
        SourceSwitch? onOff = null
    )
    {
        var state = text[
            source.State switch
            {
                ConnectionState.Connecting => "Dashboard.State.Connecting",
                ConnectionState.Connected => "Dashboard.State.Connected",
                ConnectionState.Reconnecting => "Dashboard.State.Reconnecting",
                ConnectionState.NotConfigured => "Dashboard.State.NotConfigured",
                _ => "Dashboard.State.Disconnected",
            }
        ];
        var quiet =
            source.State == ConnectionState.Connected && (last is null || now - last > DashboardViewModel.QuietAfter);
        var lastText =
            last is { } at
                ? text.Format(
                    quiet ? "Dashboard.Source.Quiet" : "Dashboard.Source.Last",
                    new Dictionary<string, object?>
                    {
                        ["seconds"] = (int)Math.Max(0, (now - at).TotalSeconds),
                        ["minutes"] = (int)Math.Max(0, (now - at).TotalMinutes),
                        ["time"] = clock(at),
                    }
                )
            : quiet ? text["Dashboard.Source.NoPackets"]
            : null;
        return new SourceRow(source.Name, state, lastText, quiet, onOff);
    }
}
