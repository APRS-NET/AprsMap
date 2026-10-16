// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>My location (docs/features/F1.13-my-location.md): set from the map, with Undo.</summary>
public sealed partial class MainViewModel
{
    /// <summary>How long Undo is offered after setting my location from the map (behavior 2).</summary>
    public static readonly TimeSpan LocationUndoWindow = TimeSpan.FromSeconds(10);

    private AppSettings? _beforeLocation;
    private ITimer? _locationUndoTimer;

    /// <summary>
    /// Whether the map offers Set my location here: there's an active profile and the settings can be changed (on a
    /// read-only gateway they can't; behavior 6).
    /// </summary>
    [ObservableProperty]
    public partial bool CanSetMyLocation { get; private set; }

    /// <summary>What the map says after setting my location, with Undo beside it; null when there's nothing to undo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoMyLocation))]
    public partial string? MyLocationNotice { get; private set; }

    /// <summary>Whether Undo is offered.</summary>
    public bool CanUndoMyLocation => MyLocationNotice is not null;

    /// <summary>
    /// Saves <paramref name="latitude"/>, <paramref name="longitude"/> as the active profile's location (behavior 2), and
    /// offers Undo for <see cref="LocationUndoWindow"/>. A refusal shows in the error line.
    /// </summary>
    internal async Task SetMyLocationAsync(double latitude, double longitude)
    {
        if (!CanSetMyLocation || _settings is not { } settings || settings.GetActiveProfile() is not { } profile)
        {
            return;
        }
        var moved = profile with { Latitude = Math.Round(latitude, 5), Longitude = Math.Round(longitude, 5) };
        if (await SaveLocationAsync(settings, moved))
        {
            _beforeLocation = settings;
            MyLocationNotice = _text["Map.LocationSet"];
            _locationUndoTimer?.Dispose();
            var context = SynchronizationContext.Current;
            _locationUndoTimer = time.CreateTimer(
                _ =>
                {
                    if (context is null)
                    {
                        StopLocationUndo();
                    }
                    else
                    {
                        context.Post(_ => StopLocationUndo(), null);
                    }
                },
                null,
                LocationUndoWindow,
                Timeout.InfiniteTimeSpan
            );
        }
    }

    /// <summary>Puts back the location the profile had before Set my location here.</summary>
    [RelayCommand]
    private async Task UndoMyLocationAsync()
    {
        var before = _beforeLocation;
        StopLocationUndo();
        if (before?.GetActiveProfile() is { } profile && _settings is { } settings)
        {
            await SaveLocationAsync(settings, profile);
        }
    }

    /// <summary>Replaces the profile with <paramref name="profile"/>'s id by it, keeping every other setting as it is now.</summary>
    private async Task<bool> SaveLocationAsync(AppSettings settings, StationProfile profile)
    {
        var changed = settings with { Profiles = [.. settings.Profiles.Select(p => p.Id == profile.Id ? profile : p)] };
        try
        {
            await engine.SaveSettingsAsync(changed);
            Show(changed);
            ShowError(null);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
            return false;
        }
    }

    private void StopLocationUndo()
    {
        _locationUndoTimer?.Dispose();
        _locationUndoTimer = null;
        _beforeLocation = null;
        MyLocationNotice = null;
    }
}
