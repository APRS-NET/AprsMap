// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Station lists (docs/features/F1.33-station-lists.md, behaviors 1–4): the active profile's lists, and an
/// editor that's a plain text box, one entry a line, with Copy list, Paste and Copy to profile. Lines that can't be
/// entries are pointed out by number as they're typed, and the list can't be saved until they're fixed.
/// </summary>
public sealed partial class StationListsSectionViewModel(
    IEngineService engine,
    Localizer text,
    Action<AppSettings> saved
) : ObservableObject
{
    private AppSettings _settings = AppSettings.Empty;

    /// <summary>Puts text on the clipboard; set by the view.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Reads text from the clipboard; set by the view.</summary>
    public Func<Task<string?>>? PasteFromClipboard { get; set; }

    /// <summary>"Station lists for Home", or null without a profile.</summary>
    [ObservableProperty]
    public partial string? Heading { get; private set; }

    /// <summary>The profile's lists.</summary>
    public ObservableCollection<StationListRow> Lists { get; } = [];

    /// <summary>The list being edited, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial StationListRow? Selected { get; set; }

    /// <summary>Whether a list is open in the editor.</summary>
    public bool IsEditing => Selected is not null;

    /// <summary>The list's name as typed.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>The list's text as typed: one entry a line.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>"12 entries", or the lines that can't be entries.</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    /// <summary>Whether every line is an entry, so the list can be saved.</summary>
    [ObservableProperty]
    public partial bool CanSave { get; private set; }

    /// <summary>The other profiles, for Copy to profile (behavior 4).</summary>
    public ObservableCollection<ListTargetProfile> OtherProfiles { get; } = [];

    /// <summary>The profile Copy to profile copies into.</summary>
    [ObservableProperty]
    public partial ListTargetProfile? CopyTarget { get; set; }

    /// <summary>What the last action did, or why it couldn't; null before one.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>Shows the active profile's lists, keeping the one open when it's still there.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        var openId = Selected?.List.Id;
        Lists.Clear();
        foreach (var list in settings.GetActiveProfile()?.StationListSet ?? [])
        {
            Lists.Add(new StationListRow(list, CountText(list.Entries.Count)));
        }
        OtherProfiles.Clear();
        foreach (var profile in settings.Profiles.Where(p => p.Id != settings.ActiveProfileId))
        {
            OtherProfiles.Add(new ListTargetProfile(profile.Id, profile.Name));
        }
        CopyTarget = OtherProfiles.FirstOrDefault();
        Heading = settings.GetActiveProfile() is { } active
            ? text.Format("StationLists.Heading", new Dictionary<string, object?> { ["profile"] = active.Name })
            : null;
        Selected = openId is null ? null : Lists.FirstOrDefault(r => r.List.Id == openId);
        ShowSavedFilters(settings.GetActiveProfile()); // F1.42 behavior 8
    }

    /// <summary>Words the section again after a language change.</summary>
    internal void Retranslate()
    {
        var selected = Selected?.List.Id;
        var name = Name;
        var typed = Text;
        Show(_settings);
        if (selected is not null && Lists.FirstOrDefault(r => r.List.Id == selected) is { } row)
        {
            Selected = row;
            Name = name; // what was being typed stays
            Text = typed;
        }
        Check();
    }

    partial void OnSelectedChanged(StationListRow? value)
    {
        Name = value?.List.Name ?? string.Empty;
        Text = value?.List.Text ?? string.Empty;
        Message = null;
    }

    partial void OnTextChanged(string value) => Check();

    partial void OnNameChanged(string value) => Check();

    /// <summary>Reads the text as typed: the entries, or each line that can't be one (behavior 3).</summary>
    private void Check()
    {
        var read = StationListText.Parse(Text);
        if (read.Errors.Count > 0)
        {
            Summary = string.Join(
                "\n",
                read.Errors.Take(5)
                    .Select(e =>
                        text.Format(
                            "StationLists.BadLine",
                            new Dictionary<string, object?> { ["line"] = e.Line, ["text"] = e.Text.Trim() }
                        )
                    )
            );
        }
        else
        {
            Summary =
                CountText(read.Entries.Count)
                + (
                    read.Entries.Count > StationList.MaxFilterEntries
                        ? " · "
                            + text.Format(
                                "StationLists.FilterPart",
                                new Dictionary<string, object?> { ["count"] = StationList.MaxFilterEntries }
                            )
                        : string.Empty
                );
        }
        CanSave =
            Selected is not null
            && read.Errors.Count == 0
            && read.Entries.Count <= StationList.MaxEntries
            && !string.IsNullOrWhiteSpace(Name);
    }

    private string CountText(int count) =>
        text.Format("StationLists.Count", new Dictionary<string, object?> { ["count"] = count });

    /// <summary>A new, empty list, open in the editor.</summary>
    [RelayCommand]
    private async Task NewAsync()
    {
        if (_settings.GetActiveProfile() is not { } profile)
        {
            return;
        }
        var name = text["StationLists.NewName"];
        for (var n = 2; profile.StationListSet.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
        {
            name = $"{text["StationLists.NewName"]} {n}";
        }
        var list = new StationList(Guid.NewGuid().ToString("N"), name, string.Empty);
        if (await SaveAsync(profile.Id, [.. profile.StationListSet, list]))
        {
            Selected = Lists.FirstOrDefault(r => r.List.Id == list.Id);
        }
    }

    /// <summary>Saves the open list's name and text.</summary>
    [RelayCommand]
    private async Task SaveListAsync()
    {
        if (Selected is not { } row || _settings.GetActiveProfile() is not { } profile)
        {
            return;
        }
        var changed = row.List with { Name = Name.Trim(), Text = Text };
        if (await SaveAsync(profile.Id, [.. profile.StationListSet.Select(l => l.Id == changed.Id ? changed : l)]))
        {
            Message = text["StationLists.Saved"];
        }
    }

    /// <summary>Deletes the open list.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row || _settings.GetActiveProfile() is not { } profile)
        {
            return;
        }
        Selected = null;
        // Out of the saved filters that showed it too (F1.42 behavior 3).
        await SaveProfileAsync(
            profile.Id,
            p =>
                p with
                {
                    StationLists = [.. p.StationListSet.Where(l => l.Id != row.List.Id)],
                    SavedFilters = p.SavedFilters is null
                        ? null
                        :
                        [
                            .. p.SavedFilterSet.Select(s =>
                                s.Filter.Lists?.Contains(row.List.Id) == true
                                    ? s with
                                    {
                                        Filter = s.Filter with
                                        {
                                            Lists = s.Filter.Lists.Where(id => id != row.List.Id).ToList()
                                                is { Count: > 0 } left
                                                ? left
                                                : null,
                                        },
                                    }
                                    : s
                            ),
                        ],
                }
        );
    }

    /// <summary>Puts the open list's text on the clipboard (behavior 3).</summary>
    [RelayCommand]
    private async Task CopyListAsync()
    {
        if (CopyToClipboard is { } copy)
        {
            await copy(Text);
            Message = text["StationLists.Copied"];
        }
    }

    /// <summary>Replaces the open list's text with the clipboard's (behavior 3); saved with Save.</summary>
    [RelayCommand]
    private async Task PasteAsync()
    {
        if (PasteFromClipboard is { } paste && await paste() is { } pasted)
        {
            Text = pasted;
        }
    }

    /// <summary>Copies the open list into the profile chosen (behavior 4), replacing a list there of the same name.</summary>
    [RelayCommand]
    private async Task CopyToProfileAsync()
    {
        if (
            Selected is not { } row
            || CopyTarget is not { } target
            || _settings.Profiles.FirstOrDefault(p => p.Id == target.Id) is not { } other
        )
        {
            return;
        }
        var copy = new StationList(Guid.NewGuid().ToString("N"), Name.Trim(), Text);
        var lists = other.StationListSet.Where(l => !l.Name.Equals(copy.Name, StringComparison.OrdinalIgnoreCase));
        if (await SaveAsync(other.Id, [.. lists, copy]))
        {
            Message = text.Format(
                "StationLists.CopiedTo",
                new Dictionary<string, object?> { ["profile"] = other.Name }
            );
        }
    }

    private Task<bool> SaveAsync(string profileId, IReadOnlyList<StationList> lists) =>
        SaveProfileAsync(profileId, p => p with { StationLists = lists });

    private async Task<bool> SaveProfileAsync(string profileId, Func<StationProfile, StationProfile> change)
    {
        var changed = _settings with
        {
            Profiles = [.. _settings.Profiles.Select(p => p.Id == profileId ? change(p) : p)],
        };
        try
        {
            await engine.SaveSettingsAsync(changed);
            _settings = changed; // what's shown follows, even before the app shows it again
            ShowSavedFilters(changed.GetActiveProfile());
            saved(changed);
            return true;
        }
        catch (EngineArgumentException ex)
        {
            Message = text.Format(ex.EngineError);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = ex.Message;
        }
        return false;
    }
}

/// <summary>A list in Settings: the list and "12 entries".</summary>
public sealed record StationListRow(StationList List, string Count)
{
    /// <summary>Its name.</summary>
    public string Name => List.Name;
}

/// <summary>A profile to copy a list into.</summary>
public sealed record ListTargetProfile(string Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}
