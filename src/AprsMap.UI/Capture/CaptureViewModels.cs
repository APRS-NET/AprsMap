// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Capture;

/// <summary>
/// Settings › Data › Capture unexpected packets (docs/features/F1.27-packet-capture.md, behaviors 4, 5): the switch, off
/// by default, the size and age limits, and whether user-defined packets are recorded too.
/// </summary>
public sealed partial class CaptureSettingsViewModel(IEngineService engine, Localizer text, Action<AppSettings> saved)
    : ObservableObject
{
    private AppSettings _settings = AppSettings.Empty;

    /// <summary>Whether capture is on.</summary>
    [ObservableProperty]
    public partial bool On { get; set; }

    /// <summary>The size limit in MB, as typed.</summary>
    [ObservableProperty]
    public partial string MaxMegabytes { get; set; } = string.Empty;

    /// <summary>The age limit in days, as typed.</summary>
    [ObservableProperty]
    public partial string Days { get; set; } = string.Empty;

    /// <summary>Whether user-defined packets are recorded too (decision 7).</summary>
    [ObservableProperty]
    public partial bool UserDefined { get; set; }

    /// <summary>"Saved", once saved; null otherwise.</summary>
    [ObservableProperty]
    public partial string? SavedText { get; private set; }

    /// <summary>Why the engine refused, in words; null when it didn't.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Shows <paramref name="settings"/>' capture settings.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        var capture = settings.EffectiveCapture;
        On = capture.On;
        MaxMegabytes = capture.MaxMegabytes.ToString(CultureInfo.InvariantCulture);
        Days = capture.Days.ToString(CultureInfo.InvariantCulture);
        UserDefined = capture.UserDefined;
        SavedText = null;
    }

    /// <summary>Saves the capture settings; a limit that isn't a whole number above 0 is refused here.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = SavedText = null;
        if (!Positive(MaxMegabytes, out var megabytes) || !Positive(Days, out var days))
        {
            Error = text["Capture.LimitsInvalid"];
            return;
        }
        var settings = _settings with { Capture = new CaptureSettings(On, megabytes, days, UserDefined) };
        try
        {
            await engine.SaveSettingsAsync(settings);
            _settings = settings;
            saved(settings);
            SavedText = text["Capture.Saved"];
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = text.Describe(ex);
        }

        static bool Positive(string value, out int number) =>
            int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0;
    }
}

/// <summary>
/// The Captured packets panel (docs/features/F1.27-packet-capture.md, behaviors 7–10): the packets capture recorded,
/// newest first, filtered by day and reason; Export for analysis, Report on GitHub, and Clear captured.
/// </summary>
public sealed partial class CapturedPacketsViewModel : ObservableObject
{
    /// <summary>Where Report on GitHub opens a new issue (behavior 9).</summary>
    public const string NewIssueUrl = "https://github.com/APRS-NET/APRS.NET/issues/new";

    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly TimeProvider _time;
    private readonly Action<string> _openStation;
    private bool _filling;

    internal CapturedPacketsViewModel(
        IEngineService engine,
        Localizer text,
        TimeProvider time,
        Action<string> openStation
    )
    {
        _engine = engine;
        _text = text;
        _time = time;
        _openStation = openStation;
    }

    /// <summary>The packets shown, newest first.</summary>
    public ObservableCollection<CapturedRow> Rows { get; } = [];

    /// <summary>All days, then each day kept with its count.</summary>
    public ObservableCollection<CaptureChoice> Days { get; } = [];

    /// <summary>Any reason, then each one seen.</summary>
    public ObservableCollection<CaptureChoice> Reasons { get; } = [];

    /// <summary>The day picked.</summary>
    [ObservableProperty]
    public partial CaptureChoice? SelectedDay { get; set; }

    /// <summary>The reason picked.</summary>
    [ObservableProperty]
    public partial CaptureChoice? SelectedReason { get; set; }

    /// <summary>Whether Export anonymizes (the default, behavior 8.1).</summary>
    [ObservableProperty]
    public partial bool Anonymize { get; set; } = true;

    /// <summary>"No packets captured yet…", or how many repeats were counted today; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    /// <summary>Why the panel can't show anything (a gateway viewer, behavior 11), or null.</summary>
    [ObservableProperty]
    public partial string? Unavailable { get; private set; }

    /// <summary>What the last action did or why it failed; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    /// <summary>Whether Clear captured waits for its confirmation.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingClear { get; private set; }

    partial void OnSelectedDayChanged(CaptureChoice? value)
    {
        if (!_filling)
        {
            _ = LoadAsync();
        }
    }

    partial void OnSelectedReasonChanged(CaptureChoice? value)
    {
        if (!_filling)
        {
            _ = LoadAsync();
        }
    }

    private DateOnly? Day =>
        SelectedDay?.Value is { } day ? DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>Lists the packets for the filters picked (behavior 7).</summary>
    [RelayCommand]
    internal async Task LoadAsync()
    {
        try
        {
            var view = await _engine.GetCaptureAsync(new CaptureQuery(Day, SelectedReason?.Value));
            Unavailable = null;
            Show(view);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Unavailable = _text.Describe(ex);
        }
    }

