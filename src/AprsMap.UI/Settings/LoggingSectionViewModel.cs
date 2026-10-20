// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>A level as a drop-down lists it; <see cref="Level"/> null is "Same as overall".</summary>
public sealed record LogLevelChoice(LogLevelSetting? Level, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>One area's level (docs/features/F1.32-logging.md, behavior 4).</summary>
public sealed partial class LogAreaRow(LogArea area) : ObservableObject
{
    /// <summary>The area.</summary>
    public LogArea Area { get; } = area;

    /// <summary>Its name, in the viewer's language.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>"Same as overall", then each level, in the viewer's language.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LogLevelChoice> Choices { get; set; } = [];

    /// <summary>The level chosen.</summary>
    [ObservableProperty]
    public partial LogLevelChoice? Selected { get; set; }

    /// <summary>The environment sets this area's level, so it can't be changed here (behavior 1).</summary>
    [ObservableProperty]
    public partial bool FromEnvironment { get; set; }

    /// <summary>Whether it can be changed here.</summary>
    public bool CanChange => !FromEnvironment;

    partial void OnFromEnvironmentChanged(bool value) => OnPropertyChanged(nameof(CanChange));
}

/// <summary>A header of an OTLP target being edited; a stored value is never shown (behavior 13.5).</summary>
public sealed partial class OtlpHeaderRow(LoggingSectionViewModel owner) : ObservableObject
{
    /// <summary>The header's name.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>A new value; empty keeps the stored one when there is one.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>A value is stored: an empty <see cref="Value"/> keeps it, shown as "••••".</summary>
    [ObservableProperty]
    public partial bool HasStoredValue { get; set; }

    /// <summary>Removes the header.</summary>
    [RelayCommand]
    private void Remove() => owner.EditHeaders.Remove(this);
}

/// <summary>An OTLP target as listed (behavior 13).</summary>
public sealed partial class OtlpTargetRow(OtlpTarget target, string summary, LoggingSectionViewModel owner)
    : ObservableObject
{
    /// <summary>The target.</summary>
    public OtlpTarget Target { get; } = target;

    /// <summary>Its name.</summary>
    public string Name => Target.Name;

    /// <summary>"On · gRPC · logs, metrics · http://localhost:4317".</summary>
    public string Summary { get; } = summary;

    [RelayCommand]
    private void Edit() => owner.EditTarget(this);

    [RelayCommand]
    private void Delete() => owner.DeleteTarget(this);
}

/// <summary>
/// Settings › Logging (docs/features/F1.32-logging.md): the overall level, each area's, the privacy switches, the log
/// file and the OTLP targets, saved together with Save. Levels and the file apply at once; targets after a restart.
/// </summary>
public sealed partial class LoggingSectionViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly Action<AppSettings> _saved;
    private AppSettings _settings = AppSettings.Empty;
    private LoggingStatus? _status;
    private string? _editingId;

    internal LoggingSectionViewModel(IEngineService engine, Localizer text, Action<AppSettings> saved)
    {
        _engine = engine;
        _text = text;
        _saved = saved;
        Areas = [.. Enum.GetValues<LogArea>().Select(a => new LogAreaRow(a))];
        Retranslate();
    }

    /// <summary>Errors to Trace, in the viewer's language.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LogLevelChoice> Levels { get; private set; } = [];

    /// <summary>Rewords the levels, the areas and the targets after a language change, keeping what's chosen.</summary>
    internal void Retranslate()
    {
        var level = SelectedLevel?.Level ?? LogLevelSetting.Information;
        Levels = [.. Enum.GetValues<LogLevelSetting>().Select(l => new LogLevelChoice(l, LevelName(l)))];
        SelectedLevel = Levels.First(l => l.Level == level);
        foreach (var row in Areas)
        {
            var chosen = row.Selected?.Level;
            var had = row.Selected is not null;
            row.Name = _text[$"Logging.Area.{row.Area}"];
            row.Choices = [new LogLevelChoice(null, _text["Logging.SameAsOverall"]), .. Levels];
            row.Selected = had ? row.Choices.First(c => c.Level == chosen) : null;
        }
        ShowTargets([.. Targets.Select(t => t.Target)]);
        if (_status is not null)
        {
            ShowStatus(_status);
        }
    }

    /// <summary>The overall level (behavior 3).</summary>
    [ObservableProperty]
    public partial LogLevelChoice? SelectedLevel { get; set; }

    /// <summary>The environment sets the overall level (behavior 1).</summary>
    [ObservableProperty]
    public partial bool LevelFromEnvironment { get; private set; }

    /// <summary>Each area's level (behavior 4).</summary>
    public IReadOnlyList<LogAreaRow> Areas { get; }

    /// <summary>Log every packet (behavior 5.1).</summary>
    [ObservableProperty]
    public partial bool Packets { get; set; }

    /// <summary>Log message texts (behavior 5.2).</summary>
    [ObservableProperty]
    public partial bool MessageTexts { get; set; }

    /// <summary>Write a log file (behavior 7).</summary>
    [ObservableProperty]
    public partial bool FileOn { get; set; }

    /// <summary>A folder chosen for the log file; empty for the default (behavior 8).</summary>
    [ObservableProperty]
    public partial string Folder { get; set; } = string.Empty;

    /// <summary>The default folder, shown when none is chosen.</summary>
    [ObservableProperty]
    public partial string DefaultFolder { get; private set; } = string.Empty;

    /// <summary>The largest file, in MB, as typed (behavior 9).</summary>
    [ObservableProperty]
    public partial string MaxFileMegabytes { get; set; } = string.Empty;

    /// <summary>Days kept, as typed (behavior 10).</summary>
    [ObservableProperty]
    public partial string KeepDays { get; set; } = string.Empty;

    /// <summary>The total cap in MB, as typed (behavior 11).</summary>
    [ObservableProperty]
    public partial string TotalMegabytes { get; set; } = string.Empty;

    /// <summary>
    /// Picks a folder on this computer and answers its path, or null when the user cancels; set by the desktop app's view
    /// (a browser can't choose a folder on the gateway).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseFolder))]
    public partial Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>Whether Choose folder… is offered.</summary>
    public bool CanChooseFolder => FolderPicker is not null;

    /// <summary>The OTLP targets (behavior 13).</summary>
    public ObservableCollection<OtlpTargetRow> Targets { get; } = [];

    /// <summary>The environment's OTLP target, read only (behavior 14); null when there's none.</summary>
    [ObservableProperty]
    public partial string? EnvironmentTarget { get; private set; }

    /// <summary>"Restart to apply", after a change to the targets (behavior 2).</summary>
    [ObservableProperty]
    public partial bool RestartPending { get; private set; }

    /// <summary>Whether the target form is open.</summary>
    [ObservableProperty]
    public partial bool IsEditingTarget { get; private set; }

    /// <summary>The target form's name.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    /// <summary>The target form's endpoint.</summary>
    [ObservableProperty]
    public partial string EditEndpoint { get; set; } = string.Empty;

    /// <summary>gRPC rather than HTTP/protobuf.</summary>
    [ObservableProperty]
    public partial bool EditGrpc { get; set; } = true;

    /// <summary>The target gets logs.</summary>
    [ObservableProperty]
    public partial bool EditLogs { get; set; } = true;

    /// <summary>The target gets metrics.</summary>
    [ObservableProperty]
    public partial bool EditMetrics { get; set; } = true;

    /// <summary>The target gets traces.</summary>
    [ObservableProperty]
    public partial bool EditTraces { get; set; } = true;

    /// <summary>The target is on.</summary>
    [ObservableProperty]
    public partial bool EditOn { get; set; } = true;

    /// <summary>The target form's headers.</summary>
    public ObservableCollection<OtlpHeaderRow> EditHeaders { get; } = [];

    /// <summary>"Saved", once saved; null otherwise.</summary>
    [ObservableProperty]
    public partial string? SavedText { get; private set; }

    /// <summary>Why the engine refused, in words; null when it didn't.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Shows <paramref name="settings"/>' logging settings, and asks the engine what they can't show.</summary>
    internal async Task ShowAsync(AppSettings settings)
    {
        Show(settings);
        try
        {
            ShowStatus(await _engine.GetLoggingStatusAsync());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
        }
    }

    /// <summary>Shows <paramref name="settings"/>' logging settings.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        var logging = settings.EffectiveLogging;
        SelectedLevel = Levels.First(l => l.Level == logging.Level);
        foreach (var row in Areas)
        {
            var own = logging.OwnLevel(row.Area);
            row.Selected = row.Choices.First(c => c.Level == own);
        }
        Packets = logging.Packets;
        MessageTexts = logging.MessageTexts;
        var file = logging.EffectiveFile;
        FileOn = file.On ?? _status?.FileOnByDefault ?? true;
        Folder = file.Folder ?? string.Empty;
        MaxFileMegabytes = file.MaxFileMegabytes.ToString(CultureInfo.InvariantCulture);
        KeepDays = file.KeepDays.ToString(CultureInfo.InvariantCulture);
        TotalMegabytes = file.TotalMegabytes.ToString(CultureInfo.InvariantCulture);
        ShowTargets(logging.TargetList);
        SavedText = null;
    }

    private void ShowStatus(LoggingStatus status)
    {
        _status = status;
        DefaultFolder = status.DefaultFolder;
        LevelFromEnvironment = status.EnvironmentLevel;
        foreach (var row in Areas)
        {
            row.FromEnvironment = status.EnvironmentAreas?.Contains(row.Area) == true;
        }
        if (_settings.EffectiveLogging.EffectiveFile.On is null)
        {
            FileOn = status.FileOnByDefault;
        }
        EnvironmentTarget = status.EnvironmentEndpoint is { } endpoint
            ? _text.Format("Logging.EnvironmentTarget", new Dictionary<string, object?> { ["endpoint"] = endpoint })
            : null;
        RestartPending = status.RestartPending;
    }

    private void ShowTargets(IReadOnlyList<OtlpTarget> targets)
    {
        Targets.Clear();
        foreach (var target in targets)
        {
            Targets.Add(new OtlpTargetRow(target, Summary(target), this));
        }
    }

    private string Summary(OtlpTarget target)
    {
        var signals = string.Join(
            ", ",
            new[] { OtlpSignals.Logs, OtlpSignals.Metrics, OtlpSignals.Traces }
                .Where(s => target.Signals.HasFlag(s))
                .Select(s => _text[$"Logging.Signal.{s}"])
        );
        return string.Join(
            " · ",
            _text[target.On ? "Logging.TargetOn" : "Logging.TargetOff"],
            _text[$"Logging.Protocol.{target.Protocol}"],
            signals.Length == 0 ? _text["Logging.Signal.None"] : signals,
            target.Endpoint
        );
    }

    /// <summary>Picks a folder with the platform's dialog (behavior 8).</summary>
    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (FolderPicker is { } pick && await pick() is { Length: > 0 } folder)
        {
            Folder = folder;
        }
    }

    /// <summary>Goes back to the default folder.</summary>
    [RelayCommand]
    private void ResetFolder() => Folder = string.Empty;

    /// <summary>Opens the form for a new target.</summary>
    [RelayCommand]
    private void AddTarget()
    {
        _editingId = null;
        EditName = EditEndpoint = string.Empty;
        EditGrpc = EditLogs = EditMetrics = EditTraces = EditOn = true;
        EditHeaders.Clear();
        Error = null;
        IsEditingTarget = true;
    }

    internal void EditTarget(OtlpTargetRow row)
    {
        var target = row.Target;
        _editingId = target.Id;
        EditName = target.Name;
        EditEndpoint = target.Endpoint;
        EditGrpc = target.Protocol == OtlpProtocol.Grpc;
        EditLogs = target.Signals.HasFlag(OtlpSignals.Logs);
        EditMetrics = target.Signals.HasFlag(OtlpSignals.Metrics);
        EditTraces = target.Signals.HasFlag(OtlpSignals.Traces);
        EditOn = target.On;
        EditHeaders.Clear();
        foreach (var header in target.HeaderList)
        {
            EditHeaders.Add(new OtlpHeaderRow(this) { Name = header.Name, HasStoredValue = header.HasValue });
        }
        Error = null;
        IsEditingTarget = true;
    }

    internal void DeleteTarget(OtlpTargetRow row) => Targets.Remove(row);

    /// <summary>Adds a header row to the target form.</summary>
    [RelayCommand]
    private void AddHeader() => EditHeaders.Add(new OtlpHeaderRow(this));

    /// <summary>Keeps the form's target in the list; Save stores it with the rest.</summary>
    [RelayCommand]
    private void KeepTarget()
    {
        var signals =
            (EditLogs ? OtlpSignals.Logs : OtlpSignals.None)
            | (EditMetrics ? OtlpSignals.Metrics : OtlpSignals.None)
            | (EditTraces ? OtlpSignals.Traces : OtlpSignals.None);
        var target = new OtlpTarget(
            _editingId ?? Guid.NewGuid().ToString("N"),
            EditName.Trim(),
            EditOn,
            EditEndpoint.Trim(),
            EditGrpc ? OtlpProtocol.Grpc : OtlpProtocol.HttpProtobuf,
            signals,
            [
                .. EditHeaders
                    .Where(h => h.Name.Trim().Length > 0)
                    .Select(h => new OtlpHeader(
                        h.Name.Trim(),
                        h.Value.Length > 0 ? h.Value : null, // empty keeps the stored value
                        h.HasStoredValue && h.Value.Length == 0
                    )),
            ]
        );
        var row = new OtlpTargetRow(target, Summary(target), this);
        var index = Targets.ToList().FindIndex(t => t.Target.Id == target.Id);
        if (index >= 0)
        {
            Targets[index] = row;
        }
        else
        {
            Targets.Add(row);
        }
        IsEditingTarget = false;
    }

    /// <summary>Closes the target form without keeping it.</summary>
    [RelayCommand]
    private void CancelTarget() => IsEditingTarget = false;

    /// <summary>Saves the section; a limit that isn't a whole number is refused here, the rest by the engine.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = SavedText = null;
        if (
            !Whole(MaxFileMegabytes, out var max)
            || !Whole(KeepDays, out var days)
            || !Whole(TotalMegabytes, out var total)
        )
        {
            Error = _text["Logging.LimitsNotNumbers"];
            return;
        }
        var logging = new LoggingSettings(
            SelectedLevel?.Level ?? LogLevelSetting.Information,
            [.. Areas.Select(a => new LogAreaLevel(a.Area, a.Selected?.Level))],
            Packets,
            MessageTexts,
            new LogFileSettings(
                FileOn == (_status?.FileOnByDefault ?? true) ? null : FileOn,
                Folder.Trim().Length > 0 ? Folder.Trim() : null,
                max,
                days,
                total
            ),
            [.. Targets.Select(t => t.Target)]
        );
        var settings = _settings with { Logging = logging };
        try
        {
            await _engine.SaveSettingsAsync(settings);
            var stored = await _engine.GetSettingsAsync();
            _saved(stored);
            Show(stored);
            ShowStatus(await _engine.GetLoggingStatusAsync());
            SavedText = _text["Logging.Saved"];
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
        }

        static bool Whole(string value, out int number) =>
            int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private string LevelName(LogLevelSetting level) => _text[$"Logging.Level.{level}"];
}
