// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.Stations;

/// <summary>
/// Also show stations heard in the last days, from the packet history (docs/features/F1.43-map-history-range.md,
/// behavior 1): it adds stations and hides none, so it isn't a condition of the filter, but it's kept with it.
/// </summary>
public sealed partial class ViewFilterViewModel
{
    /// <summary>The preference it's kept under, beside the filter's.</summary>
    internal const string AlsoHeardKey = "view-filter-also-heard";

    private static readonly int?[] AlsoHeardSteps = [null, 1, 3, 7];

    private int? _alsoHeardWanted;

    /// <summary>Off, then 24 hours, 3 days and 7 days, up to the history's length.</summary>
    public ObservableCollection<FilterOption> AlsoHeardOptions { get; } = [];

    /// <summary>The choice; Off by default.</summary>
    [ObservableProperty]
    public partial FilterOption? AlsoHeard { get; set; }

    /// <summary>Whether it's offered: the engine keeps a history (behavior 6).</summary>
    [ObservableProperty]
    public partial bool IsAlsoHeardOffered { get; private set; }

    /// <summary>"+312 from the last 3 days", with the limit when it was reached (behavior 3); null when off.</summary>
    [ObservableProperty]
    public partial string? AlsoHeardText { get; private set; }

    /// <summary>Raised when the choice changes: the map asks again.</summary>
    public event EventHandler? AlsoHeardChanged;

    /// <summary>How many days back the map adds stations from; null for none.</summary>
    public int? AlsoHeardDays => IsAlsoHeardOffered ? AlsoHeard?.Value as int? : null;

    /// <summary>
    /// The choices the engine's history allows: <paramref name="historyDays"/> kept, or none (null). The viewer's choice
    /// stays when it still fits.
    /// </summary>
    internal void OfferAlsoHeard(int? historyDays)
    {
        var steps = AlsoHeardSteps.Where(d => d is null || d <= historyDays).ToList();
        if (
            AlsoHeardOptions.Select(o => o.Value as int?).SequenceEqual(steps)
            && IsAlsoHeardOffered == historyDays is not null
        )
        {
            return;
        }
        var wanted = (AlsoHeard?.Value as int?) ?? _alsoHeardWanted;
        _loading = true;
        try
        {
            AlsoHeardOptions.Clear();
            foreach (var days in steps)
            {
                AlsoHeardOptions.Add(
                    new FilterOption(days, days is null ? "Filter.AlsoHeard.Off" : "Filter.AlsoHeard.Days")
                );
            }
            RetranslateAlsoHeard();
            IsAlsoHeardOffered = historyDays is not null;
            AlsoHeard = AlsoHeardOptions.FirstOrDefault(o => Equals(o.Value, wanted)) ?? AlsoHeardOptions[0];
        }
        finally
        {
            _loading = false;
        }
        AlsoHeardChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAlsoHeardChanged(FilterOption? value)
    {
        if (_loading)
        {
            return;
        }
        _alsoHeardWanted = value?.Value as int?;
        if (value?.Value is null)
        {
            AlsoHeardText = null;
        }
        _ = SaveAlsoHeardAsync();
        AlsoHeardChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the history added (behavior 3).</summary>
    internal void ShowAlsoHeard(KeptStations kept)
    {
        AlsoHeardText = AlsoHeardDays is not { } days
            ? null
            : _text.Format(
                kept.Total > kept.Stations.Count ? "Filter.AlsoHeard.Limited" : "Filter.AlsoHeard.Added",
                new Dictionary<string, object?> { ["count"] = kept.Stations.Count, ["days"] = days }
            );
    }

    private void RetranslateAlsoHeard()
    {
        foreach (var option in AlsoHeardOptions)
        {
            option.Label = option.Value is int days
                ? _text.Format(option.LabelKey, new Dictionary<string, object?> { ["days"] = days })
                : _text[option.LabelKey];
        }
    }

    private async Task LoadAlsoHeardAsync(Platform.IPreferenceStore store)
    {
        try
        {
            _alsoHeardWanted = int.TryParse(
                await store.LoadAsync(AlsoHeardKey),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var days
            )
                ? days
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _alsoHeardWanted = null;
        }
        if (AlsoHeardOptions.FirstOrDefault(o => Equals(o.Value, _alsoHeardWanted)) is { } option)
        {
            AlsoHeard = option;
        }
    }

    private async Task SaveAlsoHeardAsync()
    {
        if (_store is null)
        {
            return;
        }
        try
        {
            await _store.SaveAsync(
                AlsoHeardKey,
                (AlsoHeard?.Value as int?)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Not kept this time; the choice still works.
        }
    }
}
