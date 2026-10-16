// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Packets;

/// <summary>
/// The Packets panel (docs/features/F1.8-packet-list.md): the packets the engine holds and receives, newest first,
/// narrowed by filters the engine applies, added in batches, and held while paused.
/// </summary>
public sealed partial class PacketListViewModel : ObservableObject
{
    /// <summary>How many rows the list keeps: what the engine holds (F1.8 behavior 9).</summary>
    public const int MaxRows = 5_000;

    /// <summary>How often new packets are added to the list at most (F1.8 behavior 9).</summary>
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the text filter waits for typing to stop before asking the engine again.</summary>
    public static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(300);

    private readonly IEngineService _engine;
    private readonly TimeProvider _time;
    private readonly Localizer _text;
    private readonly ViewModels.SourceNames? _sources;
    private readonly List<PacketView> _pending = [];
    private CancellationTokenSource? _restart;
    private CancellationTokenSource? _typing;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
    private bool _flushScheduled;
    private bool _clearOnFirst;
    private bool _pausedByScrolling;

    internal PacketListViewModel(
        IEngineService engine,
        TimeProvider time,
        Localizer text,
        ViewModels.SourceNames? sources = null
    )
    {
        _sources = sources;
        _engine = engine;
        _time = time;
        _text = text;
        Kinds = [new KindOption(null, text), .. Enum.GetValues<PacketKind>().Select(k => new KindOption(k, text))];
        SelectedKind = Kinds[0];
        _text.LanguageChanged += (_, _) => Retranslate();
        Retranslate();
    }

    /// <summary>The rows shown, newest first.</summary>
    public PacketRows Rows { get; } = [];

    /// <summary>The type filter's choices: every kind, and all.</summary>
    public IReadOnlyList<KindOption> Kinds { get; }

    /// <summary>Matches source, destination, path or raw text (F1.8 behavior 7).</summary>
    [ObservableProperty]
    public partial string? FilterText { get; set; }

    /// <summary>Only this station's packets; empty for all.</summary>
    [ObservableProperty]
    public partial string? Station { get; set; }

    /// <summary>The type filter.</summary>
    [ObservableProperty]
    public partial KindOption SelectedKind { get; set; }

    /// <summary>Only packets with parse issues.</summary>
    [ObservableProperty]
    public partial bool IssuesOnly { get; set; }

    /// <summary>Whether every row shows its raw text (the column toggle, F1.8 behavior 1).</summary>
    [ObservableProperty]
    public partial bool ShowRaw { get; set; }

    /// <summary>Whether new packets are held back (F1.8 behavior 6).</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>How many packets arrived while paused.</summary>
    [ObservableProperty]
    public partial int NewWhilePaused { get; set; }

    /// <summary>The pause button's text: Pause, or Paused with the count of new packets.</summary>
    [ObservableProperty]
    public partial string PauseText { get; set; } = string.Empty;

    /// <summary>What the empty list says: nothing yet, or nothing matching.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    /// <summary>Whether the list has no rows.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    /// <summary>Something the panel tells the viewer, such as why Save failed; null for nothing.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    /// <summary>Whether any filter is set, for Clear filters.</summary>
    public bool IsFiltered => !CurrentFilter().IsEmpty;

    /// <summary>The filter the engine applies now.</summary>
    internal PacketFilter CurrentFilter() =>
        new(
            string.IsNullOrWhiteSpace(FilterText) ? null : FilterText.Trim(),
            string.IsNullOrWhiteSpace(Station) ? null : Station.Trim(),
            SelectedKind?.Kind is { } kind ? [kind] : null,
            IssuesOnly
        );

    /// <summary>
    /// Streams packets with the current filter until cancelled, starting again whenever the filter changes. Returns when
    /// the stream ends, so the caller can retry it; the list is kept until the new stream's history arrives.
    /// </summary>
    internal async Task WatchAsync(CancellationToken cancellationToken)
    {
        _clearOnFirst = true;
        while (true)
        {
            using var restart = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _restart = restart;
            try
            {
                var first = true;
                await foreach (var packet in _engine.WatchPacketsAsync(CurrentFilter(), restart.Token))
                {
                    if (first && _clearOnFirst)
                    {
                        Clear();
                    }
                    first = false;
                    _clearOnFirst = false;
                    Receive(packet);
                }
                _clearOnFirst = true;
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The filter changed: ask again at once.
            }
            finally
            {
                _restart = null;
            }
        }
    }

    /// <summary>Takes a packet from the stream: shown within <see cref="BatchInterval"/>, or held while paused.</summary>
    internal void Receive(PacketView packet)
    {
        _pending.Add(packet);
        if (_pending.Count > MaxRows)
        {
            _pending.RemoveRange(0, _pending.Count - MaxRows);
        }
        if (IsPaused)
        {
            NewWhilePaused = Math.Min(NewWhilePaused + 1, MaxRows);
            return;
        }
        if (_time.GetUtcNow() - _lastFlush >= BatchInterval)
        {
            Flush();
        }
        else if (!_flushScheduled)
        {
            _flushScheduled = true;
            _ = FlushLaterAsync();
        }
    }

    /// <summary>Freezes the list, or lets the held packets in (F1.8 behavior 6).</summary>
    [RelayCommand]
    public void TogglePause()
    {
        _pausedByScrolling = false;
        SetPaused(!IsPaused);
    }

