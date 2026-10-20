// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.ViewModels;

/// <summary>Objects from the map (docs/features/F2.7-objects-and-items.md, behaviors 1, 4).</summary>
public sealed partial class MainViewModel
{
    /// <summary>The active profile's live objects, for the map's Move an object here.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<OwnObject> LiveObjects { get; private set; } = [];

    /// <summary>
    /// Whether this viewer may place objects: they transmit, so the desktop user, or a gateway's signed-in operator,
    /// with settings that can change.
    /// </summary>
    public bool CanPlaceObjects =>
        !IsViewerOnly
        && (!SharedStation || IsOperator)
        && _settings is { ReadOnly: false } settings
        && settings.GetActiveProfile() is not null;

    /// <summary>Place an object here: Settings › Objects with a new object at that spot (behavior 1).</summary>
    internal async Task PlaceObjectAtAsync(double latitude, double longitude)
    {
        await OpenSettingsSectionAsync("objects");
        Settings?.Objects?.StartAt(latitude, longitude);
    }

    /// <summary>Move an object here (behavior 4): it's sent at its new place at once.</summary>
    internal async Task MoveObjectAsync(string id, double latitude, double longitude)
    {
        if (LiveObjects.FirstOrDefault(o => o.Id == id) is not { } placed)
        {
            return;
        }
        try
        {
            await engine.PlaceObjectAsync(placed with { Latitude = latitude, Longitude = longitude });
            Show(await engine.GetSettingsAsync());
            ShowError(null);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }
}
