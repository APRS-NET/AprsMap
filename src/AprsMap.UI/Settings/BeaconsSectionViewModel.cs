// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Beacons (docs/features/F2.2-beacons.md, behaviors 1–5): the active profile's position and status beacons,
/// with Send via checkmarks over its TX-enabled sources. Saved with Save; the engine checks what's sent.
/// </summary>
public sealed partial class BeaconsSectionViewModel : ObservableObject
{
    /// <summary>The intervals offered, in minutes (10 minutes to 24 hours, behavior 1.1).</summary>
    public static IReadOnlyList<int> IntervalSteps { get; } = [10, 15, 20, 30, 45, 60, 90, 120, 240, 480, 720, 1440];

    /// <summary>PHG powers the format carries, in watts (APRS101 chapter 7: the digit squared).</summary>
    public static IReadOnlyList<int> PhgPowers { get; } = [.. Enumerable.Range(0, 10).Select(d => d * d)];

    /// <summary>PHG antenna heights the format carries, in feet (10 × 2 to the digit).</summary>
    public static IReadOnlyList<int> PhgHeights { get; } = [.. Enumerable.Range(0, 10).Select(d => 10 << d)];

    /// <summary>PHG gains, in dB.</summary>
    public static IReadOnlyList<int> PhgGains { get; } = [.. Enumerable.Range(0, 10)];

    /// <summary>PHG directions, in degrees; 0 stands for omnidirectional.</summary>
    public static IReadOnlyList<int> PhgDirections { get; } = [0, 45, 90, 135, 180, 225, 270, 315, 360];

    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly Action<AppSettings> _saved;
    private AppSettings _settings = AppSettings.Empty;
    private MicEStatus? _savedMicE;
    private StatusAddition _savedAddition;

    internal BeaconsSectionViewModel(IEngineService engine, Localizer text, Action<AppSettings> saved)
    {
        _engine = engine;
        _text = text;
        _saved = saved;
    }

    /// <summary>"Beacons for Home".</summary>
    [ObservableProperty]
    public partial string? Heading { get; private set; }

    /// <summary>Whether the settings can't be changed here (a gateway's browser).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsReadOnly { get; private set; }

    /// <summary>Why this profile can't beacon (it only listens), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial string? ListensOnlyText { get; private set; }

    /// <summary>Whether the beacons can be set: the settings can change and the profile may transmit.</summary>
    public bool CanEdit => !IsReadOnly && ListensOnlyText is null;

    /// <summary>"Add a source, or untick RX only…" when the profile has none to beacon on, or null.</summary>
    [ObservableProperty]
    public partial string? NoSourcesText { get; private set; }

    /// <summary>"Send your position once by hand to start beaconing" while that's what beacons wait for (behavior 5).</summary>
    [ObservableProperty]
    public partial string? UnconfirmedText { get; private set; }

    // ── The position beacon ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the position beacon is on.</summary>
    [ObservableProperty]
    public partial bool PositionOn { get; set; }

    /// <summary>The schedules, named in the viewer's language. Moving waits for GPS (decision 5), so it isn't offered.</summary>
    public ObservableCollection<BeaconChoice<PositionBeaconSchedule>> Schedules { get; } = [];

    /// <summary>The schedule chosen.</summary>
    [ObservableProperty]
    public partial BeaconChoice<PositionBeaconSchedule>? SelectedSchedule { get; set; }

    /// <summary>The intervals, named.</summary>
    public ObservableCollection<BeaconChoice<int>> PositionIntervals { get; } = [];

    /// <summary>The position beacon's interval.</summary>
    [ObservableProperty]
    public partial BeaconChoice<int>? SelectedPositionInterval { get; set; }

    /// <summary>Send via for positions.</summary>
    public ObservableCollection<ViaChoice> PositionVia { get; } = [];

    /// <summary>Positions go uncompressed: the Standard format (F2.12 behavior 1).</summary>
    [ObservableProperty]
    public partial bool FormatStandard { get; set; } = true;

