// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>One profile in the list, with its actions.</summary>
public sealed partial class ProfileRow : ObservableObject
{
    internal ProfileRow(StationProfile profile, bool isActive, bool canDelete, bool readOnly)
    {
        Profile = profile;
        IsActive = isActive;
        CanDelete = canDelete;
        CanChange = !readOnly;
    }

    /// <summary>The profile.</summary>
    public StationProfile Profile { get; }

    /// <summary>Its name.</summary>
    public string Name => Profile.Name;

    /// <summary>Its callsign.</summary>
    public string Callsign => Profile.Callsign;

    /// <summary>Whether it is the one the engine uses.</summary>
    public bool IsActive { get; }

    /// <summary>Whether Use is offered: not the active one, and changes allowed.</summary>
    public bool CanUse => !IsActive && CanChange;

    /// <summary>Whether it can be deleted: not the only profile (F0.8 behavior 10).</summary>
    public bool CanDelete { get; }

    /// <summary>Whether this viewer may change profiles (not on a read-only gateway).</summary>
    public bool CanChange { get; }

    /// <summary>"Active", in the viewer's language, or null.</summary>
    [ObservableProperty]
    public partial string? ActiveText { get; set; }

    /// <summary>The server: a region name for a preset, otherwise host:port.</summary>
    [ObservableProperty]
    public partial string ServerText { get; set; } = string.Empty;

    /// <summary>The location's 6-character locator, or null.</summary>
    [ObservableProperty]
    public partial string? Locator { get; set; }

    /// <summary>Accessible names that say which profile an action is for ("Edit Home").</summary>
    [ObservableProperty]
    public partial string UseName { get; set; } = string.Empty;

    /// <inheritdoc cref="UseName"/>
    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    /// <inheritdoc cref="UseName"/>
    [ObservableProperty]
    public partial string CopyName { get; set; } = string.Empty;

    /// <inheritdoc cref="UseName"/>
    [ObservableProperty]
    public partial string DeleteName { get; set; } = string.Empty;

    /// <summary>Switches to this profile.</summary>
    public IAsyncRelayCommand UseCommand { get; internal set; } = null!;

    /// <summary>Opens the editor.</summary>
    public IAsyncRelayCommand EditCommand { get; internal set; } = null!;

    /// <summary>Adds a copy and opens it in the editor.</summary>
    public IAsyncRelayCommand CopyCommand { get; internal set; } = null!;

    /// <summary>Deletes, with Undo.</summary>
    public IAsyncRelayCommand DeleteCommand { get; internal set; } = null!;
}

/// <summary>
/// The Profiles section of Settings (docs/features/F0.8-profiles.md): the list, Use, Edit, Copy, Delete with Undo, and
/// Add. Every change saves the whole settings through the engine, which validates them.
/// </summary>
public sealed partial class ProfilesSectionViewModel : ObservableObject, IDisposable
{
    /// <summary>How long Undo stays offered after a delete.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);

    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly TimeProvider _time;
    private readonly Func<WizardMode, StationProfile?, AppSettings, Task> _openEditor;
    private readonly Action<AppSettings> _saved;
    private IReadOnlyList<ServerPreset> _presets = [];
    private AppSettings? _settings;
    private AppSettings? _beforeDelete;
    private ITimer? _undoTimer;

    /// <summary>
    /// Creates the section. <paramref name="openEditor"/> opens the setup wizard to add or edit a profile;
    /// <paramref name="saved"/> tells the rest of the app the settings changed.
    /// </summary>
    public ProfilesSectionViewModel(
        IEngineService engine,
        TimeProvider time,
        Localizer text,
        Func<WizardMode, StationProfile?, AppSettings, Task> openEditor,
        Action<AppSettings> saved
    )
    {
        _engine = engine;
        _time = time;
        _text = text;
        _openEditor = openEditor;
        _saved = saved;
    }

    /// <summary>The profiles, in order.</summary>
    public ObservableCollection<ProfileRow> Rows { get; } = [];

    /// <summary>Whether this viewer may only look (a read-only gateway; behavior 4).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsReadOnly { get; set; }

    /// <summary>Why the last change failed, or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>"Portable deleted.", while Undo is offered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoDelete))]
    public partial string? DeletedText { get; set; }

    /// <summary>Whether Undo is offered after a delete.</summary>
    public bool CanUndoDelete => DeletedText is not null;

    /// <summary>"The active profile's filter now: r/40/-105/150", for anyone checking it (F1.2 behavior 7).</summary>
    [ObservableProperty]
    public partial string? FilterText { get; private set; }

    internal void ShowFilter(string? filter) =>
        FilterText = string.IsNullOrEmpty(filter)
            ? null
            : _text.Format("Settings.Profiles.Filter", new Dictionary<string, object?> { ["filter"] = filter });

