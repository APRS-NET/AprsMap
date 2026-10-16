// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.Stations;

/// <summary>
/// The Stations panel (docs/features/F1.7-station-list.md): one page of the store at a time, searched, filtered and
/// sorted by the engine, more pages as the list scrolls, refreshed at most once a second while stations change.
/// </summary>
public sealed partial class StationListViewModel : ObservableObject
{
    /// <summary>Rows asked for per page.</summary>
    public const int PageSize = 200;

    /// <summary>The preference key the sort is kept under (behavior 2).</summary>
    public const string SortKey = "stations.sort";

    /// <summary>How often the list asks again while stations change, at most (service surface).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long the search waits for typing to stop.</summary>
    public static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(300);

    private readonly IEngineService _engine;
    private readonly TimeProvider _time;
    private readonly Localizer _text;
    private readonly Func<UnitSettings> _units;
    private CancellationTokenSource? _typing;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private bool _refreshScheduled;
    private bool _changedWhileHolding;
    private int _queries;
    private bool _loadingSort;

    internal StationListViewModel(IEngineService engine, TimeProvider time, Localizer text, Func<UnitSettings> units)
    {
        _engine = engine;
        _time = time;
        _text = text;
        _units = units;
        Sorts = [.. Enum.GetValues<StationSort>().Select(s => new Choice<StationSort>(s, $"Stations.Sort.{s}", text))];
        KindOptions =
        [
            .. Enum.GetValues<StationKinds>().Select(k => new Choice<StationKinds>(k, $"Stations.Kinds.{k}", text)),
        ];
        SelectedSort = Sorts[0];
        SelectedKinds = KindOptions[0];
        _text.LanguageChanged += (_, _) => Retranslate();
        Retranslate();
    }

    /// <summary>Where this viewer's sort is kept; null keeps it for this run only.</summary>
    public IPreferenceStore? Preferences { get; set; }

    /// <summary>The rows loaded so far, in order.</summary>
    public ObservableCollection<StationListRow> Rows { get; } = [];

    /// <summary>The sort choices (behavior 2).</summary>
    public IReadOnlyList<Choice<StationSort>> Sorts { get; }

    /// <summary>The kind choices (behavior 4).</summary>
    public IReadOnlyList<Choice<StationKinds>> KindOptions { get; }

    /// <summary>Part of a name to look for (behavior 3).</summary>
    [ObservableProperty]
    public partial string? Search { get; set; }

    /// <summary>The order.</summary>
    [ObservableProperty]
    public partial Choice<StationSort> SelectedSort { get; set; }

    /// <summary>Which kinds are listed.</summary>
    [ObservableProperty]
    public partial Choice<StationKinds> SelectedKinds { get; set; }

    /// <summary>How many entries match, in all.</summary>
    [ObservableProperty]
    public partial int Total { get; private set; }

    /// <summary>"40 stations", announced to screen readers (acceptance test 6).</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>What the empty list says.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = string.Empty;

    /// <summary>Whether the list has no rows.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>
    /// While the pointer or focus is in the list, rows keep their order: a refresh updates them in place and adds new
    /// ones at the end (behavior 5).
    /// </summary>
    [ObservableProperty]
    public partial bool IsHolding { get; set; }

    /// <summary>Loads the saved sort, then the first page.</summary>
    internal async Task LoadAsync()
    {
        if (Preferences is not null)
        {
            var saved = await Preferences.LoadAsync(SortKey);
            if (
                Enum.TryParse<StationSort>(saved, out var sort)
                && Sorts.FirstOrDefault(s => s.Value == sort) is { } option
            )
            {
                _loadingSort = true;
                SelectedSort = option;
                _loadingSort = false;
            }
        }
        await RefreshAsync();
    }

    /// <summary>A station stream batch: ask again, at most once per <see cref="RefreshInterval"/>.</summary>
    internal void Changed()
    {
        if (_time.GetUtcNow() - _lastRefresh >= RefreshInterval)
        {
            _ = RefreshAsync();
        }
        else if (!_refreshScheduled)
        {
            _refreshScheduled = true;
            _ = RefreshLaterAsync();
        }
    }

    /// <summary>The first page again, with the current search, kinds and sort; held rows keep their order.</summary>
    internal async Task RefreshAsync()
    {
        _lastRefresh = _time.GetUtcNow();
        var load = ++_queries;
        var count = Math.Clamp(Rows.Count, PageSize, StationQuery.MaxCount);
        StationPage page;
        try
        {
            page = await _engine.QueryStationsAsync(Query(0, count));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return; // the gateway is away; the next change asks again
        }
        if (load != _queries)
        {
            return; // a later query won
        }
        if (IsHolding)
        {
            _changedWhileHolding = true;
            Merge(page.Rows);
        }
        else
        {
            Rows.Clear();
            foreach (var row in page.Rows)
            {
                Rows.Add(new StationListRow(row));
            }
        }
        Show(page.Total);
    }

    /// <summary>The next page, when the list is scrolled to its end (behavior 6).</summary>
    internal async Task LoadMoreAsync()
    {
        if (Rows.Count >= Total)
        {
            return;
        }
        var load = ++_queries;
        StationPage page;
        try
        {
            page = await _engine.QueryStationsAsync(Query(Rows.Count, PageSize));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return;
        }
        if (load != _queries)
        {
            return;
        }
        Merge(page.Rows);
        Show(page.Total);
    }