    /// <summary>Whether positions go compressed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanIncludePhg))]
    public partial bool Compressed { get; set; }

    /// <summary>Positions go as Mic-E, with a status (docs/features/F2.12-mic-e-status.md, behaviors 1–3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanIncludePhg))]
    public partial bool MicE { get; set; }

    /// <summary>The Mic-E statuses, named; custom codes aren't offered (behavior 2).</summary>
    public ObservableCollection<BeaconChoice<MicEStatus>> MicEStatuses { get; } = [];

    /// <summary>The Mic-E status chosen.</summary>
    [ObservableProperty]
    public partial BeaconChoice<MicEStatus>? SelectedMicEStatus { get; set; }

    /// <summary>Whether PHG goes with positions.</summary>
    [ObservableProperty]
    public partial bool IncludePhg { get; set; }

    /// <summary>PHG needs an uncompressed, non-Mic-E position.</summary>
    public bool CanIncludePhg => !Compressed && !MicE;

    /// <summary>The PHG choices, named.</summary>
    public ObservableCollection<BeaconChoice<int>> Powers { get; } = [];

    /// <inheritdoc cref="Powers"/>
    public ObservableCollection<BeaconChoice<int>> Heights { get; } = [];

    /// <inheritdoc cref="Powers"/>
    public ObservableCollection<BeaconChoice<int>> Gains { get; } = [];

    /// <inheritdoc cref="Powers"/>
    public ObservableCollection<BeaconChoice<int>> Directions { get; } = [];

    /// <summary>The PHG chosen.</summary>
    [ObservableProperty]
    public partial BeaconChoice<int>? SelectedPower { get; set; }

    /// <inheritdoc cref="SelectedPower"/>
    [ObservableProperty]
    public partial BeaconChoice<int>? SelectedHeight { get; set; }

    /// <inheritdoc cref="SelectedPower"/>
    [ObservableProperty]
    public partial BeaconChoice<int>? SelectedGain { get; set; }

    /// <inheritdoc cref="SelectedPower"/>
    [ObservableProperty]
    public partial BeaconChoice<int>? SelectedDirection { get; set; }

    // ── The status beacon ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the status beacon is on.</summary>
    [ObservableProperty]
    public partial bool StatusOn { get; set; }

    /// <summary>The status text.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>The most characters a status may have.</summary>
    public static int MaxStatusLength => StatusBeaconSettings.MaxTextLength;

    /// <summary>The saved status texts (docs/features/F2.11-status-reports.md, behavior 1).</summary>
    public ObservableCollection<string> SavedTexts { get; } = [];

    /// <summary>The saved text chosen; choosing one puts it in the status box (behavior 1).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSavedTextCommand))]
    public partial string? SelectedSavedText { get; set; }

    /// <summary>Nothing, the grid square or the time, named (behavior 3).</summary>
    public ObservableCollection<BeaconChoice<StatusAddition>> Additions { get; } = [];

    /// <summary>What's added to the status.</summary>
    [ObservableProperty]
    public partial BeaconChoice<StatusAddition>? SelectedAddition { get; set; }

    /// <summary>"Status sent on 2 sources", once Send status now worked; null otherwise.</summary>
    [ObservableProperty]
    public partial string? StatusSentText { get; private set; }

    partial void OnSelectedSavedTextChanged(string? value)
    {
        if (value is not null)
        {
            StatusText = value;
        }
    }

