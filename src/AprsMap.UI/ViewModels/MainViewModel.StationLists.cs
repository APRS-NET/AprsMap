// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>Station lists from Details and the map (docs/features/F1.33-station-lists.md, behavior 5).</summary>
public sealed partial class MainViewModel
{
    /// <summary>Whether this viewer may change station lists: whoever may change settings.</summary>
    public bool CanEditStationLists => !IsViewerOnly && _settings is { ReadOnly: false };

    /// <summary>The active profile's lists.</summary>
    public IReadOnlyList<StationList> StationLists => _settings?.GetActiveProfile()?.StationListSet ?? [];

    /// <summary>A choice a list for Add to list in Details, adding the station shown.</summary>
    public ObservableCollection<ListAction> AddToListChoices { get; } = [];

    /// <summary>Whether Details offers Add to list.</summary>
    public bool CanAddToList => CanEditStationLists && AddToListChoices.Count > 0;

    /// <summary>Offers the active profile's lists to add to.</summary>
    private void ShowListChoices()
    {
        AddToListChoices.Clear();
        if (CanEditStationLists)
        {
            foreach (var list in StationLists)
            {
                var id = list.Id;
                AddToListChoices.Add(
                    new ListAction(
                        list.Name,
                        new AsyncRelayCommand(() =>
                            Details.Name is { } name ? AddToListAsync(id, name) : Task.CompletedTask
                        )
                    )
                );
            }
        }
        OnPropertyChanged(nameof(CanAddToList));
        OnPropertyChanged(nameof(CanEditStationLists));
    }

    /// <summary>
    /// Adds <paramref name="name"/> to the list <paramref name="listId"/> of the active profile, on a line of its own;
    /// nothing when it's in the list already.
    /// </summary>
    internal async Task AddToListAsync(string listId, string name)
    {
        if (_settings is not { } settings || settings.GetActiveProfile() is not { } profile)
        {
            return;
        }
        if (
            profile.StationListSet.FirstOrDefault(l => l.Id == listId) is not { } list
            || list.Entries.Any(e => e.Matches(name))
        )
        {
            return;
        }
        var changed = settings with
        {
            Profiles =
            [
                .. settings.Profiles.Select(p =>
                    p.Id != profile.Id
                        ? p
                        : p with
                        {
                            StationLists =
                            [
                                .. p.StationListSet.Select(l =>
                                    l.Id == listId
                                        ? l with
                                        {
                                            Text = StationListText.Append(l.Text, name.ToUpperInvariant()),
                                        }
                                        : l
                                ),
                            ],
                        }
                ),
            ],
        };
        try
        {
            await engine.SaveSettingsAsync(changed);
            Show(changed);
            ShowError(null);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }
}

/// <summary>A list to add a station to.</summary>
public sealed record ListAction(string Name, IRelayCommand Add);