    /// <summary>Writes the relative times and distances again (the clock or units moved on).</summary>
    internal void Rewrite()
    {
        var now = _time.GetUtcNow();
        var units = _units();
        foreach (var row in Rows)
        {
            row.Write(_text, now, units);
        }
    }

    /// <summary>The View filter in effect (F1.9 behavior 6: the list shows what the map shows).</summary>
    internal Func<StationFilter?> Filter { get; set; } = () => null;

    private StationQuery Query(int offset, int count) =>
        new(
            string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
            SelectedKinds.Value,
            SelectedSort.Value,
            offset,
            count,
            Filter()
        );

    /// <summary>Updates rows already listed in place and adds the others at the end.</summary>
    private void Merge(IReadOnlyList<StationRow> rows)
    {
        var listed = Rows.ToDictionary(r => r.Name, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (listed.TryGetValue(row.Name, out var existing))
            {
                existing.Update(row);
            }
            else
            {
                Rows.Add(new StationListRow(row));
            }
        }
    }

    private void Show(int total)
    {
        Total = total;
        IsEmpty = Rows.Count == 0;
        Rewrite();
        Retranslate();
    }

    partial void OnSearchChanged(string? value) => _ = RefreshAfterTypingAsync();

    partial void OnSelectedSortChanged(Choice<StationSort> value)
    {
        Restart();
        if (!_loadingSort && Preferences is not null)
        {
            _ = Preferences.SaveAsync(SortKey, value.Value.ToString());
        }
    }

    partial void OnSelectedKindsChanged(Choice<StationKinds> value) => Restart();

    partial void OnIsHoldingChanged(bool value)
    {
        if (!value && _changedWhileHolding)
        {
            _changedWhileHolding = false;
            _ = RefreshAsync(); // the order catches up once the pointer leaves
        }
    }

    private void Restart()
    {
        Rows.Clear();
        _ = RefreshAsync();
    }

    private async Task RefreshLaterAsync()
    {
        try
        {
            await Task.Delay(RefreshInterval, _time);
        }
        finally
        {
            _refreshScheduled = false;
        }
        await RefreshAsync();
    }

    private async Task RefreshAfterTypingAsync()
    {
        _typing?.Cancel();
        using var typing = new CancellationTokenSource();
        _typing = typing;
        try
        {
            await Task.Delay(TypingDelay, _time, typing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_typing, typing))
            {
                _typing = null;
            }
        }
        Restart();
    }

    private void Retranslate()
    {
        foreach (var option in Sorts)
        {
            option.Retranslate(_text);
        }
        foreach (var option in KindOptions)
        {
            option.Retranslate(_text);
        }
        CountText = _text.Format("Stations.Count", new Dictionary<string, object?> { ["count"] = Total });
        EmptyText = _text[
            string.IsNullOrWhiteSpace(Search) && SelectedKinds.Value == StationKinds.All
                ? "Stations.None"
                : "Stations.NoneMatching"
        ];
    }

    /// <summary>One choice of a list's sort or kinds, named in the viewer's language.</summary>
    public sealed class Choice<T>(T value, string key, Localizer text) : NamedChoice(key, text)
    {
        /// <summary>The value.</summary>
        public T Value { get; } = value;
    }
}

/// <summary>A choice in a list's drop-down, named in the viewer's language; what the drop-downs' templates show.</summary>
public abstract partial class NamedChoice(string key, Localizer text) : ObservableObject
{
    /// <summary>Its name.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = text[key];

    internal void Retranslate(Localizer text) => Name = text[key];

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>One row of the station list (F1.7 behavior 1).</summary>
public sealed partial class StationListRow(StationRow row) : ObservableObject
{
    /// <summary>The engine's row.</summary>
    public StationRow Row { get; private set; } = row;

    /// <summary>The name.</summary>
    public string Name => Row.Name;

    /// <summary>The symbol, for the icon.</summary>
    public string? Symbol => Row.Symbol;

    /// <summary>Whether the map can centre on it.</summary>
    public bool HasPosition => Row.HasPosition;

    /// <summary>Greyed when stale.</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; } = row.IsStale;

    /// <summary>Its packets had problems in the last hour: a marker on the row (F1.20 behavior 6).</summary>
    [ObservableProperty]
    public partial bool HasProblems { get; private set; } = row.HasProblems;

    /// <summary>"4 min".</summary>
    [ObservableProperty]
    public partial string HeardText { get; private set; } = string.Empty;

    /// <summary>"100 km NE 045°", or nothing.</summary>
    [ObservableProperty]
    public partial string? RangeText { get; private set; }

    /// <summary>The comment's start, control characters shown as symbols.</summary>
    [ObservableProperty]
    public partial string? Comment { get; private set; } = StationDetailsViewModel.Visible(row.Comment);

    internal void Update(StationRow row)
    {
        Row = row;
        IsStale = row.IsStale;
        HasProblems = row.HasProblems;
        Comment = StationDetailsViewModel.Visible(row.Comment);
    }

    internal void Write(Localizer text, DateTimeOffset now, UnitSettings units)
    {
        var minutes = (long)Math.Max(0, (now - Row.LastHeard).TotalMinutes);
        HeardText = text.Format(
            minutes < 60 ? "Stations.HeardMinutes" : "Stations.HeardHours",
            new Dictionary<string, object?> { ["minutes"] = minutes, ["hours"] = minutes / 60 }
        );
        RangeText = Localization.RangeText.Format(Row.DistanceKm, Row.BearingDegrees, units.Distance, text);
    }
}
