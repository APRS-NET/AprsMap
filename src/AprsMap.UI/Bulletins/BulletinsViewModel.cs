// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Bulletins;

/// <summary>
/// The Bulletins panel (docs/features/F2.5-bulletins.md, behaviors 1–4): the bulletins, announcements, group bulletins
/// and NWS bulletins heard, newest first, each the newest version; new ones marked until the panel is seen; filtered by
/// kind or group and by text; a group can be muted. With transmitting allowed it also posts this station's own
/// bulletins (behaviors 5–7) and joins, leaves and posts to ANSRVR groups (behaviors 8–10). Watching runs on the UI
/// thread's context.
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

    private readonly Func<EngineStatus?> _status;

    internal BulletinsViewModel(
        IEngineService engine,
        Localizer text,
        TimeProvider time,
        Func<EngineStatus?>? status = null
    )
    {
        _engine = engine;
        _text = text;
        _time = time;
        _status = status ?? (() => null);
        PostKinds =
        [
            new BeaconChoice<BulletinKind>(BulletinKind.Bulletin, text["Bulletins.Kind.Bulletin"]),
            new BeaconChoice<BulletinKind>(BulletinKind.Announcement, text["Bulletins.Kind.Announcement"]),
        ];
        PostKind = PostKinds[0];
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

    /// <summary>Loads the muted groups kept, and the ANSRVR groups joined.</summary>
    internal async Task LoadAsync()
    {
        if (Preferences is not null && await Preferences.LoadAsync(MutedKey) is { Length: > 0 } muted)
        {
            _muted.UnionWith(muted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        Refilter();
        await LoadGroupsAsync();
    }

    // Posting (behaviors 5–7).

    /// <summary>Whether this station may transmit here: posting and groups show only then (behavior 7).</summary>
    [ObservableProperty]
    public partial bool CanPost { get; set; }

    /// <summary>Whether the New bulletin form is open.</summary>
    [ObservableProperty]
    public partial bool IsComposing { get; private set; }

    /// <summary>Bulletin or announcement.</summary>
    public IReadOnlyList<BeaconChoice<BulletinKind>> PostKinds { get; }

    /// <summary>The kind picked.</summary>
    [ObservableProperty]
    public partial BeaconChoice<BulletinKind> PostKind { get; set; }

    /// <summary>The id: a digit for a bulletin, a letter for an announcement.</summary>
    [ObservableProperty]
    public partial string PostId { get; set; } = "0";

    /// <summary>The group, or empty for none.</summary>
    [ObservableProperty]
    public partial string PostGroup { get; set; } = string.Empty;

    /// <summary>The text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PostCount))]
    public partial string PostText { get; set; } = string.Empty;

    /// <summary>"23/67": the characters used of those a bulletin can carry.</summary>
    public string PostCount => $"{PostText.Trim().Length}/{BulletinPost.MaxText}";

    /// <summary>Send via: the sources that aren't RX only, each ticked to send on it.</summary>
    public ObservableCollection<ViaChoice> PostVia { get; } = [];

    /// <summary>Why the last post or group action didn't go through; null when it did.</summary>
    [ObservableProperty]
    public partial string? PostError { get; private set; }

    partial void OnPostKindChanged(BeaconChoice<BulletinKind> value) =>
        PostId = value.Value == BulletinKind.Announcement ? "A" : "0";

    /// <summary>Opens the form for a new bulletin, sent on every source that isn't RX only.</summary>
    [RelayCommand]
    private void NewBulletin()
    {
        PostKind = PostKinds[0];
        PostGroup = string.Empty;
        PostText = string.Empty;
        FillVia(null);
        PostError = null;
        IsComposing = true;
    }

    /// <summary>Opens the form on one of ours, to change its text (behavior 6: its schedule starts again).</summary>
    internal void Edit(BulletinView bulletin)
    {
        PostKind = PostKinds.First(k => k.Value == bulletin.Kind);
        PostId = bulletin.Id ?? PostId;
        PostGroup = bulletin.Group ?? string.Empty;
        PostText = bulletin.Text;
        FillVia(null);
        PostError = null;
        IsComposing = true;
    }

    private void FillVia(IReadOnlyList<string>? ticked)
    {
        PostVia.Clear();
        foreach (var source in (_status()?.Sources ?? []).Where(s => s.MaySend))
        {
            PostVia.Add(new ViaChoice(source.Id, source.Name) { IsChecked = ticked?.Contains(source.Id) ?? true });
        }
    }

    /// <summary>Closes the form without posting.</summary>
    [RelayCommand]
    private void CancelPost()
    {
        IsComposing = false;
        PostError = null;
    }

    /// <summary>Posts the form's bulletin; every source ticked is the same as all of them.</summary>
    [RelayCommand]
    private async Task PostAsync()
    {
        var via = PostVia.All(v => v.IsChecked) ? null : PostVia.Where(v => v.IsChecked).Select(v => v.Id).ToList();
        try
        {
            await _engine.PostBulletinAsync(
                new BulletinPost(PostKind.Value, PostId, PostText, PostGroup.Length == 0 ? null : PostGroup, via)
            );
            IsComposing = false;
            PostError = null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            PostError = _text.Describe(ex);
        }
    }

    /// <summary>Stops one of ours repeating (behavior 6).</summary>
    internal async Task StopAsync(BulletinView bulletin)
    {
        try
        {
            await _engine.StopBulletinAsync(bulletin.Addressee);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            PostError = _text.Describe(ex);
        }
    }

    // ANSRVR groups (behaviors 8–10).

    /// <summary>The groups joined.</summary>
    public ObservableCollection<GroupRow> Groups { get; } = [];

    /// <summary>Whether any group is joined.</summary>
    [ObservableProperty]
    public partial bool HasGroups { get; private set; }

    /// <summary>The group to join, as typed.</summary>
    [ObservableProperty]
    public partial string JoinName { get; set; } = string.Empty;

    /// <summary>The joined group to post to.</summary>
    [ObservableProperty]
    public partial GroupRow? PostTo { get; set; }

    /// <summary>The text to post to <see cref="PostTo"/>.</summary>
    [ObservableProperty]
    public partial string GroupText { get; set; } = string.Empty;

    private async Task LoadGroupsAsync()
    {
        IReadOnlyList<string> groups;
        try
        {
            groups = await _engine.GetGroupsAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return;
        }
        var picked = PostTo?.Name;
        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(new GroupRow(group, this));
        }
        HasGroups = Groups.Count > 0;
        PostTo = Groups.FirstOrDefault(g => g.Name == picked) ?? Groups.FirstOrDefault();
    }

    /// <summary>Joins the group typed (behavior 8).</summary>
    [RelayCommand]
    private async Task JoinAsync()
    {
        if (await GroupActionAsync(() => _engine.JoinGroupAsync(JoinName)))
        {
            JoinName = string.Empty;
        }
    }

    /// <summary>Leaves a group (behavior 9).</summary>
    internal Task LeaveAsync(string group) => GroupActionAsync(() => _engine.LeaveGroupAsync(group));

    /// <summary>Posts to the group picked (behavior 9).</summary>
    [RelayCommand]
    private async Task PostToGroupAsync()
    {
        if (PostTo is { } group && await GroupActionAsync(() => _engine.PostToGroupAsync(group.Name, GroupText)))
        {
            GroupText = string.Empty;
        }
    }

    private async Task<bool> GroupActionAsync(Func<Task> act)
    {
        try
        {
            await act();
            PostError = null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            PostError = _text.Describe(ex);
            return false;
        }
        await LoadGroupsAsync();
        return true;
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
        MineText =
            !bulletin.Mine ? null
            : bulletin.NextSend is { } next
                ? text.Format(
                    "Bulletins.MineNext",
                    new Dictionary<string, object?>
                    {
                        ["time"] = TimeZoneInfo.ConvertTime(next, zone).ToString("t", text.Culture),
                    }
                )
            : text["Bulletins.MineDone"];
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
    public bool HasGroup => Bulletin.Group is not null && !Bulletin.Mine;

    /// <summary>"Yours · next send 12:40" for one of ours (behavior 6); null otherwise.</summary>
    public string? MineText { get; }

    /// <summary>Whether it's ours and still repeating, so Edit and Stop apply.</summary>
    public bool CanStop => Bulletin.Mine && Bulletin.NextSend is not null;

    /// <summary>Opens the form on it to change its text.</summary>
    [RelayCommand]
    private void Edit() => _owner.Edit(Bulletin);

    /// <summary>Stops it repeating.</summary>
    [RelayCommand]
    private Task StopAsync() => _owner.StopAsync(Bulletin);

    /// <summary>Mutes this bulletin's group (behavior 4).</summary>
    [RelayCommand]
    private Task MuteGroupAsync() => Bulletin.Group is { } group ? _owner.MuteAsync(group) : Task.CompletedTask;
}

/// <summary>One ANSRVR group joined.</summary>
public sealed partial class GroupRow(string name, BulletinsViewModel owner)
{
    /// <summary>The group's name.</summary>
    public string Name { get; } = name;

    /// <summary>Leaves the group.</summary>
    [RelayCommand]
    private Task LeaveAsync() => owner.LeaveAsync(Name);

    /// <inheritdoc />
    public override string ToString() => Name;
}
