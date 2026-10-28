// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Text.Json;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Stations;

/// <summary>
/// The View filter (docs/features/F1.9-view-filters.md): which stations the map and the station list show, without
/// changing what APRS-IS sends. One switch goes between everything and My filter (behavior 5); the conditions are kept
/// either way, and per viewer (behavior 7).
/// </summary>
public sealed partial class ViewFilterViewModel : ObservableObject
{
    /// <summary>The preference key the filter is kept under.</summary>
    internal const string Key = "view-filter";

    private static readonly int?[] HeardSteps = [null, 10, 30, 60, 120];

    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private IPreferenceStore? _store;
    private bool _loading;

    internal ViewFilterViewModel(IEngineService engine, Localizer text)
    {
        _engine = engine;
        _text = text;
        KindOptions = [.. Enum.GetValues<StationKinds>().Select(k => new FilterOption(k, $"Filter.Kinds.{k}"))];
        HeardOptions =
        [
            .. HeardSteps.Select(m => new FilterOption(m, m is null ? "Filter.Heard.Any" : "Filter.Heard.Within")),
        ];
        ReceptionOptions = [.. Enum.GetValues<Reception>().Select(r => new FilterOption(r, $"Filter.Reception.{r}"))];
        _loading = true; // the defaults aren't a change
        Kind = KindOptions[0];
        Heard = HeardOptions[0];
        ReceptionChoice = ReceptionOptions[0];
        _loading = false;
        Retranslate();
        _text.LanguageChanged += (_, _) => Retranslate();
    }

    /// <summary>Raised when the filter in effect changes: the map and list ask again.</summary>
    public event EventHandler? Changed;

    /// <summary>My filter (true) or everything (false), behavior 5.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsFiltered))]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial FilterOption Kind { get; set; }

    [ObservableProperty]
    public partial FilterOption Heard { get; set; }

    [ObservableProperty]
    public partial FilterOption ReceptionChoice { get; set; }

    [ObservableProperty]
    public partial bool MessageCapable { get; set; }

    [ObservableProperty]
    public partial bool WithWeather { get; set; }

    [ObservableProperty]
    public partial bool WithTrack { get; set; }

    [ObservableProperty]
    public partial bool WithTelemetry { get; set; }

    [ObservableProperty]
    public partial bool HasProblems { get; set; }

    public IReadOnlyList<FilterOption> KindOptions { get; }

    public IReadOnlyList<FilterOption> HeardOptions { get; }

    public IReadOnlyList<FilterOption> ReceptionOptions { get; }

    /// <summary>Symbol categories heard, with counts (behavior 2).</summary>
    public ObservableCollection<FacetChoice> Categories { get; } = [];

    /// <summary>Device classes heard, with counts.</summary>
    public ObservableCollection<FacetChoice> DeviceClasses { get; } = [];

    /// <summary>Mic-E statuses heard, with counts.</summary>
    public ObservableCollection<FacetChoice> MicEStatuses { get; } = [];

    /// <summary>Tags heard, with counts.</summary>
    public ObservableCollection<FacetChoice> Tags { get; } = [];

    /// <summary>The active profile's station lists (docs/features/F1.33-station-lists.md, behavior 6), by name.</summary>
    public ObservableCollection<FacetChoice> Lists { get; } = [];

    // Lists ticked in a kept filter before the profile's lists are known.
    private HashSet<string> _tickedLists = new(StringComparer.Ordinal);

    /// <summary>Offers the active profile's <paramref name="lists"/> (behavior 6), keeping what was ticked.</summary>
    internal void ShowLists(IReadOnlyList<StationList> lists)
    {
        _tickedLists.UnionWith(Lists.Where(c => c.IsChecked).Select(c => c.Value));
        var ticked = _tickedLists;
        foreach (var choice in Lists)
        {
            choice.PropertyChanged -= OnChoiceChanged;
        }
        Lists.Clear();
        foreach (var list in lists)
        {
            var choice = new FacetChoice(list.Id, null, list.Name) { IsChecked = ticked.Contains(list.Id) };
            choice.Retranslate(_text, 0);
            choice.PropertyChanged += OnChoiceChanged;
            Lists.Add(choice);
        }
        _tickedLists = new HashSet<string>(StringComparer.Ordinal);
        OnPropertyChanged(nameof(Current));
    }

    /// <summary>The conditions as chosen, whether or not they're in effect.</summary>
    public StationFilter Conditions =>
        new(
            (StationKinds)Kind.Value!,
            Checked(Categories),
            (int?)Heard.Value,
            (Reception)ReceptionChoice.Value!,
            MessageCapable,
            WithWeather,
            WithTrack,
            Checked(DeviceClasses),
            Checked(MicEStatuses),
            Checked(Tags),
            WithTelemetry,
            HasProblems,
            Lists.Count > 0 ? Checked(Lists)
                : _tickedLists.Count > 0 ? [.. _tickedLists]
                : null
        );

