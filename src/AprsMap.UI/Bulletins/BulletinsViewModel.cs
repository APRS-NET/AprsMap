// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Bulletins;

/// <summary>
/// The Bulletins panel (docs/features/F2.5-bulletins.md, behaviors 1–4): the bulletins, announcements, group bulletins
/// and NWS bulletins heard, newest first, each the newest version; new ones marked until the panel is seen; filtered by
/// kind or group and by text; a group can be muted. Watching runs on the UI thread's context.
/// </summary>
public sealed partial class BulletinsViewModel : ObservableObject
{
    /// <summary>The preference the muted groups are kept under (behavior 4).</summary>
    public const string MutedKey = "bulletins.muted";

    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, BulletinRow> _all = new(StringComparer.Ordinal);
    private readonly HashSet<string> _muted = new(StringComparer.OrdinalIgnoreCase);
    private bool _seen;

    internal BulletinsViewModel(IEngineService engine, Localizer text, TimeProvider time)
    {
        _engine = engine;
        _text = text;
        _time = time;
        Filters.Add(new BulletinFilter(null, null, text["Bulletins.Filter.All"]));
        Filters.Add(new BulletinFilter(BulletinKind.Bulletin, null, text["Bulletins.Kind.Bulletin"]));
        Filters.Add(new BulletinFilter(BulletinKind.Announcement, null, text["Bulletins.Kind.Announcement"]));
        Filters.Add(new BulletinFilter(BulletinKind.Nws, null, text["Bulletins.Kind.Nws"]));
        SelectedFilter = Filters[0];
    }

    /// <summary>Where muted groups are kept; null keeps them for this session only.</summary>
    public IPreferenceStore? Preferences { get; set; }

    /// <summary>The bulletins shown, newest first.</summary>
    public ObservableCollection<BulletinRow> Rows { get; } = [];

    /// <summary>All, each kind, then each group heard.</summary>
    public ObservableCollection<BulletinFilter> Filters { get; } = [];

    /// <summary>The filter picked.</summary>
    [ObservableProperty]
    public partial BulletinFilter? SelectedFilter { get; set; }

    /// <summary>Text the bulletins must contain; empty for any.</summary>
    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    /// <summary>How many came since the panel was last seen (behavior 2).</summary>
    [ObservableProperty]
    public partial int NewCount { get; private set; }

    /// <summary>"No bulletins heard yet", or the muted groups; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Note { get; private set; }

    partial void OnSelectedFilterChanged(BulletinFilter? value) => Refilter();

    partial void OnSearchChanged(string value) => Refilter();

