// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>A schedule choice: Off, Daily, Weekly.</summary>
/// <param name="Value">The schedule.</param>
/// <param name="Label">How it reads.</param>
public sealed record DeviceListScheduleOption(DeviceListSchedule Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// Settings › Data › Device list (docs/features/F4.16-device-list.md, behaviors 4, 5, 9, 10, 12): the list in use and
/// the last check for everyone; Update now, the schedule and, on the desktop, the address for whoever may change
/// the station.
/// </summary>
public sealed partial class DeviceListViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly TimeProvider _time;
    private readonly Action<AppSettings> _saved;
    private AppSettings _settings = AppSettings.Empty;
    private bool _showing;

    internal DeviceListViewModel(
        IEngineService engine,
        Localizer text,
        TimeProvider time,
        bool canChange,
        Action<AppSettings> saved
    )
    {
        _engine = engine;
        _text = text;
        _time = time;
        _saved = saved;
        CanChange = canChange;
        ScheduleOptions =
        [
            new(DeviceListSchedule.Weekly, text["DeviceList.Schedule.Weekly"]),
            new(DeviceListSchedule.Daily, text["DeviceList.Schedule.Daily"]),
            new(DeviceListSchedule.Off, text["DeviceList.Schedule.Off"]),
        ];
        Schedule = ScheduleOptions[0];
    }

    /// <summary>Whether this viewer may check now and change the schedule (an operator, or the desktop).</summary>
    public bool CanChange { get; }

    /// <summary>"Built in, from 18 October 2026" or "Downloaded, from 25 October 2026".</summary>
    [ObservableProperty]
    public partial string? InUseText { get; private set; }

    /// <summary>"Checked today: up to date", "Checked 21 October: couldn't reach the server", or "Not checked yet".</summary>
    [ObservableProperty]
    public partial string? CheckedText { get; private set; }

    /// <summary>Whether the last check failed, for its colour.</summary>
    [ObservableProperty]
    public partial bool CheckFailed { get; private set; }

    /// <summary>Whether Update now and the schedule show: this viewer may change them and an address is set.</summary>
    [ObservableProperty]
    public partial bool ShowsChecks { get; private set; }

    /// <summary>Whether the address shows, under Advanced: the desktop.</summary>
    [ObservableProperty]
    public partial bool ShowsUrl { get; private set; }

    /// <summary>Whether a check is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    public partial bool Checking { get; private set; }

    /// <summary>Off, Daily, Weekly.</summary>
    public IReadOnlyList<DeviceListScheduleOption> ScheduleOptions { get; }

    /// <summary>The schedule; saved as it's chosen.</summary>
    [ObservableProperty]
    public partial DeviceListScheduleOption Schedule { get; set; }

    /// <summary>The address, as typed; empty never downloads.</summary>
    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    /// <summary>Why the address can't be saved, or why saving failed; null otherwise.</summary>
    [ObservableProperty]
    public partial string? UrlError { get; private set; }

    /// <summary>Shows the settings this viewer has.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        _showing = true;
        try
        {
            var list = settings.EffectiveDeviceList;
            Schedule = ScheduleOptions.First(o => o.Value == list.Schedule);
        }
        finally
        {
            _showing = false;
        }
    }

    /// <summary>Shows the list in use and its last check (behavior 9).</summary>
    internal void Show(DeviceListStatus? status)
    {
        if (status is null)
        {
            return;
        }
        var culture = _text.Culture;
        var zone = _time.LocalTimeZone;
        string Date(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).ToString("d MMMM yyyy", culture);
        InUseText = _text.Format(
            status.Origin == DeviceListOrigin.Downloaded ? "DeviceList.Downloaded" : "DeviceList.BuiltIn",
            new Dictionary<string, object?>
            {
                ["date"] = status.GeneratedAt is { } generated ? Date(generated) : _text["DeviceList.UnknownDate"],
            }
        );
        CheckFailed = status.LastError is not null;
        if (status.Checking)
        {
            CheckedText = _text["DeviceList.Checking"];
        }
        else if (status.LastChecked is { } checkedAt)
        {
            var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).Date;
            var day = TimeZoneInfo.ConvertTime(checkedAt, zone).Date;
            var when = day == today ? _text["DeviceList.Today"] : Date(checkedAt);
            CheckedText = _text.Format(
                "DeviceList.Checked",
                new Dictionary<string, object?>
                {
                    ["when"] = when,
                    ["result"] = status.LastError is { } error ? _text.Format(error) : _text["DeviceList.UpToDate"],
                }
            );
        }
        else
        {
            CheckedText = status.CanDownload ? _text["DeviceList.NotChecked"] : _text["DeviceList.NoDownload"];
        }
        ShowsChecks = CanChange && status.CanDownload;
        ShowsUrl = CanChange && status.UrlIsSetting;
        if (ShowsUrl && UrlError is null)
        {
            Url = _settings.EffectiveDeviceList.Url ?? status.Url ?? string.Empty;
        }
        Checking = status.Checking;
    }

    partial void OnScheduleChanged(DeviceListScheduleOption value)
    {
        if (!_showing && CanChange && value.Value != _settings.EffectiveDeviceList.Schedule)
        {
            _ = SaveAsync(
                _settings with
                {
                    DeviceList = _settings.EffectiveDeviceList with { Schedule = value.Value },
                }
            );
        }
    }

    /// <summary>Checks now, whatever the schedule (behavior 5).</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private async Task UpdateNowAsync()
    {
        Checking = true;
        CheckedText = _text["DeviceList.Checking"];
        try
        {
            Show(await _engine.UpdateDeviceListAsync());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            CheckedText = _text.Describe(ex);
            CheckFailed = true;
        }
        finally
        {
            Checking = false;
        }
    }

    private bool CanUpdateNow() => !Checking;

    /// <summary>Saves the address: empty never downloads (behavior 10).</summary>
    [RelayCommand]
    private async Task SaveUrlAsync()
    {
        var url = Url.Trim();
        UrlError =
            url.Length == 0 || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
                ? null
                : _text["DeviceList.UrlInvalid"];
        if (UrlError is null)
        {
            await SaveAsync(_settings with { DeviceList = _settings.EffectiveDeviceList with { Url = url } });
        }
    }

    /// <summary>Back to the configured address, the APRS Foundation's by default.</summary>
    [RelayCommand]
    private async Task ResetUrlAsync()
    {
        UrlError = null;
        await SaveAsync(_settings with { DeviceList = _settings.EffectiveDeviceList with { Url = null } });
        Show(await _engine.GetDeviceListAsync());
    }

    private async Task SaveAsync(AppSettings settings)
    {
        try
        {
            await _engine.SaveSettingsAsync(settings);
            _settings = settings;
            _saved(settings);
            Show(await _engine.GetDeviceListAsync());
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            UrlError = _text.Describe(ex);
        }
    }
}