    /// <summary>The filter in effect: the conditions with My filter on, otherwise none.</summary>
    public StationFilter? Current => IsOn && !Conditions.IsEmpty ? Conditions : null;

    /// <summary>Whether a filter is in effect, for the map's chip (behavior 4).</summary>
    public bool IsFiltered => Current is not null;

    /// <summary>"Filtered · 312 of 2,140 · emergencies always shown" (behaviors 3, 4); null when nothing is filtered.</summary>
    [ObservableProperty]
    public partial string? ChipText { get; private set; }

    /// <summary>Writes the chip for <paramref name="shown"/> of <paramref name="total"/> stations.</summary>
    internal void ShowCounts(int shown, int total)
    {
        // With station lists, the chip names them (F1.33 behavior 8).
        var lists = string.Join(", ", Lists.Where(c => c.IsChecked).Select(c => c.Label));
        ChipText =
            !IsFiltered ? null
            : lists.Length > 0
                ? _text.Format(
                    "Filter.ChipLists",
                    new Dictionary<string, object?>
                    {
                        ["lists"] = lists,
                        ["shown"] = shown,
                        ["total"] = total,
                    }
                )
            : _text.Format("Filter.Chip", new Dictionary<string, object?> { ["shown"] = shown, ["total"] = total });
    }

    /// <summary>Shows everything, keeping the conditions for next time (behavior 5).</summary>
    [RelayCommand]
    private void ShowAll() => IsOn = false;

    /// <summary>Clears every condition.</summary>
    [RelayCommand]
    private void Reset()
    {
        _loading = true;
        Kind = KindOptions[0];
        Heard = HeardOptions[0];
        ReceptionChoice = ReceptionOptions[0];
        MessageCapable = WithWeather = WithTrack = WithTelemetry = HasProblems = false;
        foreach (var choice in Categories.Concat(DeviceClasses).Concat(MicEStatuses).Concat(Tags).Concat(Lists))
        {
            choice.IsChecked = false;
        }
        _tickedLists.Clear();
        _loading = false;
        Apply();
    }

    /// <summary>Fetches what's been heard, keeping what was ticked (behavior 2).</summary>
    internal async Task RefreshFacetsAsync()
    {
        ViewFacets facets;
        try
        {
            facets = await _engine.GetViewFacetsAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return;
        }
        var filter = Conditions;
        Fill(Categories, facets.Categories, filter.Categories, v => $"Filter.Category.{v}");
        Fill(DeviceClasses, facets.DeviceClasses, filter.DeviceClasses, v => $"Filter.DeviceClass.{v}");
        Fill(MicEStatuses, facets.MicEStatuses, filter.MicEStatuses, v => $"Filter.MicE.{v}");
        Fill(Tags, facets.Tags, filter.Tags, null);
    }

