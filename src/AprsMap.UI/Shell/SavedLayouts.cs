// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Shell;

/// <summary>
/// Saved layouts (docs/features/F1.23-saved-layouts.md): named layouts, each with the monitor setup it was last used
/// with, applied from the app menu or Settings › Layout; when the monitors change to a setup a layout was used with, the
/// app asks before switching. Per viewer, in the preference store under <see cref="Key"/>.
/// </summary>
public sealed partial class SavedLayoutsViewModel : ObservableObject
{
    /// <summary>The preference the saved layouts are kept under.</summary>
    public const string Key = "layouts";

    private readonly ShellViewModel _shell;
    private readonly Localizer _text;
    private IPreferenceStore? _store;
    private Func<IReadOnlyList<ScreenArea>> _screens = () => [];
    private Func<MainWindowPlacement?> _placement = () => null;
    private Action<MainWindowPlacement>? _placeMain;
    private List<string> _dontAsk = [];
    private string? _promptedFor;
    private string? _lastSetup;

    internal SavedLayoutsViewModel(ShellViewModel shell, Localizer text)
    {
        _shell = shell;
        _text = text;
    }

    /// <summary>The saved layouts, by name.</summary>
    public ObservableCollection<SavedLayoutRow> Layouts { get; } = [];

    /// <summary>The name for Save layout as.</summary>
    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    /// <summary>Whether the app asks to switch when the monitors change (behavior 4); on unless turned off.</summary>
    [ObservableProperty]
    public partial bool Ask { get; set; } = true;

    /// <summary>"Monitors changed. Use layout Desk?", while asking; null otherwise (behavior 3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking))]
    public partial string? PromptText { get; private set; }

    /// <summary>Whether the question is showing.</summary>
    public bool IsAsking => PromptText is not null;

    /// <summary>Said once when the saved layouts couldn't be read (behavior 5); null otherwise.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Why the last action couldn't be done; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Error { get; internal set; }

    private SavedLayoutRow? _prompted;

    partial void OnAskChanged(bool value) => _ = KeepAsync();

    /// <summary>
    /// Connects to the platform: where the layouts are kept, the screens as they are now, the main window's place, and
    /// how to move the main window (desktop only).
    /// </summary>
    internal void Attach(
        IPreferenceStore? store,
        Func<IReadOnlyList<ScreenArea>> screens,
        Func<MainWindowPlacement?> placement,
        Action<MainWindowPlacement>? placeMain
    )
    {
        _store = store;
        _screens = screens;
        _placement = placement;
        _placeMain = placeMain;
    }

    /// <summary>Loads the saved layouts; one that can't be read is dropped with a note (behavior 5).</summary>
    internal async Task LoadAsync()
    {
        if (_store is null || await _store.LoadAsync(Key) is not { } json)
        {
            return;
        }
        try
        {
            var file = JsonSerializer.Deserialize(json, SavedLayoutsJson.Default.File) ?? new File([], true, [], null);
            Layouts.Clear();
            foreach (var saved in file.Layouts.Where(l => LayoutFile.Read(l.Layout) is not null))
            {
                Layouts.Add(new SavedLayoutRow(saved, this));
            }
            if (Layouts.Count < file.Layouts.Count)
            {
                Notice = _text["Layouts.SomeUnreadable"];
            }
            _dontAsk = [.. file.DontAsk ?? []];
            _lastSetup = file.LastScreens; // at start-up, the same monitors as last time aren't a change
            Ask = file.Ask;
        }
        catch (JsonException)
        {
            Notice = _text["Layouts.Unreadable"];
            await _store.SetAsideAsync(Key);
        }
    }

    /// <summary>Saves the layout as it is now, with the monitors in use, under <see cref="NewName"/> (behavior 1).</summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var name = NewName.Trim();
        Error = null;
        if (name.Length == 0)
        {
            Error = _text["Layouts.NameNeeded"];
            return;
        }
        var saved = Current(name);
        if (Layouts.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same)
        {
            same.Replace(saved); // the same name: updated, not doubled
        }
        else
        {
            Layouts.Add(new SavedLayoutRow(saved, this));
        }
        NewName = string.Empty;
        await KeepAsync();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after Save layout as kept a layout (it closes its dialog, F1.29 behavior 11).</summary>
    public event EventHandler? Saved;

    /// <summary>Puts a saved layout on screen, and the main window where it was (behavior 1).</summary>
    internal async Task ApplyAsync(SavedLayoutRow row)
    {
        var screens = _screens();
        if (_shell.Apply(row.Saved.Layout, screens) == LayoutRestore.Unreadable)
        {
            Error = _text["Layouts.Unreadable"];
            return;
        }
        if (LayoutFile.ReadMainWindow(row.Saved.Layout) is { } main)
        {
            _placeMain?.Invoke(main);
        }
        row.Replace(row.Saved with { Screens = WindowPlacement.Fingerprint(screens) }); // last used with these
        _lastSetup = row.Saved.Screens;
        PromptText = null;
        await KeepAsync();
    }

