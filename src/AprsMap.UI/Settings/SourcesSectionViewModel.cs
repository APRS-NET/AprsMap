// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Sources (docs/features/F1.25-sources.md, behaviors 1–3): the master list profiles pick from, with Add a
/// source, edit and delete. APRS-IS is the only kind built; the others are listed as coming. Deleting a source in use
/// asks first and unticks it from its profiles.
/// </summary>
public sealed partial class SourcesSectionViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly Action<AppSettings> _saved;
    private AppSettings _settings = AppSettings.Empty;
    private string? _editingId;

    internal SourcesSectionViewModel(IEngineService engine, Localizer text, Action<AppSettings> saved)
    {
        _engine = engine;
        _text = text;
        _saved = saved;
        Kinds =
        [
            .. Enum.GetValues<SourceKind>()
                .Select(k => new SourceKindChoice(k, k is SourceKind.AprsIs or SourceKind.KissTcp, text, this)),
        ];
    }

    /// <summary>The kind in the form: picked for a new source, fixed for one being edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAprsIs), nameof(IsKiss))]
    public partial SourceKind EditKind { get; set; } = SourceKind.AprsIs;

    /// <summary>Whether the form shows APRS-IS's server and filter.</summary>
    public bool IsAprsIs => EditKind == SourceKind.AprsIs;

    /// <summary>Whether the form shows a KISS TNC's host, port and the profiles that use it (F3.1 behavior 1).</summary>
    public bool IsKiss => EditKind == SourceKind.KissTcp;

    /// <summary>Whether the kind can still be picked: only while adding.</summary>
    [ObservableProperty]
    public partial bool CanPickKind { get; private set; }

    /// <summary>The TNC's host in the form.</summary>
    [ObservableProperty]
    public partial string EditHost { get; set; } = string.Empty;

    /// <summary>The TNC's port in the form; empty for 8001.</summary>
    [ObservableProperty]
    public partial string EditPort { get; set; } = string.Empty;

    /// <summary>The TNC's radio port in the form, 0 to 15; empty for 0.</summary>
    [ObservableProperty]
    public partial string EditTncPort { get; set; } = string.Empty;

    /// <summary>
    /// The profiles, each ticked when it uses the KISS source being edited (F3.1 behavior 2); a new one is ticked for
    /// the active profile, so it opens at once.
    /// </summary>
    public ObservableCollection<ProfileChoice> EditProfiles { get; } = [];

    partial void OnEditKindChanged(SourceKind value)
    {
        foreach (var kind in Kinds)
        {
            kind.Refresh();
        }
    }

    /// <summary>The sources, in order.</summary>
    public ObservableCollection<SourceRow> Rows { get; } = [];

    /// <summary>The kinds Add a source offers; only built ones can be picked (behavior 1).</summary>
    public IReadOnlyList<SourceKindChoice> Kinds { get; }

    /// <summary>Whether this viewer may only look (a gateway without operator sign-in).</summary>
    [ObservableProperty]
    public partial bool IsReadOnly { get; private set; }

    /// <summary>Whether the add or edit form is open.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    /// <summary>The form's title: Add a source, or Edit with the source's name.</summary>
    [ObservableProperty]
    public partial string? EditTitle { get; private set; }

    /// <summary>The source's name in the form.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    /// <summary>The APRS-IS server in the form, <c>host</c> or <c>host:port</c>.</summary>
    [ObservableProperty]
    public partial string EditServer { get; set; } = string.Empty;

    /// <summary>The filter text in the form, added to the automatic filter (F1.2).</summary>
    [ObservableProperty]
    public partial string EditFilter { get; set; } = string.Empty;

    /// <summary>"Used by: Home, Portable" for the source being edited (behavior 2).</summary>
    [ObservableProperty]
    public partial string? EditUsedBy { get; private set; }

    /// <summary>Why the engine refused the last change, in words; null when it didn't.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The source waiting for its delete to be confirmed, because profiles use it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete))]
    public partial SourceRow? PendingDelete { get; private set; }

    /// <summary>Whether a delete waits for confirmation.</summary>
    public bool IsConfirmingDelete => PendingDelete is not null;

    /// <summary>"Rotate is used by Home and Portable. Delete it and untick it there?"</summary>
    [ObservableProperty]
    public partial string? PendingDeleteText { get; private set; }

    /// <summary>The settings shown.</summary>
    internal AppSettings Current => _settings;

    /// <summary>Shows <paramref name="settings"/>.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        IsReadOnly = settings.ReadOnly;
        Rows.Clear();
        foreach (var source in settings.SourceList)
        {
            Rows.Add(new SourceRow(source, this));
        }
        Retranslate();
    }

    /// <summary>Rewords the rows after a language change.</summary>
    internal void Retranslate()
    {
        foreach (var kind in Kinds)
        {
            kind.Retranslate(_text);
        }
        foreach (var row in Rows)
        {
            var users = UsersOf(row.Source);
            row.KindText = _text[$"Settings.Sources.Kind.{row.Source.Kind}"];
            row.UsedByText =
                users.Count == 0
                    ? _text["Settings.Sources.Unused"]
                    : _text.Format(
                        "Settings.Sources.UsedBy",
                        new Dictionary<string, object?> { ["profiles"] = string.Join(", ", users) }
                    );
            row.EditName = _text.Format(
                "Settings.Sources.EditNamed",
                new Dictionary<string, object?> { ["source"] = row.Name }
            );
            row.DeleteName = _text.Format(
                "Settings.Sources.DeleteNamed",
                new Dictionary<string, object?> { ["source"] = row.Name }
            );
        }
    }

    /// <summary>Opens the form for a new APRS-IS source (behavior 2).</summary>
    [RelayCommand]
    private void Add()
    {
        _editingId = null;
        EditTitle = _text["Settings.Sources.Add"];
        EditName = EditServer = EditFilter = EditHost = EditPort = EditTncPort = string.Empty;
        EditKind = SourceKind.AprsIs;
        CanPickKind = true;
        FillProfiles(id => id == _settings.ActiveProfileId);
        EditUsedBy = null;
        Error = null;
        IsEditing = true;
    }

    internal void Edit(SourceRow row)
    {
        _editingId = row.Source.Id;
        EditTitle = _text.Format(
            "Settings.Sources.EditNamed",
            new Dictionary<string, object?> { ["source"] = row.Name }
        );
        EditName = row.Source.Name;
        EditKind = row.Source.Kind;
        CanPickKind = false;
        EditServer = row.Source.Server ?? string.Empty;
        EditFilter = row.Source.Filter ?? string.Empty;
        EditHost = row.Source.Host ?? string.Empty;
        EditPort = row.Source.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        EditTncPort = row.Source.TncPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        FillProfiles(id => _settings.Profiles.Any(p => p.Id == id && p.Uses(row.Source)));
        EditUsedBy = row.UsedByText;
        Error = null;
        IsEditing = true;
    }

    /// <summary>Saves the form: a new source, or the edited one in its place. A name defaults to the server's host.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsKiss)
        {
            await SaveKissAsync();
            return;
        }
        var server = EditServer.Trim();
        var name = EditName.Trim().Length > 0 ? EditName.Trim() : SourceSettings.NameFor(server);
        var filter = EditFilter.Trim().Length > 0 ? EditFilter.Trim() : null;
        var source = _editingId is { } id
            ? _settings.SourceList.Single(s => s.Id == id) with
            {
                Name = name,
                Server = server,
                Filter = filter,
            }
            : SourceSettings.AprsIsFor(server, filter) with
            {
                Name = name,
            };
        var sources = _editingId is null
            ? [.. _settings.SourceList, source]
            : _settings.SourceList.Select(s => s.Id == source.Id ? source : s).ToList();
        if (await SaveSettingsAsync(_settings with { Sources = sources }))
        {
            IsEditing = false;
        }
    }

    /// <summary>
    /// Saves a KISS over TCP source and ticks or unticks it on each profile as the form says (F3.1 behaviors 1, 2). A
    /// port that isn't a number is passed on as out of range, so the engine's check words it.
    /// </summary>
    private async Task SaveKissAsync()
    {
        var host = EditHost.Trim();
        int? port = EditPort.Trim().Length == 0 ? null : Number(EditPort);
        int? tncPort = EditTncPort.Trim().Length == 0 ? null : Number(EditTncPort);
        var name = EditName.Trim().Length > 0 ? EditName.Trim() : host;
        var source = _editingId is { } id
            ? _settings.SourceList.Single(s => s.Id == id) with
            {
                Name = name,
                Host = host,
                Port = port,
                TncPort = tncPort,
            }
            : SourceSettings.KissTcpFor(host, port, tncPort) with
            {
                Name = name,
            };
        var ticked = EditProfiles.Where(p => p.IsChecked).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var settings = _settings with
        {
            Sources = _editingId is null
                ? [.. _settings.SourceList, source]
                : [.. _settings.SourceList.Select(s => s.Id == source.Id ? source : s)],
            Profiles =
            [
                .. _settings.Profiles.Select(p =>
                    ticked.Contains(p.Id) == p.SourceIds.Contains(source.Id)
                        ? p
                        : p with
                        {
                            SourceIds = ticked.Contains(p.Id)
                                ? [.. p.SourceIds, source.Id]
                                : [.. p.SourceIds.Where(s => s != source.Id)],
                        }
                ),
            ],
        };
        if (await SaveSettingsAsync(settings))
        {
            IsEditing = false;
        }

        static int Number(string text) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    private void FillProfiles(Func<string, bool> ticked)
    {
        EditProfiles.Clear();
        foreach (var profile in _settings.Profiles)
        {
            EditProfiles.Add(new ProfileChoice(profile.Id, profile.Name) { IsChecked = ticked(profile.Id) });
        }
    }

    /// <summary>Closes the form without saving.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsEditing = false;
        Error = null;
    }

    /// <summary>Deletes at once when no profile uses it; otherwise asks first (behavior 2).</summary>
    internal async Task DeleteAsync(SourceRow row)
    {
        var users = UsersOf(row.Source);
        if (users.Count == 0)
        {
            await RemoveAsync(row.Source);
            return;
        }
        PendingDeleteText = _text.Format(
            "Settings.Sources.ConfirmDelete",
            new Dictionary<string, object?> { ["source"] = row.Name, ["profiles"] = string.Join(", ", users) }
        );
        PendingDelete = row;
    }

    /// <summary>Deletes the source waiting for confirmation and unticks it from its profiles.</summary>
    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is { } row)
        {
            PendingDelete = null;
            await RemoveAsync(row.Source);
        }
    }

    /// <summary>Keeps the source.</summary>
    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    private Task<bool> RemoveAsync(SourceSettings source) =>
        SaveSettingsAsync(
            _settings with
            {
                Sources = [.. _settings.SourceList.Where(s => s.Id != source.Id)],
                Profiles =
                [
                    .. _settings.Profiles.Select(p =>
                        p.Uses(source) ? p with { SourceIds = [.. p.SourceIds.Where(id => id != source.Id)] } : p
                    ),
                ],
            }
        );

    private List<string> UsersOf(SourceSettings source) =>
        [.. _settings.Profiles.Where(p => p.Uses(source)).Select(p => p.Name)];

    private async Task<bool> SaveSettingsAsync(AppSettings settings)
    {
        Error = null;
        try
        {
            await _engine.SaveSettingsAsync(settings);
            Show(settings);
            _saved(settings);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
            return false;
        }
    }
}

