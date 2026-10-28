// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Lists and filters: the profile's saved filters (docs/features/F1.42-saved-filters.md, behaviors 8, 9),
/// in order, each with Rename, Delete (with Undo), Move up, Move down and Copy filter, and Paste filter… to add one from
/// its text.
/// </summary>
public sealed partial class StationListsSectionViewModel
{
    private (SavedFilter Filter, int Index)? _deleted;

    /// <summary>The saved filters, in order.</summary>
    public ObservableCollection<SavedFilterRow> SavedFilters { get; } = [];

    /// <summary>The saved filter being renamed; null for none.</summary>
    [ObservableProperty]
    public partial SavedFilterRow? Renaming { get; private set; }

    /// <summary>The new name typed while renaming.</summary>
    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    /// <summary>"Storm spotting deleted", with Undo; null when nothing was just deleted.</summary>
    [ObservableProperty]
    public partial string? DeletedText { get; private set; }

    /// <summary>Paste filter…: the name for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPastedFilterCommand))]
    public partial string PastedName { get; set; } = string.Empty;

    /// <summary>Paste filter…: its text, as Copy filter gives it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPastedFilterCommand))]
    public partial string PastedText { get; set; } = string.Empty;

    private void ShowSavedFilters(StationProfile? profile)
    {
        SavedFilters.Clear();
        var lists = profile?.StationListSet ?? [];
        foreach (var saved in profile?.SavedFilterSet ?? [])
        {
            SavedFilters.Add(new SavedFilterRow(saved, Describe(saved, lists), this));
        }
    }

    private string Describe(SavedFilter saved, IReadOnlyList<StationList> lists)
    {
        var words = saved.Filter.IsEmpty
            ? text["SavedFilters.Everything"]
            : SavedFilterText.Format(saved.Filter, null, lists);
        return saved.View is null ? words : words + " · " + text["SavedFilters.MovesMap"];
    }

    internal void StartRename(SavedFilterRow row)
    {
        Renaming = row;
        NewName = row.Filter.Name;
    }

    /// <summary>Saves the new name (behavior 8).</summary>
    [RelayCommand]
    private async Task RenameAsync()
    {
        if (
            Renaming is { } row
            && await ChangeFiltersAsync(f =>
                [.. f.Select(s => s.Id == row.Filter.Id ? s with { Name = NewName.Trim() } : s)]
            )
        )
        {
            Renaming = null;
        }
    }

    /// <summary>Leaves the name as it was.</summary>
    [RelayCommand]
    private void CancelRename() => Renaming = null;

    internal async Task DeleteFilterAsync(SavedFilterRow row)
    {
        var index = SavedFilters.IndexOf(row);
        if (await ChangeFiltersAsync(f => [.. f.Where(s => s.Id != row.Filter.Id)]))
        {
            _deleted = (row.Filter, index);
            DeletedText = text.Format(
                "SavedFilters.Deleted",
                new Dictionary<string, object?> { ["name"] = row.Filter.Name }
            );
        }
    }

    /// <summary>Puts the saved filter just deleted back where it was (behavior 8).</summary>
    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        if (_deleted is not var (filter, index))
        {
            return;
        }
        if (await ChangeFiltersAsync(f => [.. f.Take(index), filter, .. f.Skip(index)]))
        {
            _deleted = null;
            DeletedText = null;
        }
    }

    internal Task MoveFilterAsync(SavedFilterRow row, int by) =>
        ChangeFiltersAsync(f =>
        {
            var list = f.ToList();
            var at = list.FindIndex(s => s.Id == row.Filter.Id);
            var to = Math.Clamp(at + by, 0, list.Count - 1);
            if (at < 0 || to == at)
            {
                return f;
            }
            list.RemoveAt(at);
            list.Insert(to, row.Filter);
            return list;
        });

    internal async Task CopyFilterAsync(SavedFilterRow row)
    {
        if (CopyToClipboard is { } copy)
        {
            var lists = _settings.GetActiveProfile()?.StationListSet ?? [];
            await copy(SavedFilterText.Format(row.Filter.Filter, row.Filter.View, lists));
            Message = text["SavedFilters.Copied"];
        }
    }

    /// <summary>Puts the clipboard's text in the Paste filter… box (behavior 9).</summary>
    [RelayCommand]
    private async Task PasteFilterTextAsync()
    {
        if (PasteFromClipboard is { } paste && await paste() is { } pasted)
        {
            PastedText = pasted.Trim();
        }
    }

    private bool CanAddPasted => PastedName.Trim().Length > 0 && PastedText.Trim().Length > 0;

    /// <summary>
    /// Adds a saved filter from its text (behavior 9), pointing out what couldn't be read and the lists this profile
    /// doesn't have; it's added without them.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddPasted))]
    private async Task AddPastedFilterAsync()
    {
        var lists = _settings.GetActiveProfile()?.StationListSet ?? [];
        var read = SavedFilterText.Parse(PastedText, lists);
        var added = SavedFilter.Create(PastedName, read.Filter, read.View);
        if (!await ChangeFiltersAsync(f => [.. f, added]))
        {
            return;
        }
        var notes = new List<string>();
        if (read.MissingLists.Count > 0)
        {
            notes.Add(
                text.Format(
                    "SavedFilters.MissingLists",
                    new Dictionary<string, object?> { ["lists"] = string.Join(", ", read.MissingLists) }
                )
            );
        }
        if (read.Errors.Count > 0)
        {
            notes.Add(
                text.Format(
                    "SavedFilters.Unread",
                    new Dictionary<string, object?> { ["parts"] = string.Join("; ", read.Errors) }
                )
            );
        }
        Message = notes.Count > 0 ? string.Join(" ", notes) : text["SavedFilters.Added"];
        PastedName = PastedText = string.Empty;
    }

    /// <summary>Saves the active profile's filters as <paramref name="change"/> makes them; whether it took.</summary>
    private async Task<bool> ChangeFiltersAsync(Func<IReadOnlyList<SavedFilter>, IReadOnlyList<SavedFilter>> change)
    {
        if (_settings.GetActiveProfile() is not { } profile)
        {
            return false;
        }
        return await SaveProfileAsync(profile.Id, p => p with { SavedFilters = change(p.SavedFilterSet) });
    }
}

/// <summary>A saved filter in Settings › Lists and filters (F1.42 behavior 8).</summary>
/// <param name="Filter">The saved filter.</param>
/// <param name="Summary">Its conditions in words, and whether it moves the map.</param>
public sealed partial class SavedFilterRow(SavedFilter filter, string summary, StationListsSectionViewModel owner)
{
    /// <summary>The saved filter.</summary>
    public SavedFilter Filter { get; } = filter;

    /// <summary>Its name.</summary>
    public string Name => Filter.Name;

    /// <summary>Its conditions in words.</summary>
    public string Summary { get; } = summary;

    [RelayCommand]
    private void Rename() => owner.StartRename(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteFilterAsync(this);

    [RelayCommand]
    private Task MoveUpAsync() => owner.MoveFilterAsync(this, -1);

    [RelayCommand]
    private Task MoveDownAsync() => owner.MoveFilterAsync(this, 1);

    [RelayCommand]
    private Task CopyAsync() => owner.CopyFilterAsync(this);
}