    /// <summary>Loads this viewer's filter (behavior 7).</summary>
    internal async Task LoadAsync(IPreferenceStore? store)
    {
        _store = store;
        if (store is null)
        {
            return;
        }
        await LoadAlsoHeardAsync(store); // F1.43 behavior 1
        string? json;
        try
        {
            json = await store.LoadAsync(Key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return;
        }
        if (Read(json) is not var (on, filter))
        {
            return;
        }
        Set(on, filter);
    }

    /// <summary>
    /// Sets the conditions to <paramref name="filter"/> and turns the filter on, as one change: the dashboard's table
    /// rows use it (F1.22 behavior 12).
    /// </summary>
    internal void Use(StationFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Set(true, filter);
        Commit();
    }

    private void Set(bool on, StationFilter filter)
    {
        _loading = true;
        Kind = KindOptions.FirstOrDefault(o => Equals(o.Value, filter.Kinds)) ?? KindOptions[0];
        Heard = HeardOptions.FirstOrDefault(o => Equals(o.Value, filter.HeardWithinMinutes)) ?? HeardOptions[0];
        ReceptionChoice =
            ReceptionOptions.FirstOrDefault(o => Equals(o.Value, filter.Reception)) ?? ReceptionOptions[0];
        MessageCapable = filter.MessageCapable;
        WithWeather = filter.WithWeather;
        WithTrack = filter.WithTrack;
        WithTelemetry = filter.WithTelemetry;
        HasProblems = filter.HasProblems;
        // Ticked values come back before anything is heard: kept as choices with no count yet.
        Fill(Categories, [], filter.Categories, v => $"Filter.Category.{v}");
        Fill(DeviceClasses, [], filter.DeviceClasses, v => $"Filter.DeviceClass.{v}");
        Fill(MicEStatuses, [], filter.MicEStatuses, v => $"Filter.MicE.{v}");
        Fill(Tags, [], filter.Tags, null);
        _tickedLists = new HashSet<string>(filter.Lists ?? [], StringComparer.Ordinal);
        foreach (var choice in Lists)
        {
            choice.IsChecked = _tickedLists.Contains(choice.Value);
        }
        if (Lists.Count > 0)
        {
            _tickedLists.Clear();
        }
        IsOn = on;
        _loading = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The kept filter, or null when the text isn't one.</summary>
    internal static (bool On, StationFilter Filter)? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var kept = JsonSerializer.Deserialize(json, ViewFilterJson.Default.KeptFilter);
            return kept?.Filter is { } filter ? (kept.On, filter) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    partial void OnIsOnChanged(bool value) => Commit();

    partial void OnKindChanged(FilterOption value) => Apply();

    partial void OnHeardChanged(FilterOption value) => Apply();

    partial void OnReceptionChoiceChanged(FilterOption value) => Apply();

    partial void OnMessageCapableChanged(bool value) => Apply();

    partial void OnWithWeatherChanged(bool value) => Apply();

    partial void OnWithTrackChanged(bool value) => Apply();

    partial void OnWithTelemetryChanged(bool value) => Apply();

    partial void OnHasProblemsChanged(bool value) => Apply();

    /// <summary>A condition changed: choosing one turns My filter on, since picking one and seeing nothing change would puzzle.</summary>
    internal void Apply()
    {
        if (_loading)
        {
            return;
        }
        if (!IsOn && !Conditions.IsEmpty)
        {
            IsOn = true; // commits
            return;
        }
        Commit();
    }

    private void Commit()
    {
        if (_loading)
        {
            return;
        }
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsFiltered));
        _ = SaveAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveAsync()
    {
        if (_store is null)
        {
            return;
        }
        try
        {
            await _store.SaveAsync(
                Key,
                JsonSerializer.Serialize(new KeptFilter(IsOn, Conditions), ViewFilterJson.Default.KeptFilter)
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Not kept this time; the filter still works.
        }
    }

    private void Fill(
        ObservableCollection<FacetChoice> choices,
        IReadOnlyList<Facet> heard,
        IReadOnlyList<string>? ticked,
        Func<string, string>? labelKey
    )
    {
        var keep = new HashSet<string>(
            choices.Where(c => c.IsChecked).Select(c => c.Value).Concat(ticked ?? []),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var choice in choices)
        {
            choice.PropertyChanged -= OnChoiceChanged;
        }
        choices.Clear();
        var values = heard.Select(f => (f.Value, f.Count)).ToList();
        values.AddRange(
            keep.Where(k => !values.Any(v => string.Equals(v.Value, k, StringComparison.OrdinalIgnoreCase)))
                .Select(k => (k, 0))
        );
        foreach (var (value, count) in values)
        {
            var choice = new FacetChoice(value, labelKey) { IsChecked = keep.Contains(value) };
            choice.Retranslate(_text, count);
            choice.PropertyChanged += OnChoiceChanged;
            choices.Add(choice);
        }
    }

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FacetChoice.IsChecked))
        {
            Apply();
        }
    }

    private void Retranslate()
    {
        foreach (var option in KindOptions.Concat(ReceptionOptions))
        {
            option.Label = _text[option.LabelKey];
        }
        foreach (var option in HeardOptions)
        {
            option.Label = option.Value is int minutes
                ? _text.Format(option.LabelKey, new Dictionary<string, object?> { ["minutes"] = minutes })
                : _text[option.LabelKey];
        }
        foreach (var choice in Categories.Concat(DeviceClasses).Concat(MicEStatuses).Concat(Tags))
        {
            choice.Retranslate(_text, choice.Count);
        }
        RetranslateAlsoHeard();
    }

    private static List<string>? Checked(IEnumerable<FacetChoice> choices) =>
        choices.Where(c => c.IsChecked).Select(c => c.Value).ToList() is { Count: > 0 } values ? values : null;
}

/// <summary>One choice in a View filter list, such as a device class with how many stations have it.</summary>
public sealed partial class FacetChoice(string value, Func<string, string>? labelKey, string? name = null)
    : ObservableObject
{
    /// <summary>The value as the engine names it.</summary>
    public string Value { get; } = value;

    /// <summary>How many stations have it now.</summary>
    public int Count { get; private set; }

    /// <summary>"Trackers (24)".</summary>
    [ObservableProperty]
    public partial string Label { get; private set; } = value;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    internal void Retranslate(Localizer text, int count)
    {
        Count = count;
        var shown = name ?? (labelKey is null ? Value : text[labelKey(Value)]);
        Label =
            count > 0
                ? text.Format("Filter.Counted", new Dictionary<string, object?> { ["name"] = shown, ["count"] = count })
                : shown;
    }
}

/// <summary>One choice of a View filter drop-down.</summary>
public sealed partial class FilterOption(object? value, string labelKey) : ObservableObject
{
    public object? Value { get; } = value;

    internal string LabelKey { get; } = labelKey;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    public override string ToString() => Label;
}

/// <summary>The filter as kept in this viewer's preferences.</summary>
internal sealed record KeptFilter(bool On, StationFilter Filter);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true
)]
[System.Text.Json.Serialization.JsonSerializable(typeof(KeptFilter))]
internal sealed partial class ViewFilterJson : System.Text.Json.Serialization.JsonSerializerContext;
