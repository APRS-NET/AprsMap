// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>Extra map views (docs/features/F1.16-extra-map-views.md, behavior 1).</summary>
public sealed partial class MainViewModel
{
    /// <summary>Watch in a view: the selected station, from Details or <c>W</c> on the map.</summary>
    [RelayCommand]
    private void WatchSelected()
    {
        if (SelectedStation is { } name)
        {
            Watch(name);
        }
    }

    /// <summary>
    /// Opens a view following <paramref name="name"/> beside the map, or brings its view to the front; with the most
    /// views open already, says to close one first.
    /// </summary>
    internal void Watch(string name)
    {
        if (!Shell.Watch(name))
        {
            ErrorText = _text.Format(
                "Watch.TooMany",
                new Dictionary<string, object?> { ["count"] = AprsMap.UI.Shell.ShellViewModel.MaxWatchViews }
            );
        }
    }
}
