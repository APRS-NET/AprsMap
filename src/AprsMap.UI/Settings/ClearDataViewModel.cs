// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Data › Clear (docs/features/F1.19-housekeeping.md): what the station has collected, each with a count, and
/// a confirmation with the count before anything goes (decision 1: no Undo). Offered only where this viewer may change
/// the station, because clearing empties it for every viewer.
/// </summary>
public sealed partial class ClearDataViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private ClearCounts _counts = new(0, 0, 0, 0);

    internal ClearDataViewModel(IEngineService engine, Localizer text)
    {
        _engine = engine;
        _text = text;
        Rows = [.. Enum.GetValues<ClearScope>().Select(scope => new ClearRow(scope, this))];
        Retranslate();
    }

    /// <summary>One row per kind of clear, in the spec's order.</summary>
    public ObservableCollection<ClearRow> Rows { get; }

    /// <summary>The row waiting for its clear to be confirmed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    public partial ClearRow? Pending { get; private set; }

    /// <summary>Whether a confirmation is showing.</summary>
    public bool IsConfirming => Pending is not null;

    /// <summary>"Clear 2,140 stations? This can't be undone."</summary>
    [ObservableProperty]
    public partial string? ConfirmText { get; private set; }

    /// <summary>What the last clear did, or why it failed.</summary>
    [ObservableProperty]
    public partial string? ResultText { get; private set; }

    /// <summary>Fetches the counts again (when the section is shown, and after a clear).</summary>
    internal async Task RefreshAsync()
    {
        try
        {
            _counts = await _engine.GetClearCountsAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return; // the engine went away; the counts stay as they were
        }
        Retranslate();
    }

    internal void Retranslate()
    {
        foreach (var row in Rows)
        {
            row.Text = _text.Format($"Settings.Data.Clear.{row.Scope}", Counted(row.Scope));
            row.IsEmpty = CountOf(row.Scope) == 0;
        }
    }

    internal void Ask(ClearRow row)
    {
        ResultText = null;
        ConfirmText = _text.Format($"Settings.Data.Clear.Confirm.{row.Scope}", Counted(row.Scope));
        Pending = row;
    }

    /// <summary>Clears what the confirmation names.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Pending is not { } row)
        {
            return;
        }
        Pending = null;
        try
        {
            var cleared = await _engine.ClearAsync(row.Scope);
            ResultText = _text.Format(
                $"Settings.Data.Clear.Done.{row.Scope}",
                new Dictionary<string, object?> { ["count"] = cleared }
            );
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            ResultText = _text.Describe(ex);
        }
        await RefreshAsync();
    }

    /// <summary>Keeps everything.</summary>
    [RelayCommand]
    private void Cancel() => Pending = null;

    private int CountOf(ClearScope scope) =>
        scope switch
        {
            ClearScope.Stations => _counts.Stations,
            ClearScope.StaleStations => _counts.StaleStations,
            ClearScope.Tracks => _counts.Tracks,
            ClearScope.Telemetry => _counts.Telemetry,
            _ => _counts.Packets,
        };

    private Dictionary<string, object?> Counted(ClearScope scope) => new() { ["count"] = CountOf(scope) };
}

/// <summary>One kind of clear in Settings › Data.</summary>
public sealed partial class ClearRow(ClearScope scope, ClearDataViewModel owner) : ObservableObject
{
    /// <summary>What it clears.</summary>
    public ClearScope Scope { get; } = scope;

    /// <summary>"Stations (2,140)".</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>Nothing to clear now.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>Asks for confirmation.</summary>
    [RelayCommand]
    private void Clear() => owner.Ask(this);
}
