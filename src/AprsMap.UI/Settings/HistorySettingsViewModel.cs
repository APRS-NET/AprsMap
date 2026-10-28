// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Data › History (docs/features/F1.41-persistent-history.md, behaviors 2, 3, 5, 13): whether packets are
/// kept and for how long, the size limit, and what's kept now, with what stands in its way. Turning it off deletes
/// what's kept, so that Save asks once more first. Where the gateway's configuration sets the limits, they're shown,
/// not changed here.
/// </summary>
public sealed partial class HistorySettingsViewModel(
    IEngineService engine,
    Localizer text,
    TimeZoneInfo zone,
    Action<AppSettings> saved
) : ObservableObject
{
    private AppSettings _settings = AppSettings.Empty;
    private HistoryStatus _status = HistoryStatus.NotAvailable;
    private bool _confirming;

    /// <summary>Whether packets are kept at all.</summary>
    [ObservableProperty]
    public partial bool On { get; set; }

    /// <summary>How many days, as typed.</summary>
    [ObservableProperty]
    public partial string Days { get; set; } = string.Empty;

    /// <summary>The size limit in MB, as typed.</summary>
    [ObservableProperty]
    public partial string MaxMegabytes { get; set; } = string.Empty;

    /// <summary>Whether the limits can be changed here: not where the gateway's configuration sets them.</summary>
    [ObservableProperty]
    public partial bool CanChange { get; private set; } = true;

    /// <summary>"7 days kept, 412 MB, since 21/10/2026 14:02", or that nothing is kept yet.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>What stands in the history's way, in words; null for nothing.</summary>
    [ObservableProperty]
    public partial string? Warning { get; private set; }

    /// <summary>"Saved", once saved; null otherwise.</summary>
    [ObservableProperty]
    public partial string? SavedText { get; private set; }

    /// <summary>Why it wasn't saved, or the question before deleting; null for nothing.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Shows <paramref name="settings"/>' history settings, then reads what's kept.</summary>
    internal async Task ShowAsync(AppSettings settings)
    {
        _settings = settings;
        Fill(settings.EffectiveHistory);
        SavedText = Error = null;
        await RefreshAsync();
    }

    private void Fill(HistorySettings history)
    {
        On = history.On;
        Days = (history.On ? history.Days : HistorySettings.DefaultDays).ToString(CultureInfo.InvariantCulture);
        MaxMegabytes = history.MaxMegabytes.ToString(CultureInfo.InvariantCulture);
        _confirming = false;
    }

    /// <summary>Reads what's kept now.</summary>
    internal async Task RefreshAsync()
    {
        try
        {
            _status = await engine.GetHistoryStatusAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            _status = HistoryStatus.NotAvailable;
        }
        if (_status.Configured)
        {
            Fill(_status.Settings); // the configuration's, which win
        }
        CanChange = !_status.Configured;
        ShowStatus();
    }

    private void ShowStatus()
    {
        var status = _status;
        var args = new Dictionary<string, object?>
        {
            ["days"] = status.DaysKept,
            ["megabytes"] = (long)Math.Ceiling(status.Bytes / (1024.0 * 1024.0)),
            ["since"] = status.KeptSince is { } since
                ? TimeZoneInfo.ConvertTime(since, zone).ToString("g", text.Culture)
                : string.Empty,
            ["fit"] = status.DaysFit ?? 0,
            ["wanted"] = status.Settings.Days,
        };
        StatusText =
            !status.Available ? text["History.NotAvailable"]
            : status.Configured && !status.Settings.On ? text["History.OffConfigured"]
            : !status.Settings.On ? text["History.Off"]
            : status.KeptSince is null ? text["History.Empty"]
            : text.Format("History.Kept", args);
        if (status.Configured && status.Settings.On)
        {
            StatusText += " " + text["History.Configured"];
        }
        var warning = status.Problem switch
        {
            HistoryProblem.SizeLimitShortens => text.Format("History.SizeLimitShortens", args),
            HistoryProblem.TodayFull => text["History.TodayFull"],
            HistoryProblem.InUseElsewhere => text["History.InUseElsewhere"],
            HistoryProblem.CantWrite => text["History.CantWrite"],
            _ => null,
        };
        if (status.SetAside is { Count: > 0 } setAside)
        {
            var days = string.Join(", ", setAside.Select(d => d.ToString("d", text.Culture)));
            var note = text.Format("History.SetAside", new Dictionary<string, object?> { ["days"] = days });
            warning = warning is null ? note : warning + " " + note;
        }
        Warning = warning;
    }

    partial void OnOnChanged(bool value) => _confirming = false;

    /// <summary>
    /// Saves the length and size limit. Turning history off deletes what's kept, so the first press only asks (behavior
    /// 2); a limit out of range is refused here.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = SavedText = null;
        if (
            !int.TryParse(Days.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
            || !int.TryParse(MaxMegabytes.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var megabytes)
            || new HistorySettings(On ? days : 1, megabytes) is not { IsValid: true }
            || (On && days < 1)
        )
        {
            Error = text["History.LimitsInvalid"];
            return;
        }
        if (!On && _settings.EffectiveHistory.On && _status.KeptSince is not null && !_confirming)
        {
            _confirming = true;
            Error = text["History.ConfirmOff"];
            return;
        }
        _confirming = false;
        var settings = _settings with { History = new HistorySettings(On ? days : 0, megabytes) };
        try
        {
            await engine.SaveSettingsAsync(settings);
            _settings = settings;
            saved(settings);
            SavedText = text["History.Saved"];
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = text.Describe(ex);
        }
    }

    /// <summary>The texts again, in the new language.</summary>
    internal void Retranslate() => ShowStatus();
}
