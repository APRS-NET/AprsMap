// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Settings;
using AprsMap.UI.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// Actions that left Settings for the menus (docs/features/F1.29-menus.md, behaviors 9–11), each in a dialog; Reset
/// layout's question and its undo (behavior 14); and the note when a panel is first closed (behavior 15).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The preference recording that the first-close note was shown (behavior 15).</summary>
    public const string CloseNoteKey = "panels.close-note-shown";

    /// <summary>The dialog over the main view, or null.</summary>
    [ObservableProperty]
    public partial DialogViewModel? Dialog { get; private set; }

    private void OpenDialog(string titleKey, object content) =>
        Dialog = new DialogViewModel(_text[titleKey], content, () => Dialog = null);

    /// <summary>An update's dialog shows in the dialog overlay (F4.15 behavior 9).</summary>
    private void OnUpdatesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AprsMap.UI.Updates.UpdatesViewModel.Dialog))
        {
            return;
        }
        if (Updates.Dialog is { } dialog)
        {
            Dialog = new DialogViewModel(_text["Updates.Title"], dialog, Updates.Close);
        }
        else if (Dialog?.Content is AprsMap.UI.Updates.UpdateDialogViewModel)
        {
            Dialog = null;
        }
    }

    /// <summary>Tools › Clear data… (F1.19): what the station has collected, each confirmed before it goes.</summary>
    internal void OpenClearData()
    {
        if (CanClearData)
        {
            var clear = new ClearDataViewModel(engine, _text);
            _ = clear.RefreshAsync();
            OpenDialog("Shell.Menu.ClearData", clear);
        }
    }

    /// <summary>Tools › Start over… (F0.10): what goes, and the confirmation, as Settings › Data had it.</summary>
    internal void OpenStartOver()
    {
        var data = new DataSectionViewModel(
            _text,
            SharedStation,
            canResetStation: _settings is { ReadOnly: false },
            ResetAppAsync
        );
        data.StartCommand.Execute(null);
        OpenDialog("Menu.Tools.StartOver.Title", data);
    }

    /// <summary>View › Layouts › Save layout as… (F1.23): the name, then saved.</summary>
    internal void OpenSaveLayoutAs()
    {
        Layouts.NewName = string.Empty;
        Layouts.Error = null;
        OpenDialog("Layouts.SaveAs.Title", Layouts);
    }

    private void OnLayoutSaved(object? sender, EventArgs e)
    {
        if (Dialog?.Content == Layouts)
        {
            Dialog = null;
        }
    }

    /// <summary>Help › About: the versions, the connection, the licence and the credits, as Settings › About had them.</summary>
    internal async Task OpenAboutAsync()
    {
        var about = new AboutViewModel(_text);
        GeocodingInfo? geocoding = null;
        try
        {
            geocoding = await engine.GetGeocodingInfoAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // About shows less without it; nothing else depends on it.
        }
        about.Show(_status, geocoding, Auth);
        about.FolderOpener = SharedStation ? null : FolderOpener; // a browser can't open a folder (F1.31 behavior 4a)
        await about.ShowFoldersAsync(engine, SharedStation, IsOperator, Updates.Status is { CanInstall: true });
        await about.ShowComponentsAsync(engine, ComponentReport.Of(), SharedStation, IsOperator); // F1.34
        if (!SharedStation || IsOperator)
        {
            about.UpdatesText = Updates.Summary(time.LocalTimeZone);
            about.CheckUpdatesCommand = Updates.Status is { CheckDisabled: true } ? null : Updates.CheckCommand;
        }
        if (SharedStation && IsSignedIn && Updates.Status is { CanInstall: true, Install: not { Running: true } })
        {
            about.RestartGatewayCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(AskRestartGateway);
        }
        OpenDialog("Menu.Help.About.Title", about);
    }

    /// <summary>
    /// Restart gateway (docs/features/F1.40-gateway-service-updates.md, behavior 2): asks first, as everyone watching
    /// is disconnected for a few seconds; the app reconnects by itself.
    /// </summary>
    private void AskRestartGateway() =>
        OpenDialog(
            "Updates.RestartGateway",
            new ConfirmViewModel(
                _text["Updates.RestartGatewayQuestion"],
                _text["Updates.RestartGateway"],
                () => _ = RestartGatewayAsync(),
                () => Dialog = null
            )
        );

    private async Task RestartGatewayAsync()
    {
        try
        {
            await engine.RestartGatewayAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }

    /// <summary>Help › Keyboard shortcuts.</summary>
    internal void ShowShortcuts(IReadOnlyList<ShortcutRow> rows) =>
        OpenDialog("Menu.Help.Shortcuts.Title", new ShortcutsViewModel(rows));

    private string? _beforeReset;
    private string? _afterReset;

    /// <summary>Whether View offers Undo reset layout: right after a reset, until the layout changes again.</summary>
    [ObservableProperty]
    public partial bool CanUndoResetLayout { get; private set; }

    /// <summary>View › Reset layout: asks first, then resets; the old layout can be had back once (behavior 14).</summary>
    internal void AskResetLayout() =>
        OpenDialog(
            "Shell.Menu.ResetLayout",
            new ConfirmViewModel(
                _text["Menu.View.ResetQuestion"],
                _text["Shell.Menu.ResetLayout"],
                () =>
                {
                    var before = Shell.Save();
                    Shell.Reset();
                    _beforeReset = before; // after Reset, whose own change doesn't count as a new one
                    _afterReset = Shell.Save();
                    CanUndoResetLayout = true;
                },
                () => Dialog = null
            )
        );

    /// <summary>View › Undo reset layout.</summary>
    internal void UndoResetLayout()
    {
        if (_beforeReset is not { } before)
        {
            return;
        }
        _beforeReset = null;
        _afterReset = null;
        CanUndoResetLayout = false;
        Shell.Apply(before, MainScreen?.Invoke() is { } screen ? [screen] : []);
    }

    private bool _closeNoteShown;

    /// <summary>
    /// Follows the layout's changes: one after a reset ends its undo, and the first panel closed from its own × says
    /// where to get it back (behavior 15).
    /// </summary>
    private void OnShellChangedForMenus(object? sender, EventArgs e)
    {
        if (_beforeReset is not null && _afterReset is { } reset && Shell.Save() != reset)
        {
            _beforeReset = null;
            CanUndoResetLayout = false;
        }
    }

    /// <summary>The first panel closed from its own × says where to get it back (behavior 15); a move isn't a close.</summary>
    private void OnPanelClosed(object? sender, string kind)
    {
        if (_closeNoteShown || _togglingPanel)
        {
            return;
        }
        _closeNoteShown = true;
        LayoutNotice = _text.Format(
            "Menu.View.ClosedNote",
            new Dictionary<string, object?> { ["panel"] = _text[PanelKinds.TitleKey(kind)] }
        );
        _ = Preferences?.SaveAsync(CloseNoteKey, "1");
    }

    private bool _togglingPanel;

    /// <summary>Loads whether the first-close note was shown already.</summary>
    internal async Task LoadCloseNoteAsync()
    {
        if (Preferences is not null && await Preferences.LoadAsync(CloseNoteKey) is { Length: > 0 })
        {
            _closeNoteShown = true;
        }
    }
}