/// <summary>One source in Settings › Sources.</summary>
public sealed partial class SourceRow(SourceSettings source, SourcesSectionViewModel section) : ObservableObject
{
    /// <summary>The source.</summary>
    public SourceSettings Source { get; } = source;

    /// <summary>Its name.</summary>
    public string Name => Source.Name;

    /// <summary>Where it connects, for APRS-IS.</summary>
    public string? Server => Source.Server ?? Source.KissEndpoint;

    /// <summary>Its filter text, when it has one.</summary>
    public string? Filter => Source.Filter;

    /// <summary>"APRS-IS".</summary>
    [ObservableProperty]
    public partial string KindText { get; set; } = string.Empty;

    /// <summary>"Used by: Home, Portable", or "Not used by any profile".</summary>
    [ObservableProperty]
    public partial string UsedByText { get; set; } = string.Empty;

    /// <summary>Edit's accessible name.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    /// <summary>Delete's accessible name.</summary>
    [ObservableProperty]
    public partial string DeleteName { get; set; } = string.Empty;

    [RelayCommand]
    private void Edit() => section.Edit(this);

    [RelayCommand]
    private Task DeleteAsync() => section.DeleteAsync(this);
}

/// <summary>A kind Add a source offers, with "coming" for those not built yet.</summary>
public sealed partial class SourceKindChoice(
    SourceKind kind,
    bool isAvailable,
    Localizer text,
    SourcesSectionViewModel section
) : ObservableObject
{
    /// <summary>The kind.</summary>
    public SourceKind Kind { get; } = kind;

    /// <summary>Whether it's the kind in the form; picking it switches the form's fields.</summary>
    public bool IsSelected
    {
        get => section.EditKind == Kind;
        set
        {
            if (value && IsAvailable)
            {
                section.EditKind = Kind;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));

    /// <summary>Whether it can be added now.</summary>
    public bool IsAvailable { get; } = isAvailable;

    /// <summary>"APRS-IS", or "KISS over TCP · coming".</summary>
    [ObservableProperty]
    public partial string Name { get; private set; } = Named(kind, isAvailable, text);

    internal void Retranslate(Localizer text) => Name = Named(Kind, IsAvailable, text);

    private static string Named(SourceKind kind, bool available, Localizer text) =>
        available
            ? text[$"Settings.Sources.Kind.{kind}"]
            : text.Format(
                "Settings.Sources.Coming",
                new Dictionary<string, object?> { ["kind"] = text[$"Settings.Sources.Kind.{kind}"] }
            );
}

/// <summary>A profile in the KISS form's "Use with" list (F3.1 behavior 2).</summary>
public sealed partial class ProfileChoice(string id, string name) : ObservableObject
{
    /// <summary>The profile's id.</summary>
    public string Id { get; } = id;

    /// <summary>The profile's name.</summary>
    public string Name { get; } = name;

    /// <summary>Whether the profile uses the source.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}