    /// <summary>
    /// The list was scrolled (F1.8 behavior 6): away from the newest packets pauses it, and back to them resumes it, unless
    /// the viewer paused it with the button.
    /// </summary>
    internal void Scrolled(bool atTop)
    {
        if (!atTop && !IsPaused)
        {
            _pausedByScrolling = true;
            SetPaused(true);
        }
        else if (atTop && IsPaused && _pausedByScrolling)
        {
            _pausedByScrolling = false;
            SetPaused(false);
        }
    }

    /// <summary>Shows only <paramref name="station"/>'s packets.</summary>
    [RelayCommand]
    public void OnlyStation(string? station) => Station = station;

    /// <summary>Removes every filter.</summary>
    [RelayCommand]
    public void ClearFilters()
    {
        FilterText = null;
        Station = null;
        IssuesOnly = false;
        SelectedKind = Kinds[0];
    }

    /// <summary>The raw lines of <paramref name="rows"/>, oldest first, for Copy (F1.8 behavior 8).</summary>
    public static string CopyText(IEnumerable<PacketRow> rows) =>
        string.Join(Environment.NewLine, rows.OrderBy(r => r.Packet.ReceivedAt).Select(r => r.Raw));

    /// <summary>Every row shown, oldest first, in <paramref name="exporter"/>'s format, for Save (F1.8 behavior 10).</summary>
    public string SaveText(Export.PacketExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        return exporter.Export([.. Rows.Reverse().Select(r => r.Packet)], id => _sources?.NameOf(id) ?? id);
    }

    /// <summary>The name Save suggests: the packets' date and time, with the format's extension.</summary>
    public string SaveName(Export.PacketExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"packets-{TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone):yyyyMMdd-HHmmss}.{exporter.Extension}"
        );
    }

    /// <summary>Empties the list (Reset the app).</summary>
    internal void Clear()
    {
        _pending.Clear();
        Rows.Clear();
        NewWhilePaused = 0;
        UpdateEmpty();
    }

    partial void OnFilterTextChanged(string? value) => _ = RestartAfterTypingAsync();

    partial void OnStationChanged(string? value) => Restart();

    partial void OnSelectedKindChanged(KindOption value) => Restart();

    partial void OnIssuesOnlyChanged(bool value) => Restart();

    partial void OnNewWhilePausedChanged(int value) => UpdatePauseText();

    partial void OnIsPausedChanged(bool value) => UpdatePauseText();

    private void SetPaused(bool paused)
    {
        var resuming = IsPaused && !paused;
        IsPaused = paused;
        if (!paused)
        {
            NewWhilePaused = 0;
            Flush();
        }
        if (resuming)
        {
            Resumed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the list resumes: the panel scrolls back to the newest packets (F1.8 behavior 6).</summary>
    public event EventHandler? Resumed;

    /// <summary>The filter changed: the list empties and the engine sends its matching history again.</summary>
    private void Restart()
    {
        OnPropertyChanged(nameof(IsFiltered));
        Clear();
        _clearOnFirst = false;
        _restart?.Cancel();
    }

    private async Task RestartAfterTypingAsync()
    {
        _typing?.Cancel();
        using var typing = new CancellationTokenSource();
        _typing = typing;
        try
        {
            await Task.Delay(TypingDelay, _time, typing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_typing, typing))
            {
                _typing = null;
            }
        }
        Restart();
    }

    private async Task FlushLaterAsync()
    {
        try
        {
            await Task.Delay(BatchInterval, _time);
        }
        finally
        {
            _flushScheduled = false;
        }
        if (!IsPaused)
        {
            Flush();
        }
    }

    /// <summary>Adds the waiting packets to the top of the list, newest first, and drops the oldest past the cap.</summary>
    internal void Flush()
    {
        _lastFlush = _time.GetUtcNow();
        if (_pending.Count == 0)
        {
            return;
        }
        var zone = _time.LocalTimeZone;
        Rows.AddNewest([.. _pending.Select(p => new PacketRow(p, _text, zone, _sources))], MaxRows);
        _pending.Clear();
        UpdateEmpty();
    }

    private void UpdateEmpty()
    {
        IsEmpty = Rows.Count == 0;
        EmptyText = _text[IsFiltered ? "Packets.NoneMatching" : "Packets.None"];
    }

    private void UpdatePauseText() =>
        PauseText = IsPaused
            ? _text.Format("Packets.Paused", new Dictionary<string, object?> { ["count"] = NewWhilePaused })
            : _text["Packets.Pause"];

    private void Retranslate()
    {
        foreach (var kind in Kinds)
        {
            kind.Retranslate(_text);
        }
        UpdatePauseText();
        UpdateEmpty();
    }

    /// <summary>One choice of the type filter.</summary>
    public sealed partial class KindOption : ObservableObject
    {
        internal KindOption(PacketKind? kind, Localizer text)
        {
            Kind = kind;
            Retranslate(text);
        }

        /// <summary>The kind; null for all.</summary>
        public PacketKind? Kind { get; }

        /// <summary>Its name in the viewer's language.</summary>
        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        internal void Retranslate(Localizer text) => Name = text[$"Packets.Kind.{Kind?.ToString() ?? "All"}"];

        /// <inheritdoc />
        public override string ToString() => Name;
    }
}
