// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Stations;

/// <summary>
/// Saved filters (docs/features/F1.42-saved-filters.md): the profile's filters saved under names, and which of All, My
/// filter or a saved filter this viewer has in use. My filter's conditions are kept apart, so using a saved filter never
/// loses them.
/// </summary>
public sealed partial class ViewFilterViewModel
{
    private IReadOnlyList<SavedFilter> _saved = [];
    private bool _canSave;
    private string? _inUseId;
    private string? _pendingSavedId; // kept as in use before the profile's filters are known
    private StationFilter _mine = StationFilter.None;

    /// <summary>All, My filter and each saved filter, for the chip's menu and View › Filter (behavior 4).</summary>
    public ObservableCollection<FilterChoice> Choices { get; } = [];

    /// <summary>The saved filter in use; null for All or My filter.</summary>
    public SavedFilter? InUse => _inUseId is { } id ? _saved.FirstOrDefault(s => s.Id == id) : null;

    /// <summary>Whether the conditions differ from the saved filter in use: "Storm spotting (changed)" (behavior 6).</summary>
    [ObservableProperty]
    public partial bool IsChanged { get; private set; }

    /// <summary>Whether this viewer may save, change and delete saved filters (behavior 2).</summary>
    public bool CanSave => _canSave;

    /// <summary>Whether the chip shows: something's filtered, or there are saved filters to switch to.</summary>
    public bool ShowsChip => IsFiltered || _saved.Count > 0;

    /// <summary>The name typed for Save filter as… (behavior 1).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
    public partial string SaveAsName { get; set; } = string.Empty;

    /// <summary>Whether Save filter as… keeps the map view too; unticked by default (decision 3).</summary>
    [ObservableProperty]
    public partial bool SaveAsUseView { get; set; }

    /// <summary>Why the last save was refused, in words; null when it wasn't.</summary>
    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    /// <summary>Saves the profile's filters as given, returning why it was refused, or null; set by the app.</summary>
    internal Func<IReadOnlyList<SavedFilter>, Task<string?>>? SaveFilters { get; set; }

    /// <summary>Where the map is now, for Use this map view; set by the app.</summary>
    internal Func<SavedMapView?>? CurrentView { get; set; }

    /// <summary>Raised when a saved filter with a map view is chosen: the map moves there (behavior 5).</summary>
    public event EventHandler<SavedMapView>? MapViewRequested;