    /// <summary>Loads the muted groups kept.</summary>
    internal async Task LoadAsync()
    {
        if (Preferences is not null && await Preferences.LoadAsync(MutedKey) is { Length: > 0 } muted)
        {
            _muted.UnionWith(muted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        Refilter();
    }

    /// <summary>Follows the bulletins until cancelled.</summary>
    internal async Task WatchAsync(CancellationToken cancellationToken)
    {
        await foreach (var bulletin in _engine.WatchBulletinsAsync(cancellationToken))
        {
            Apply(bulletin);
        }
    }

    /// <summary>Takes one bulletin: new, a new version, or gone (behaviors 1, 3).</summary>
    internal void Apply(BulletinView bulletin)
    {
        if (bulletin.Removed)
        {
            _all.Remove(bulletin.Key);
        }
        else
        {
            var isNew = !_seen || !_all.ContainsKey(bulletin.Key) || bulletin.Updated;
            _all[bulletin.Key] = new BulletinRow(bulletin, _text, _time.LocalTimeZone, this) { IsNew = isNew };
            if (bulletin.Group is { } group && Filters.All(f => f.Group != group))
            {
                Filters.Add(new BulletinFilter(null, group, group));
            }
        }
        NewCount = _all.Values.Count(r => r.IsNew && !IsMuted(r));
        Refilter();
    }

    /// <summary>The panel was seen: nothing is new any more (behavior 2).</summary>
    internal void MarkSeen()
    {
        _seen = true;
        foreach (var row in _all.Values)
        {
            row.IsNew = false;
        }
        NewCount = 0;
    }

    /// <summary>Hides a group's bulletins, here and in the count (behavior 4).</summary>
    internal async Task MuteAsync(string group)
    {
        _muted.Add(group);
        await KeepMutedAsync();
        NewCount = _all.Values.Count(r => r.IsNew && !IsMuted(r));
        Refilter();
    }

    /// <summary>Shows every muted group again.</summary>
    [RelayCommand]
    private async Task UnmuteAllAsync()
    {
        _muted.Clear();
        await KeepMutedAsync();
        Refilter();
    }

    private Task KeepMutedAsync() =>
        Preferences?.SaveAsync(MutedKey, string.Join(',', _muted.Order(StringComparer.OrdinalIgnoreCase)))
        ?? Task.CompletedTask;

    private bool IsMuted(BulletinRow row) => row.Bulletin.Group is { } group && _muted.Contains(group);

    private void Refilter()
    {
        var filter = SelectedFilter;
        var search = Search.Trim();
        Rows.Clear();
        foreach (
            var row in _all
                .Values.Where(r => !IsMuted(r))
                .Where(r =>
                    filter is null
                    || filter.Group is { } group && r.Bulletin.Group == group
                    || filter.Group is null && (filter.Kind is null || r.Bulletin.Kind == filter.Kind)
                )
                .Where(r =>
                    search.Length == 0
                    || r.Bulletin.Text.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || r.Bulletin.From.Contains(search, StringComparison.OrdinalIgnoreCase)
                )
                .OrderByDescending(r => r.Bulletin.LastHeard)
        )
        {
            Rows.Add(row);
        }
        Note =
            _all.Count == 0 ? _text["Bulletins.Empty"]
            : _muted.Count > 0
                ? _text.Format(
                    "Bulletins.Muted",
                    new Dictionary<string, object?>
                    {
                        ["groups"] = string.Join(", ", _muted.Order(StringComparer.OrdinalIgnoreCase)),
                    }
                )
            : null;
    }
}

/// <summary>A kind or a group to show; both null for everything.</summary>
public sealed record BulletinFilter(BulletinKind? Kind, string? Group, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>One bulletin in the panel.</summary>
public sealed partial class BulletinRow : ObservableObject
{
    private readonly BulletinsViewModel _owner;

    internal BulletinRow(BulletinView bulletin, Localizer text, TimeZoneInfo zone, BulletinsViewModel owner)
    {
        Bulletin = bulletin;
        _owner = owner;
        KindText = text[$"Bulletins.Kind.{bulletin.Kind}"];
        Heard = text.Format(
            bulletin.Updated ? "Bulletins.HeardUpdated" : "Bulletins.Heard",
            new Dictionary<string, object?>
            {
                ["first"] = TimeZoneInfo.ConvertTime(bulletin.FirstHeard, zone).ToString("g", text.Culture),
                ["last"] = TimeZoneInfo.ConvertTime(bulletin.LastHeard, zone).ToString("t", text.Culture),
            }
        );
    }

    /// <summary>The bulletin.</summary>
    public BulletinView Bulletin { get; }

    /// <summary>"Bulletin", "Announcement" or "NWS".</summary>
    public string KindText { get; }

    /// <summary>The sender.</summary>
    public string From => Bulletin.From;

    /// <summary>The addressee as sent, such as BLN4WX.</summary>
    public string Addressee => Bulletin.Addressee;

    /// <summary>The text.</summary>
    public string Text => Bulletin.Text;

    /// <summary>"First heard 18/10 12:00 · last 12:20", "updated" when it replaced an earlier text.</summary>
    public string Heard { get; }

    /// <summary>Whether it came since the panel was last seen (behavior 2).</summary>
    [ObservableProperty]
    public partial bool IsNew { get; set; }

    /// <summary>Whether it has a group to mute.</summary>
    public bool HasGroup => Bulletin.Group is not null;

    /// <summary>Mutes this bulletin's group (behavior 4).</summary>
    [RelayCommand]
    private Task MuteGroupAsync() => Bulletin.Group is { } group ? _owner.MuteAsync(group) : Task.CompletedTask;
}