    private void Show(CaptureView view)
    {
        _filling = true;
        try
        {
            var day = SelectedDay?.Value;
            var reason = SelectedReason?.Value;
            Days.Clear();
            Days.Add(new CaptureChoice(null, _text["Capture.AllDays"]));
            foreach (var kept in view.Days)
            {
                var value = kept.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Days.Add(
                    new CaptureChoice(
                        value,
                        _text.Format(
                            "Capture.Day",
                            new Dictionary<string, object?>
                            {
                                ["day"] = kept.Day.ToString("d", _text.Culture),
                                ["count"] = kept.Count,
                            }
                        )
                    )
                );
            }
            Reasons.Clear();
            Reasons.Add(new CaptureChoice(null, _text["Capture.AnyReason"]));
            foreach (var code in view.Reasons)
            {
                Reasons.Add(new CaptureChoice(code, _text.Format(new EngineError(code, code))));
            }
            SelectedDay = Days.FirstOrDefault(d => d.Value == day) ?? Days[0];
            SelectedReason = Reasons.FirstOrDefault(r => r.Value == reason) ?? Reasons[0];
        }
        finally
        {
            _filling = false;
        }
        Rows.Clear();
        foreach (var packet in view.Packets)
        {
            Rows.Add(new CapturedRow(packet, _text, _time.LocalTimeZone, _openStation));
        }
        Summary =
            view.Packets.Count == 0 && view.Days.Count == 0 ? _text["Capture.Empty"]
            : view.RepeatsLeftOut > 0
                ? _text.Format("Capture.Repeats", new Dictionary<string, object?> { ["count"] = view.RepeatsLeftOut })
            : null;
    }

    /// <summary>The export for the filters picked, for the view to save (behavior 8); null when it failed.</summary>
    internal async Task<CaptureExportResult?> ExportAsync()
    {
        Notice = null;
        try
        {
            return await _engine.ExportCaptureAsync(new CaptureExport(Anonymize, Day, SelectedReason?.Value));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Notice = _text.Describe(ex);
            return null;
        }
    }

    /// <summary>Says what was saved, and what to do next (behavior 8).</summary>
    internal void Exported(CaptureExportResult result) =>
        Notice = _text.Format(
            result.Clean || !Anonymize ? "Capture.Exported" : "Capture.ExportedCheck",
            new Dictionary<string, object?> { ["count"] = result.Lines, ["file"] = result.FileName }
        );

    /// <summary>
    /// The new-issue address with the note filled in (behavior 9): the browser opens it and the viewer attaches the
    /// export and submits. Nothing is uploaded by the app.
    /// </summary>
    internal async Task<Uri?> ReportUriAsync()
    {
        var result = await ExportAsync();
        if (result is null)
        {
            return null;
        }
        var title = Uri.EscapeDataString(_text["Capture.IssueTitle"]);
        var body = Uri.EscapeDataString(result.Note + Environment.NewLine + _text["Capture.IssueAttach"]);
        return new Uri($"{NewIssueUrl}?title={title}&body={body}");
    }

    /// <summary>Asks before deleting the captured packets (behavior 10).</summary>
    [RelayCommand]
    private void Clear() => IsConfirmingClear = true;

    /// <summary>Keeps them.</summary>
    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    /// <summary>Deletes them.</summary>
    [RelayCommand]
    private async Task ConfirmClearAsync()
    {
        IsConfirmingClear = false;
        try
        {
            await _engine.ClearCaptureAsync();
            await LoadAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Notice = _text.Describe(ex);
        }
    }
}

/// <summary>A day or reason to filter by; a null value is "all".</summary>
public sealed record CaptureChoice(string? Value, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>One captured packet in the panel (behavior 7).</summary>
public sealed partial class CapturedRow : ObservableObject
{
    private readonly Action<string> _openStation;

    internal CapturedRow(CapturedPacket packet, Localizer text, TimeZoneInfo zone, Action<string> openStation)
    {
        Packet = packet;
        _openStation = openStation;
        Time = TimeZoneInfo.ConvertTime(packet.At, zone).ToString("g", text.Culture);
        ReasonsText = string.Join(" · ", packet.Reasons.Select(text.Format));
    }

    /// <summary>The packet.</summary>
    public CapturedPacket Packet { get; }

    /// <summary>When it arrived, local time.</summary>
    public string Time { get; }

    /// <summary>The station.</summary>
    public string Station => Packet.Station;

    /// <summary>The reasons in words.</summary>
    public string ReasonsText { get; }

    /// <summary>The raw packet.</summary>
    public string Raw => Packet.Raw;

    /// <summary>Opens the station's packets in the Packets panel (behavior 7).</summary>
    [RelayCommand]
    private void OpenInPackets()
    {
        if (Station.Length > 0)
        {
            _openStation(Station);
        }
    }
}