    /// <summary>Saves the current arrangement into a saved layout (behavior 1).</summary>
    internal Task UpdateAsync(SavedLayoutRow row)
    {
        row.Replace(Current(row.Name));
        return KeepAsync();
    }

    /// <summary>Renames a saved layout to what's typed in its row (behavior 1).</summary>
    internal Task RenameAsync(SavedLayoutRow row)
    {
        var name = row.EditName.Trim();
        if (name.Length == 0)
        {
            row.EditName = row.Name;
            return Task.CompletedTask;
        }
        row.Replace(row.Saved with { Name = name });
        return KeepAsync();
    }

    /// <summary>Deletes a saved layout (behavior 1).</summary>
    internal Task DeleteAsync(SavedLayoutRow row)
    {
        Layouts.Remove(row);
        return KeepAsync();
    }

    /// <summary>
    /// The monitors changed, or the app started: when a saved layout was last used with the setup now, and asking isn't
    /// off for it, asks before switching (behavior 3). Never switches by itself.
    /// </summary>
    internal void ScreensChanged()
    {
        var setup = WindowPlacement.Fingerprint(_screens());
        if (setup == _lastSetup)
        {
            return; // asked once per change of monitors
        }
        _lastSetup = setup;
        _ = KeepAsync();
        if (!Ask || _dontAsk.Contains(setup))
        {
            return;
        }
        if (Layouts.LastOrDefault(l => l.Saved.Screens == setup) is not { } match)
        {
            return; // nothing saved for these monitors: nothing asked (windows are still fitted, F1.0 behavior 12)
        }
        _promptedFor = setup;
        _prompted = match;
        PromptText = _text.Format("Layouts.Prompt", new Dictionary<string, object?> { ["layout"] = match.Name });
    }

    /// <summary>Use: applies the layout offered.</summary>
    [RelayCommand]
    private Task UsePromptedAsync() => _prompted is { } row ? ApplyAsync(row) : Task.CompletedTask;

    /// <summary>Keep current: leaves the layout as it is.</summary>
    [RelayCommand]
    private void KeepCurrent() => PromptText = null;

    /// <summary>Don't ask again for this setup (behavior 4).</summary>
    [RelayCommand]
    private async Task DontAskAgainAsync()
    {
        if (_promptedFor is { } setup && !_dontAsk.Contains(setup))
        {
            _dontAsk.Add(setup);
        }
        PromptText = null;
        await KeepAsync();
    }

    /// <summary>Hides <see cref="Notice"/>.</summary>
    [RelayCommand]
    private void DismissNotice() => Notice = null;

    private SavedLayout Current(string name) =>
        new(name, WindowPlacement.Fingerprint(_screens()), _shell.Save(_placement()));

    private Task KeepAsync() =>
        _store?.SaveAsync(
            Key,
            JsonSerializer.Serialize(
                new File([.. Layouts.Select(l => l.Saved)], Ask, _dontAsk, _lastSetup),
                SavedLayoutsJson.Default.File
            )
        )
        ?? Task.CompletedTask;

    /// <summary>What's kept: the layouts, whether to ask, the setups not to ask about, and the monitors last seen.</summary>
    internal sealed record File(
        IReadOnlyList<SavedLayout> Layouts,
        bool Ask,
        IReadOnlyList<string>? DontAsk,
        string? LastScreens
    );
}

/// <summary>A saved layout: its name, the monitor setup it was last used with, and the layout as F1.0 saves it.</summary>
public sealed record SavedLayout(string Name, string Screens, string Layout);

/// <summary>A saved layout in the app menu and Settings › Layout.</summary>
public sealed partial class SavedLayoutRow : ObservableObject
{
    private readonly SavedLayoutsViewModel _owner;

    internal SavedLayoutRow(SavedLayout saved, SavedLayoutsViewModel owner)
    {
        Saved = saved;
        _owner = owner;
        EditName = saved.Name;
    }

    /// <summary>The layout as kept.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    public partial SavedLayout Saved { get; private set; }

    /// <summary>Its name.</summary>
    public string Name => Saved.Name;

    /// <summary>The name in the rename box.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; }

    internal void Replace(SavedLayout saved)
    {
        Saved = saved;
        EditName = saved.Name;
    }

    [RelayCommand]
    private Task ApplyAsync() => _owner.ApplyAsync(this);

    [RelayCommand]
    private Task UpdateAsync() => _owner.UpdateAsync(this);

    [RelayCommand]
    private Task RenameAsync() => _owner.RenameAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SavedLayoutsViewModel.File))]
internal sealed partial class SavedLayoutsJson : JsonSerializerContext;
