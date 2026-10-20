// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Input;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Objects (docs/features/F2.7-objects-and-items.md): the active profile's objects and items, a form to
/// place or change one, and Kill and Forget. Every change goes to the engine at once; it sends them.
/// </summary>
public sealed partial class ObjectsSectionViewModel(IEngineService engine, Localizer text, Action<AppSettings> saved)
    : ObservableObject
{
    /// <summary>The emergency symbol, never offered: the app sends no emergency (F2.12 decision 3).</summary>
    private const string EmergencySymbol = "\\!";

    private AppSettings _settings = AppSettings.Empty;
    private string? _editingId;

    /// <summary>Every symbol an object may have, by name (behavior 2.4).</summary>
    public static IReadOnlyList<SymbolChoice> Symbols { get; } =
    [.. SymbolChoice.All.Where(c => c.Symbol != EmergencySymbol)];

    /// <summary>The most characters a comment may have.</summary>
    public static int MaxCommentLength => OwnObject.MaxCommentLength;

    /// <summary>"Objects for Home", or null without a profile.</summary>
    [ObservableProperty]
    public partial string? Heading { get; private set; }

    /// <summary>The profile's objects and items, in the order placed.</summary>
    public ObservableCollection<ObjectRow> Rows { get; } = [];

    /// <summary>"No objects yet…" when there are none; null otherwise.</summary>
    [ObservableProperty]
    public partial string? EmptyText { get; private set; }

    /// <summary>Whether the form shows.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    /// <summary>"New object" or "Change NETCTL".</summary>
    [ObservableProperty]
    public partial string? FormHeading { get; private set; }

    /// <summary>An item rather than an object (behavior 2.1).</summary>
    [ObservableProperty]
    public partial bool IsItem { get; set; }

    /// <summary>The name as typed (behavior 2.2).</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>The place, as coordinates in any form the setup wizard reads (behavior 2.3).</summary>
    [ObservableProperty]
    public partial string CoordinatesText { get; set; } = string.Empty;

    /// <summary>The symbol chosen (behavior 2.4).</summary>
    [ObservableProperty]
    public partial SymbolChoice? SelectedSymbol { get; set; }

    /// <summary>The comment (behavior 2.5).</summary>
    [ObservableProperty]
    public partial string Comment { get; set; } = string.Empty;

    /// <summary>The intervals, named (behavior 2.6).</summary>
    public ObservableCollection<BeaconChoice<int>> Intervals { get; } = [];

    /// <summary>The interval chosen.</summary>
    [ObservableProperty]
    public partial BeaconChoice<int>? SelectedInterval { get; set; }

    /// <summary>Send via (behavior 2.7).</summary>
    public ObservableCollection<ViaChoice> Via { get; } = [];

    /// <summary>Why it couldn't be placed, killed or forgotten, in words; null when it could.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Shows the active profile's objects.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        Rows.Clear();
        foreach (var placed in settings.GetActiveProfile()?.ObjectList ?? [])
        {
            Rows.Add(new ObjectRow(placed, RowState(placed), Where(placed.Latitude, placed.Longitude)));
        }
        Retranslate();
    }

    /// <summary>Words the section, and again after a language change.</summary>
    internal void Retranslate()
    {
        var profile = _settings.GetActiveProfile();
        Heading = profile is null
            ? null
            : text.Format("Objects.Heading", new Dictionary<string, object?> { ["profile"] = profile.Name });
        EmptyText = Rows.Count == 0 ? text["Objects.Empty"] : null;
        foreach (var row in Rows)
        {
            row.State = RowState(row.Placed);
        }
        var chosen = SelectedInterval?.Value;
        Intervals.Clear();
        foreach (var minutes in BeaconsSectionViewModel.IntervalSteps)
        {
            Intervals.Add(
                new(
                    minutes,
                    minutes % 60 == 0
                        ? text.Format("Beacons.Hours", new Dictionary<string, object?> { ["count"] = minutes / 60 })
                        : text.Format("Beacons.Minutes", new Dictionary<string, object?> { ["count"] = minutes })
                )
            );
        }
        SelectedInterval = Intervals.FirstOrDefault(i => i.Value == chosen);
    }

    /// <summary>Add: a new object, its place typed (behavior 1).</summary>
    [RelayCommand]
    private void Add() => StartNew(null);

    /// <summary>Place an object here, from the map (behavior 1).</summary>
    internal void StartAt(double latitude, double longitude) => StartNew((latitude, longitude));

    private void StartNew((double Latitude, double Longitude)? at)
    {
        _editingId = null;
        Fill(new OwnObject(string.Empty, string.Empty, OwnObjectKind.Object, at?.Latitude ?? 0, at?.Longitude ?? 0));
        CoordinatesText = at is var (lat, lon) ? Where(lat, lon) : string.Empty;
        FormHeading = text["Objects.New"];
    }

    /// <summary>Change: opens the form on one.</summary>
    [RelayCommand]
    private void Edit(ObjectRow row)
    {
        _editingId = row.Placed.Id;
        Fill(row.Placed);
        FormHeading = text.Format("Objects.Change", new Dictionary<string, object?> { ["name"] = row.Placed.Name });
    }

    private void Fill(OwnObject placed)
    {
        IsItem = placed.Kind == OwnObjectKind.Item;
        Name = placed.Name;
        CoordinatesText = Where(placed.Latitude, placed.Longitude);
        SelectedSymbol = Symbols.FirstOrDefault(s => s.Symbol == placed.Symbol) ?? SymbolChoice.For(placed.Symbol);
        Comment = placed.Comment;
        SelectedInterval =
            Intervals.FirstOrDefault(i => i.Value == placed.IntervalMinutes)
            ?? Intervals.FirstOrDefault(i => i.Value == OwnObject.DefaultIntervalMinutes);
        Via.Clear();
        var profile = _settings.GetActiveProfile();
        foreach (var source in _settings.SourceList.Where(s => s.MaySend && profile?.Uses(s) == true))
        {
            Via.Add(new ViaChoice(source.Id, source.Name) { IsChecked = placed.SendsVia(source.Id) });
        }
        Error = null;
        IsEditing = true;
    }

    /// <summary>Cancel: closes the form without placing anything.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Error = null;
    }

    /// <summary>Save and send: places it, or changes the one being edited (behavior 3).</summary>
    [RelayCommand]
    private async Task SaveObjectAsync()
    {
        Error = null;
        switch (CoordinateParser.TryParse(CoordinatesText, out var lat, out var lon))
        {
            case CoordinateParseError.None:
                break;
            case CoordinateParseError.OutOfRange:
                Error = text["Wizard.Location.Coordinates.OutOfRange"];
                return;
            default:
                Error = text["Wizard.Location.Coordinates.Unreadable"];
                return;
        }
        var placed = new OwnObject(
            _editingId ?? Guid.NewGuid().ToString("N"),
            Name.Trim(),
            IsItem ? OwnObjectKind.Item : OwnObjectKind.Object,
            lat,
            lon,
            SelectedSymbol?.Symbol ?? OwnObject.DefaultSymbol,
            Comment.Trim(),
            SelectedInterval?.Value ?? OwnObject.DefaultIntervalMinutes,
            Via.All(v => v.IsChecked) ? null : [.. Via.Where(v => v.IsChecked).Select(v => v.Id)]
        );
        if (await RunAsync(() => engine.PlaceObjectAsync(placed)))
        {
            IsEditing = false;
        }
    }

    /// <summary>Move an object here, from the map (behavior 4).</summary>
    internal Task MoveAsync(string id, double latitude, double longitude) =>
        _settings.GetActiveProfile()?.ObjectList.FirstOrDefault(o => o.Id == id) is { } placed
            ? RunAsync(() => engine.PlaceObjectAsync(placed with { Latitude = latitude, Longitude = longitude }))
            : Task.CompletedTask;

    /// <summary>Kill: it's sent killed three times, then removed (behavior 8).</summary>
    [RelayCommand]
    private async Task KillAsync(ObjectRow row) =>
        await RunAsync(() => engine.RemoveObjectAsync(row.Placed.Id, kill: true));

    /// <summary>Forget: removed without sending anything (behavior 9).</summary>
    [RelayCommand]
    private async Task ForgetAsync(ObjectRow row) =>
        await RunAsync(() => engine.RemoveObjectAsync(row.Placed.Id, kill: false));

    /// <summary>Runs one change and shows the objects as the engine has them now; false, with why, when refused.</summary>
    private async Task<bool> RunAsync(Func<Task> change)
    {
        try
        {
            await change();
            var stored = await engine.GetSettingsAsync();
            Show(stored);
            saved(stored);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = text.Describe(ex);
            return false;
        }
    }

    private string RowState(OwnObject placed) =>
        text[
            (placed.Live, placed.Kind) switch
            {
                (false, _) => "Objects.State.Killing",
                (true, OwnObjectKind.Item) => "Objects.State.Item",
                _ => "Objects.State.Object",
            }
        ];

    /// <summary>"49.0583, -72.0292": what the coordinates box reads back.</summary>
    private static string Where(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:0.0####}, {longitude:0.0####}");
}

/// <summary>One object in Settings › Objects.</summary>
/// <param name="obj">The object.</param>
/// <param name="state">"Object", "Item", or "Being killed".</param>
/// <param name="where">Its coordinates.</param>
public sealed partial class ObjectRow(OwnObject obj, string state, string where) : ObservableObject
{
    /// <summary>The object.</summary>
    public OwnObject Placed { get; } = obj;

    /// <summary>Its name.</summary>
    public string Name => Placed.Name;

    /// <summary>Its coordinates and comment.</summary>
    public string Details => Placed.Comment.Length > 0 ? $"{where} · {Placed.Comment}" : where;

    /// <summary>"Object", "Item", or "Being killed".</summary>
    [ObservableProperty]
    public partial string State { get; set; } = state;

    /// <summary>Whether it can still be killed or changed.</summary>
    public bool IsLive => Placed.Live;
}