    /// <summary>The active profile's saved filters, and whether this viewer may change them (behavior 2).</summary>
    internal void ShowSaved(IReadOnlyList<SavedFilter> saved, bool canSave)
    {
        _saved = saved;
        _canSave = canSave;
        if (_pendingSavedId is { } pending && saved.FirstOrDefault(s => s.Id == pending) is { } kept)
        {
            _pendingSavedId = null;
            Use(kept, moveMap: false); // as this viewer left it
        }
        else if (_inUseId is not null && InUse is null)
        {
            Choose(FilterChoiceKind.Mine, null); // deleted, or another profile's: My filter again
        }
        else if (InUse is { } inUse && !IsChanged && !SavedFilterText.Same(inUse.Filter, Conditions))
        {
            Use(inUse, moveMap: false); // saved over by someone else: shown as it is now
        }
        RefreshChoices();
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowsChip));
        SaveAsCommand.NotifyCanExecuteChanged();
        SaveOverCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Switches to All, My filter or a saved filter (behaviors 4, 5).</summary>
    internal void Choose(FilterChoiceKind kind, string? savedId)
    {
        switch (kind)
        {
            case FilterChoiceKind.All:
                IsOn = false;
                break;
            case FilterChoiceKind.Mine:
                _inUseId = null;
                IsChanged = false;
                Set(true, _mine);
                _ = SaveAsync();
                break;
            case FilterChoiceKind.Saved when _saved.FirstOrDefault(s => s.Id == savedId) is { } saved:
                Use(saved, moveMap: true);
                _ = SaveAsync();
                break;
        }
        RefreshChoices();
    }

    private void Use(SavedFilter saved, bool moveMap)
    {
        if (_inUseId is null && IsOn)
        {
            _mine = Conditions; // kept for coming back
        }
        _inUseId = saved.Id;
        IsChanged = false;
        Set(true, saved.Filter);
        if (moveMap && saved.View is { } view)
        {
            MapViewRequested?.Invoke(this, view);
        }
        OnPropertyChanged(nameof(InUse));
    }

    /// <summary>All → My filter → each saved filter → All (behavior 4): Ctrl+Shift+F.</summary>
    [RelayCommand]
    private void NextFilter()
    {
        var at = Choices.ToList().FindIndex(c => c.IsChecked);
        var next = Choices[(at + 1) % Choices.Count];
        Choose(next.Kind, next.SavedId);
    }

    /// <summary>
    /// A condition changed (behavior 6): with a saved filter in use it's "(changed)", or, for a viewer who may not save,
    /// the conditions become their My filter.
    /// </summary>
    private void NoteChange()
    {
        if (InUse is not { } inUse)
        {
            if (IsOn)
            {
                _mine = Conditions;
            }
            return;
        }
        if (!_canSave)
        {
            _inUseId = null;
            _mine = Conditions;
            IsChanged = false;
            OnPropertyChanged(nameof(InUse));
            RefreshChoices();
            return;
        }
        IsChanged = !SavedFilterText.Same(inUse.Filter, Conditions);
    }

    private bool CanSaveAs => _canSave && SaveAsName.Trim().Length > 0;

    /// <summary>Saves the conditions shown under a new name, with the map view if ticked, and uses it (behavior 1).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private async Task SaveAsAsync()
    {
        if (_saved.Any(s => string.Equals(s.Name, SaveAsName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            SaveError = _text["Error.settings.saved-filter.name-taken"]; // said at once, before asking the engine
            return;
        }
        var saved = SavedFilter.Create(SaveAsName, Conditions, SaveAsUseView ? CurrentView?.Invoke() : null);
        if (await SaveAllAsync([.. _saved, saved]))
        {
            SaveAsName = string.Empty;
            SaveAsUseView = false;
            _saved = [.. _saved, saved];
            Use(saved, moveMap: false);
            _ = SaveAsync();
            RefreshChoices();
        }
    }

    private bool CanSaveOver => _canSave && InUse is not null;

    /// <summary>Saves the conditions shown over the saved filter in use, for every viewer (behavior 6).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveOver))]
    private async Task SaveOverAsync()
    {
        if (InUse is not { } inUse)
        {
            return;
        }
        var changed = inUse with { Filter = Conditions };
        if (await SaveAllAsync([.. _saved.Select(s => s.Id == inUse.Id ? changed : s)]))
        {
            _saved = [.. _saved.Select(s => s.Id == inUse.Id ? changed : s)];
            IsChanged = false;
            RefreshChoices();
        }
    }

    /// <summary>The saved filter in use as it was saved (behavior 6).</summary>
    [RelayCommand]
    private void BackToSaved()
    {
        if (InUse is { } inUse)
        {
            Use(inUse, moveMap: false);
            RefreshChoices();
        }
    }

    private async Task<bool> SaveAllAsync(IReadOnlyList<SavedFilter> filters)
    {
        SaveError = null;
        if (SaveFilters is null)
        {
            return false;
        }
        SaveError = await SaveFilters(filters);
        return SaveError is null;
    }

    private void RefreshChoices()
    {
        Choices.Clear();
        Choices.Add(new FilterChoice(FilterChoiceKind.All, null, _text["Filter.Choice.All"], !IsOn, this));
        Choices.Add(
            new FilterChoice(FilterChoiceKind.Mine, null, _text["Filter.Choice.Mine"], IsOn && _inUseId is null, this)
        );
        foreach (var saved in _saved)
        {
            Choices.Add(
                new FilterChoice(FilterChoiceKind.Saved, saved.Id, saved.Name, IsOn && _inUseId == saved.Id, this)
            );
        }
        OnPropertyChanged(nameof(ShowsChip));
        SaveOverCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>What a filter choice is.</summary>
public enum FilterChoiceKind
{
    /// <summary>Every station.</summary>
    All,

    /// <summary>This viewer's own filter.</summary>
    Mine,

    /// <summary>One of the profile's saved filters.</summary>
    Saved,
}

/// <summary>A choice in the chip's menu and View › Filter (F1.42 behavior 4).</summary>
public sealed partial class FilterChoice(
    FilterChoiceKind kind,
    string? savedId,
    string label,
    bool isChecked,
    ViewFilterViewModel owner
)
{
    /// <summary>What it is.</summary>
    public FilterChoiceKind Kind { get; } = kind;

    /// <summary>The saved filter's id, for a saved filter.</summary>
    public string? SavedId { get; } = savedId;

    /// <summary>"All", "My filter" or the saved filter's name.</summary>
    public string Label { get; } = label;

    /// <summary>Whether it's the one in use.</summary>
    public bool IsChecked { get; } = isChecked;

    /// <summary>Switches to it.</summary>
    [RelayCommand]
    private void Choose() => owner.Choose(Kind, SavedId);
}
