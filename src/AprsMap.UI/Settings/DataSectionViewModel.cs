// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Data (docs/features/F0.10-start-over.md): Reset the app, after a confirmation that says what goes. On a
/// gateway the station's profiles are shared by every viewer, so deleting them is a separate choice, offered only where
/// this browser may change settings.
/// </summary>
public sealed partial class DataSectionViewModel : ObservableObject
{
    private readonly Localizer _text;
    private readonly Func<bool, Task> _reset;

    /// <param name="text">For the section's wording.</param>
    /// <param name="sharedStation">The station's settings are on a gateway, shared with other viewers (the browser).</param>
    /// <param name="canResetStation">This viewer may change the station's settings.</param>
    /// <param name="reset">Does the reset; its argument says whether to delete the station's settings too.</param>
    public DataSectionViewModel(Localizer text, bool sharedStation, bool canResetStation, Func<bool, Task> reset)
    {
        _text = text;
        _reset = reset;
        SharedStation = sharedStation;
        ShowStationChoice = sharedStation && canResetStation;
        Retranslate();
    }

    /// <summary>Clear (F1.19), where this viewer may change the station; null otherwise.</summary>
    public ClearDataViewModel? Clear { get; init; }

    /// <summary>
    /// Capture unexpected packets (docs/features/F1.27-packet-capture.md, behavior 4), where this viewer may change the
    /// station; null otherwise.
    /// </summary>
    public Capture.CaptureSettingsViewModel? Capture { get; init; }

    /// <summary>
    /// History (docs/features/F1.41-persistent-history.md, behavior 2), where this viewer may change the station; null
    /// otherwise.
    /// </summary>
    public HistorySettingsViewModel? History { get; init; }

    /// <summary>The device list (docs/features/F4.16-device-list.md): for everyone; its checks for whoever may change them.</summary>
    public DeviceListViewModel? DeviceList { get; init; }

    /// <summary>
    /// Updates (docs/features/F4.15-updates.md): Check for updates, the channel and a skipped version, where updates are
    /// this viewer's; null otherwise, or where checking is off in the engine's configuration.
    /// </summary>
    public Updates.UpdateSettingsViewModel? UpdateChecks { get; init; }

    /// <summary>Whether the station's settings live on a gateway, shared with other viewers.</summary>
    public bool SharedStation { get; }

    /// <summary>Whether the "also the gateway's station profiles" tick is offered.</summary>
    public bool ShowStationChoice { get; }

    /// <summary>What Reset the app deletes, for this platform.</summary>
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>Whether the confirmation is showing.</summary>
    [ObservableProperty]
    public partial bool IsConfirming { get; set; }

    /// <summary>On a gateway: delete the station's profiles too (off by default).</summary>
    [ObservableProperty]
    public partial bool IncludeStation { get; set; }

    /// <summary>Why the reset failed, or null.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>Shows the confirmation.</summary>
    [RelayCommand]
    private void Start()
    {
        ErrorText = null;
        IsConfirming = true;
    }

    /// <summary>Hides the confirmation without deleting anything.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsConfirming = false;
        IncludeStation = false;
    }

    /// <summary>Deletes everything the confirmation listed.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        try
        {
            await _reset(!SharedStation || IncludeStation);
            IsConfirming = false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            ErrorText = _text.Describe(ex);
        }
    }

    internal void Retranslate()
    {
        Description = _text[SharedStation ? "Settings.Data.Description.Browser" : "Settings.Data.Description.Desktop"];
        Clear?.Retranslate();
        History?.Retranslate();
    }
}