    /// <summary>Save this text: adds what's in the box to the saved texts, once (behavior 1).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveText))]
    private void SaveText()
    {
        var text = StatusText.Trim();
        SavedTexts.Add(text);
        SelectedSavedText = text;
        SaveTextCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveText() =>
        StatusText.Trim().Length > 0
        && SavedTexts.Count < StatusBeaconSettings.MaxSavedTexts
        && !SavedTexts.Contains(StatusText.Trim());

    /// <summary>Remove: takes the chosen saved text off; the status box keeps its text.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSavedText))]
    private void RemoveSavedText()
    {
        var text = SelectedSavedText!;
        SelectedSavedText = null;
        SavedTexts.Remove(text);
        SaveTextCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveSavedText() => SelectedSavedText is not null;

    /// <summary>Send status now: saves the section, then sends the status once (behavior 5).</summary>
    [RelayCommand]
    private async Task SendStatusNowAsync()
    {
        StatusSentText = null;
        if (!await SaveBeaconsAsync())
        {
            return;
        }
        try
        {
            var sent = await _engine.SendStatusAsync();
            StatusSentText = _text.Format(
                "Beacons.StatusSent",
                new Dictionary<string, object?> { ["count"] = sent.Count }
            );
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
        }
    }

    /// <summary>The intervals, named.</summary>
    public ObservableCollection<BeaconChoice<int>> StatusIntervals { get; } = [];

    /// <summary>The status beacon's interval.</summary>
    [ObservableProperty]
    public partial BeaconChoice<int>? SelectedStatusInterval { get; set; }

    /// <summary>Send via for the status.</summary>
    public ObservableCollection<ViaChoice> StatusVia { get; } = [];

    /// <summary>Why Save was refused, or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>"Saved", for screen readers and the line under Save.</summary>
    [ObservableProperty]
    public partial string? SavedText { get; private set; }

    /// <summary>Shows the active profile's beacons from <paramref name="settings"/>.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        IsReadOnly = settings.ReadOnly;
        var profile = settings.GetActiveProfile();
        var position = profile?.EffectivePositionBeacon ?? PositionBeaconSettings.Off;
        var status = profile?.EffectiveStatusBeacon ?? StatusBeaconSettings.Off;
        Fill(position.IntervalMinutes, status.IntervalMinutes);
        PositionOn = position.On;
        SelectedSchedule = Schedules.FirstOrDefault(s => s.Value == position.Schedule);
        SelectedPositionInterval = PositionIntervals.FirstOrDefault(i => i.Value == position.IntervalMinutes);
        MicE = position.MicE is not null;
        Compressed = !MicE && position.Compressed;
        FormatStandard = !MicE && !Compressed;
        _savedMicE = position.MicE;
        SelectedMicEStatus = null; // chosen afresh from the profile when the lists fill
        IncludePhg = position.Phg is not null;
        var phg = position.Phg ?? new PhgSettings(PhgPowers[3], PhgHeights[1], 3);
        SelectedPower = Powers.FirstOrDefault(c => c.Value == phg.PowerWatts) ?? Powers[0];
        SelectedHeight = Heights.FirstOrDefault(c => c.Value == phg.HeightFeet) ?? Heights[0];
        SelectedGain = Gains.FirstOrDefault(c => c.Value == phg.GainDbi) ?? Gains[0];
        SelectedDirection = Directions.FirstOrDefault(c => c.Value == (phg.Directivity ?? 0)) ?? Directions[0];
        StatusOn = status.On;
        StatusText = status.Text;
        SelectedSavedText = null;
        SavedTexts.Clear();
        foreach (var text in status.SavedTextList)
        {
            SavedTexts.Add(text);
        }
        SaveTextCommand.NotifyCanExecuteChanged();
        _savedAddition = status.Add;
        SelectedAddition = null;
        StatusSentText = null;
        SelectedStatusInterval = StatusIntervals.FirstOrDefault(i => i.Value == status.IntervalMinutes);
        var sources = profile is null ? [] : settings.SourceList.Where(s => s.MaySend && profile.Uses(s)).ToList();
        PositionVia.Clear();
        StatusVia.Clear();
        foreach (var source in sources)
        {
            PositionVia.Add(new ViaChoice(source.Id, source.Name) { IsChecked = position.SendsVia(source.Id) });
            StatusVia.Add(new ViaChoice(source.Id, source.Name) { IsChecked = status.SendsVia(source.Id) });
        }
        Retranslate();
    }

    /// <summary>Rewords the section after a language change.</summary>
    internal void Retranslate()
    {
        var profile = _settings.GetActiveProfile();
        Fill(SelectedPositionInterval?.Value, SelectedStatusInterval?.Value);
        Heading = profile is null
            ? null
            : _text.Format("Beacons.Heading", new Dictionary<string, object?> { ["profile"] = profile.Name });
        ListensOnlyText =
            profile is null || !profile.Licensed || ListenerId.Is(profile.Callsign)
                ? _text["Beacons.ListensOnly"]
                : null;
        NoSourcesText = PositionVia.Count == 0 ? _text["Beacons.NoSources"] : null;
        UnconfirmedText =
            profile is { PositionConfirmed: false } && (PositionOn || StatusOn) ? _text["Beacons.Unconfirmed"] : null;
    }

    /// <summary>Fills the choice lists in the viewer's language, keeping what's chosen.</summary>
    private void Fill(int? positionMinutes, int? statusMinutes)
    {
        var schedule = SelectedSchedule?.Value;
        Schedules.Clear();
        Schedules.Add(new(PositionBeaconSchedule.Decay, _text["Beacons.Schedule.Decay"]));
        Schedules.Add(new(PositionBeaconSchedule.Fixed, _text["Beacons.Schedule.Fixed"]));
        if (schedule == PositionBeaconSchedule.Moving)
        {
            Schedules.Add(new(PositionBeaconSchedule.Moving, _text["Beacons.Schedule.Moving"])); // saved elsewhere
        }
        SelectedSchedule = Schedules.FirstOrDefault(s => s.Value == schedule);
        var status = SelectedMicEStatus?.Value ?? _savedMicE ?? MicEStatus.OffDuty;
        MicEStatuses.Clear();
        foreach (var each in Enum.GetValues<MicEStatus>())
        {
            MicEStatuses.Add(new(each, _text[$"Filter.MicE.{each}"]));
        }
        SelectedMicEStatus = MicEStatuses.FirstOrDefault(c => c.Value == status);
        Refill(PositionIntervals, positionMinutes, m => SelectedPositionInterval = m);
        Refill(StatusIntervals, statusMinutes, m => SelectedStatusInterval = m);
        var addition = SelectedAddition?.Value ?? _savedAddition;
        Additions.Clear();
        Additions.Add(new(StatusAddition.None, _text["Beacons.StatusAdd.None"]));
        Additions.Add(new(StatusAddition.GridSquare, _text["Beacons.StatusAdd.GridSquare"]));
        Additions.Add(new(StatusAddition.Time, _text["Beacons.StatusAdd.Time"]));
        SelectedAddition = Additions.FirstOrDefault(a => a.Value == addition);
        RefillPhg(Powers, PhgPowers, SelectedPower?.Value, "Beacons.Phg.Watts", c => SelectedPower = c);
        RefillPhg(Heights, PhgHeights, SelectedHeight?.Value, "Beacons.Phg.Feet", c => SelectedHeight = c);
        RefillPhg(Gains, PhgGains, SelectedGain?.Value, "Beacons.Phg.Db", c => SelectedGain = c);
        var direction = SelectedDirection?.Value;
        Directions.Clear();
        foreach (var degrees in PhgDirections)
        {
            Directions.Add(
                new(
                    degrees,
                    degrees == 0
                        ? _text["Beacons.Phg.Omni"]
                        : _text.Format("Beacons.Phg.Degrees", new Dictionary<string, object?> { ["degrees"] = degrees })
                )
            );
        }
        SelectedDirection = Directions.FirstOrDefault(c => c.Value == direction);
    }

    private void Refill(ObservableCollection<BeaconChoice<int>> list, int? chosen, Action<BeaconChoice<int>?> select)
    {
        list.Clear();
        var steps = IntervalSteps.ToList();
        if (chosen is { } minutes && !steps.Contains(minutes))
        {
            steps.Add(minutes); // a value saved elsewhere stays choosable
            steps.Sort();
        }
        foreach (var step in steps)
        {
            list.Add(new(step, Interval(step)));
        }
        select(list.FirstOrDefault(c => c.Value == chosen));
    }

    private void RefillPhg(
        ObservableCollection<BeaconChoice<int>> list,
        IReadOnlyList<int> values,
        int? chosen,
        string key,
        Action<BeaconChoice<int>?> select
    )
    {
        list.Clear();
        foreach (var value in values)
        {
            list.Add(new(value, _text.Format(key, new Dictionary<string, object?> { ["value"] = value })));
        }
        select(list.FirstOrDefault(c => c.Value == chosen));
    }

    /// <summary>"30 minutes", "2 hours".</summary>
    private string Interval(int minutes) =>
        minutes % 60 == 0
            ? _text.Format("Beacons.Hours", new Dictionary<string, object?> { ["count"] = minutes / 60 })
            : _text.Format("Beacons.Minutes", new Dictionary<string, object?> { ["count"] = minutes });

    partial void OnPositionOnChanged(bool value) => SavedText = null;

    partial void OnStatusOnChanged(bool value) => SavedText = null;

    partial void OnStatusTextChanged(string value)
    {
        (Error, SavedText) = (null, null);
        SaveTextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Saves both beacons to the active profile.</summary>
    [RelayCommand]
    private async Task SaveAsync() => await SaveBeaconsAsync();

    private async Task<bool> SaveBeaconsAsync()
    {
        if (_settings.GetActiveProfile() is not { } profile)
        {
            return false;
        }
        var position = new PositionBeaconSettings(
            PositionOn,
            SelectedSchedule?.Value ?? PositionBeaconSchedule.Decay,
            SelectedPositionInterval?.Value ?? PositionBeaconSettings.DefaultIntervalMinutes,
            Via(PositionVia),
            Compressed && !MicE,
            IncludePhg && CanIncludePhg
                ? new PhgSettings(
                    SelectedPower?.Value ?? 0,
                    SelectedHeight?.Value ?? PhgHeights[0],
                    SelectedGain?.Value ?? 0,
                    SelectedDirection is { Value: > 0 } d ? d.Value : null
                )
                : null,
            MicE ? SelectedMicEStatus?.Value ?? MicEStatus.OffDuty : null
        );
        var status = new StatusBeaconSettings(
            StatusOn,
            StatusText.Trim(),
            SelectedStatusInterval?.Value ?? StatusBeaconSettings.DefaultIntervalMinutes,
            Via(StatusVia),
            SavedTexts.Count > 0 ? [.. SavedTexts] : null,
            SelectedAddition?.Value ?? StatusAddition.None
        );
        var updated = profile with { PositionBeacon = position, StatusBeacon = status };
        var settings = _settings with
        {
            Profiles = [.. _settings.Profiles.Select(p => p.Id == profile.Id ? updated : p)],
        };
        Error = null;
        try
        {
            await _engine.SaveSettingsAsync(settings);
            var stored = await _engine.GetSettingsAsync(); // the engine decides PositionConfirmed
            Show(stored);
            _saved(stored);
            SavedText = _text["Beacons.Saved"];
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
            return false;
        }
    }

    /// <summary>Every source ticked is null, so a source that can send later is included (behavior 1.2).</summary>
    private static List<string>? Via(IEnumerable<ViaChoice> choices)
    {
        var list = choices.ToList();
        return list.All(c => c.IsChecked) ? null : [.. list.Where(c => c.IsChecked).Select(c => c.Id)];
    }
}

/// <summary>A choice with its name in the viewer's language.</summary>
/// <param name="Value">What it stands for.</param>
/// <param name="Name">Its name.</param>
public sealed record BeaconChoice<T>(T Value, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A Send via checkmark: one source a beacon may go out on.</summary>
public sealed partial class ViaChoice(string id, string name) : ObservableObject
{
    /// <summary>The source's id.</summary>
    public string Id { get; } = id;

    /// <summary>The source's name.</summary>
    public string Name { get; } = name;

    /// <summary>Whether the beacon goes out on it.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}
