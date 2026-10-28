// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.UI.ViewModels;

/// <summary>Saved filters (docs/features/F1.42-saved-filters.md): the View filter's, saved with the active profile.</summary>
public sealed partial class MainViewModel
{
    // Tile maps' resolution at zoom 0, in metres a pixel at the equator: zoom z is this over 2^z.
    private const double ResolutionAtZoom0 = 156543.03392804097;

    /// <summary>Connects the View filter to the active profile's saved filters and to the map (behaviors 1, 2, 5).</summary>
    private void WireSavedFilters()
    {
        ViewFilter.SaveFilters = SaveFiltersAsync;
        ViewFilter.CurrentView = () =>
            Map?.SavedView is { } view
                ? new SavedMapView(view.Latitude, view.Longitude, Math.Log2(ResolutionAtZoom0 / view.Resolution))
                : null;
    }

    /// <summary>The resolution a saved map view's zoom stands for, for the map to go to.</summary>
    internal static double ResolutionOf(SavedMapView view) => ResolutionAtZoom0 / Math.Pow(2, view.Zoom);

    /// <summary>Saves the active profile with <paramref name="filters"/>; why it was refused, or null.</summary>
    private async Task<string?> SaveFiltersAsync(IReadOnlyList<SavedFilter> filters)
    {
        if (_settings?.GetActiveProfile() is not { } active)
        {
            return _text["Filter.SaveNoProfile"];
        }
        var changed = _settings with
        {
            Profiles = [.. _settings.Profiles.Select(p => p.Id == active.Id ? p with { SavedFilters = filters } : p)],
        };
        try
        {
            await engine.SaveSettingsAsync(changed);
            Show(changed);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            return _text.Describe(ex);
        }
    }
}