    /// <summary>Shows <paramref name="settings"/>; asks the engine for the region names and locators it needs.</summary>
    public async Task ShowAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        IsReadOnly = settings.ReadOnly;
        if (_presets.Count == 0)
        {
            try
            {
                _presets = await _engine.GetServerPresetsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Servers then show as host:port.
            }
        }
        var rows = settings
            .Profiles.Select(p => new ProfileRow(
                p,
                p.Id == settings.ActiveProfileId,
                settings.Profiles.Count > 1,
                settings.ReadOnly
            ))
            .ToList();
        foreach (var row in rows)
        {
            row.UseCommand = new AsyncRelayCommand(() => UseAsync(row.Profile));
            row.EditCommand = new AsyncRelayCommand(() => _openEditor(WizardMode.Edit, row.Profile, Current));
            row.CopyCommand = new AsyncRelayCommand(() => CopyAsync(row.Profile));
            row.DeleteCommand = new AsyncRelayCommand(() => DeleteAsync(row.Profile));
            if (row.Profile is { Latitude: { } lat, Longitude: { } lon })
            {
                try
                {
                    row.Locator = await _engine.GetLocatorAsync(lat, lon);
                }
                catch (ArgumentException)
                {
                    // Off the globe can't be stored; nothing to show.
                }
            }
        }
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }
        Retranslate();
    }

    private AppSettings Current => _settings ?? AppSettings.Empty;

    private bool CanAdd() => !IsReadOnly;

    /// <summary>Opens the wizard to add a profile (behavior 5).</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddAsync() => _openEditor(WizardMode.Add, null, Current);

    /// <summary>Puts back the profile deleted last.</summary>
    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        var before = _beforeDelete;
        StopUndo();
        if (before is not null)
        {
            await SaveAsync(before);
        }
    }

    private async Task UseAsync(StationProfile profile) =>
        await SaveAsync(Current with { ActiveProfileId = profile.Id });

    /// <summary>Adds "name (copy)" after the original and opens it in the editor (behavior 9).</summary>
    private async Task CopyAsync(StationProfile profile)
    {
        var settings = Current;
        var copy = profile with { Id = Guid.NewGuid().ToString("N"), Name = CopyName(profile.Name, settings) };
        var profiles = settings.Profiles.ToList();
        profiles.Insert(profiles.FindIndex(p => p.Id == profile.Id) + 1, copy);
        if (await SaveAsync(settings with { Profiles = profiles }) is { } saved)
        {
            await _openEditor(WizardMode.Edit, saved.Profiles.First(p => p.Id == copy.Id), saved);
        }
    }

    /// <summary>"Home (copy)", then "Home (copy 2)"… until no profile has the name.</summary>
    internal string CopyName(string name, AppSettings settings)
    {
        for (var n = 1; ; n++)
        {
            var candidate = _text.Format(
                n == 1 ? "Settings.Profiles.CopyName" : "Settings.Profiles.CopyNameNumbered",
                new Dictionary<string, object?> { ["name"] = name, ["n"] = n }
            );
            if (!settings.Profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    /// <summary>Deletes at once, with Undo; the active profile hands over to the first remaining one (behavior 10).</summary>
    private async Task DeleteAsync(StationProfile profile)
    {
        var settings = Current;
        if (settings.Profiles.Count < 2)
        {
            return;
        }
        var remaining = settings.Profiles.Where(p => p.Id != profile.Id).ToList();
        var active = settings.ActiveProfileId == profile.Id ? remaining[0].Id : settings.ActiveProfileId;
        if (await SaveAsync(settings with { Profiles = remaining, ActiveProfileId = active }) is not null)
        {
            _beforeDelete = settings;
            DeletedText = _text.Format(
                "Settings.Profiles.Deleted",
                new Dictionary<string, object?> { ["profile"] = profile.Name }
            );
            _undoTimer?.Dispose();
            var context = SynchronizationContext.Current;
            _undoTimer = _time.CreateTimer(
                _ =>
                {
                    if (context is null)
                    {
                        StopUndo();
                    }
                    else
                    {
                        context.Post(_ => StopUndo(), null);
                    }
                },
                null,
                UndoWindow,
                Timeout.InfiniteTimeSpan
            );
        }
    }

    private void StopUndo()
    {
        _undoTimer?.Dispose();
        _undoTimer = null;
        _beforeDelete = null;
        DeletedText = null;
    }

    /// <summary>Saves, then shows what the engine now has; null and the reason shown when it refuses.</summary>
    private async Task<AppSettings?> SaveAsync(AppSettings settings)
    {
        Error = null;
        try
        {
            await _engine.SaveSettingsAsync(settings);
            var saved = await _engine.GetSettingsAsync();
            await ShowAsync(saved);
            _saved(saved);
            return saved;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
            return null;
        }
    }

    /// <summary>Rewords the rows after a language change.</summary>
    public void Retranslate()
    {
        foreach (var row in Rows)
        {
            var name = new Dictionary<string, object?> { ["profile"] = row.Name };
            row.ActiveText = row.IsActive ? _text["Settings.Profiles.Active"] : null;
            var preset = _presets.FirstOrDefault(p =>
                string.Equals(p.Server, row.Profile.Server, StringComparison.OrdinalIgnoreCase)
            );
            var regionKey = preset is null ? null : "Wizard.Server.Region." + preset.Key;
            row.ServerText =
                preset is null ? row.Profile.Server
                : Localizer.Exists(regionKey!) ? _text[regionKey!]
                : preset.Name;
            row.UseName = _text.Format("Settings.Profiles.UseNamed", name);
            row.EditName = _text.Format("Settings.Profiles.EditNamed", name);
            row.CopyName = _text.Format("Settings.Profiles.CopyNamed", name);
            row.DeleteName = _text.Format("Settings.Profiles.DeleteNamed", name);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _undoTimer?.Dispose();
}
