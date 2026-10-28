// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Stations;

/// <summary>How far back a station's track reaches in its details (docs/features/F1.41-persistent-history.md, behavior 8).</summary>
public enum TrackSpan
{
    /// <summary>The live track, as the map draws every station's.</summary>
    Live,

    /// <summary>The last 24 hours, from the packet history.</summary>
    Day,

    /// <summary>The last 7 days, from the packet history.</summary>
    Week,
}

/// <summary>A station's longer track, for the map to draw instead of its live one.</summary>
/// <param name="Name">The station.</param>
/// <param name="Points">Its positions, oldest first.</param>
public sealed record LongTrack(string Name, IReadOnlyList<TrackPoint> Points);

/// <summary>A packet kept in the history, as the details list it (F1.41 behavior 9).</summary>
/// <param name="Time">When it arrived, in the viewer's time.</param>
/// <param name="Raw">The packet as received.</param>
/// <param name="ReceivedAt">When it arrived, for paging back.</param>
public sealed record KeptPacketRow(string Time, string Raw, DateTimeOffset ReceivedAt);

/// <summary>The details' history: a longer track and the packets kept (docs/features/F1.41-persistent-history.md).</summary>
public sealed partial class StationDetailsViewModel
{
    /// <summary>How many kept packets each press loads (behavior 9).</summary>
    internal const int KeptPage = 100;

    private int _trackLoads;

    /// <summary>The track choices, Live first.</summary>
    public IReadOnlyList<TrackSpanOption> TrackSpans =>
        _trackSpans ??= [.. Enum.GetValues<TrackSpan>().Select(s => new TrackSpanOption(s, this))];

    private IReadOnlyList<TrackSpanOption>? _trackSpans;

    /// <summary>How far back the shown station's track reaches; Live again when another station is shown.</summary>
    [ObservableProperty]
    public partial TrackSpan TrackSpan { get; set; }

    /// <summary>The longer track the map draws for the shown station; null for the live tracks.</summary>
    [ObservableProperty]
    public partial LongTrack? LongTrack { get; private set; }

    /// <summary>The packets kept for the shown station, newest first, once asked for.</summary>
    public ObservableCollection<KeptPacketRow> KeptPackets { get; } = [];

    /// <summary>Whether the kept packets have been asked for.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowKeptPacketsCommand))]
    public partial bool KeptPacketsShown { get; private set; }

    /// <summary>Whether there may be older ones to load.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OlderPacketsCommand))]
    public partial bool HasOlderPackets { get; private set; }

    /// <summary>"No packets are kept for this station.", once asked for and there are none; null otherwise.</summary>
    [ObservableProperty]
    public partial string? KeptPacketsNote { get; private set; }

    partial void OnTrackSpanChanged(TrackSpan value)
    {
        foreach (var option in TrackSpans)
        {
            option.Refresh();
        }
        _ = LoadTrackAsync();
    }

    /// <summary>A new station shown: its live track, and none of the last one's packets.</summary>
    private void ResetHistory()
    {
        TrackSpan = TrackSpan.Live;
        LongTrack = null;
        FromHistory = false;
        KeptPackets.Clear();
        KeptPacketsShown = false;
        HasOlderPackets = false;
        KeptPacketsNote = null;
    }

    private async Task LoadTrackAsync()
    {
        var load = ++_trackLoads;
        if (Name is not { } name || TrackSpan == TrackSpan.Live)
        {
            LongTrack = null;
            return;
        }
        var since = _time.GetUtcNow() - (TrackSpan == TrackSpan.Day ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7));
        IReadOnlyList<TrackPoint> points;
        try
        {
            points = await _engine.GetTrackAsync(name, since);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return; // the gateway is away: the live track stays
        }
        if (load == _trackLoads && name == Name)
        {
            LongTrack = new LongTrack(name, points);
        }
    }

    private bool CanShowKeptPackets => !KeptPacketsShown;

    /// <summary>Loads the newest kept packets (behavior 9).</summary>
    [RelayCommand(CanExecute = nameof(CanShowKeptPackets))]
    private Task ShowKeptPacketsAsync()
    {
        KeptPacketsShown = true;
        return LoadKeptAsync(null);
    }

    /// <summary>Loads the next kept packets, older than those shown (behavior 9).</summary>
    [RelayCommand(CanExecute = nameof(HasOlderPackets))]
    private Task OlderPacketsAsync() => LoadKeptAsync(KeptPackets.Count > 0 ? KeptPackets[^1].ReceivedAt : null);

    private async Task LoadKeptAsync(DateTimeOffset? before)
    {
        if (Name is not { } name)
        {
            return;
        }
        IReadOnlyList<PacketView> page;
        try
        {
            page = await _engine.GetStationPacketsAsync(name, before, KeptPage);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return;
        }
        if (name != Name)
        {
            return;
        }
        var zone = _time.LocalTimeZone;
        foreach (var packet in page)
        {
            KeptPackets.Add(
                new KeptPacketRow(
                    TimeZoneInfo.ConvertTime(packet.ReceivedAt, zone).ToString("g", _text.Culture),
                    packet.Raw,
                    packet.ReceivedAt
                )
            );
        }
        HasOlderPackets = page.Count == KeptPage;
        KeptPacketsNote = KeptPackets.Count == 0 ? _text["Details.Kept.None"] : null;
    }

    /// <summary>One of the track choices, as a radio button.</summary>
    public sealed partial class TrackSpanOption(TrackSpan span, StationDetailsViewModel owner) : ObservableObject
    {
        /// <summary>"Live", "24 hours" or "7 days".</summary>
        public string Label => owner._text[$"Details.Track.{span}"];

        /// <summary>Whether this is the span shown; setting it picks it.</summary>
        public bool IsSelected
        {
            get => owner.TrackSpan == span;
            set
            {
                if (value)
                {
                    owner.TrackSpan = span;
                }
            }
        }

        internal void Refresh()
        {
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(Label));
        }
    }
}
