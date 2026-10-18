// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Traffic;

/// <summary>What the console shows (docs/features/F1.28-source-console.md, behavior 5).</summary>
public enum ConsoleFilter
{
    /// <summary>Everything.</summary>
    All,

    /// <summary>Not APRS, other port, dropped, unframed and failed setup steps.</summary>
    Problems,

    /// <summary>Packets in and out only.</summary>
    Packets,
}

/// <summary>
/// The Source console (docs/features/F1.28-source-console.md): one source's traffic, newest at the bottom like a
/// terminal, paused while scrolled up, with a command line for a serial TNC (docs/features/F3.2-built-in-tncs.md,
/// behaviors 8–11). Watching runs on the UI thread's context, so the lines are only touched there.
/// </summary>
public sealed partial class SourceConsoleViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 500;
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly TimeProvider _time;
    private readonly Func<EngineStatus?> _status;
    private readonly List<ConsoleLine> _all = [];
    private readonly List<ConsoleLine> _held = [];
    private CancellationTokenSource? _watch;

    internal SourceConsoleViewModel(
        IEngineService engine,
        Localizer text,
        TimeProvider time,
        Func<EngineStatus?> status
    )
    {
        _engine = engine;
        _text = text;
        _time = time;
        _status = status;
        Filters =
        [
            new(ConsoleFilter.All, text["Console.Filter.All"]),
            new(ConsoleFilter.Problems, text["Console.Filter.Problems"]),
            new(ConsoleFilter.Packets, text["Console.Filter.Packets"]),
        ];
        SelectedFilter = Filters[0];
    }

    /// <summary>The active profile's sources.</summary>
    public ObservableCollection<ConsoleSource> Sources { get; } = [];

    /// <summary>The source watched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanType))]
    public partial ConsoleSource? SelectedSource { get; set; }

    /// <summary>The lines shown: the filter applied, oldest first.</summary>
    public ObservableCollection<ConsoleLine> Lines { get; } = [];

    /// <summary>The filter choices.</summary>
    public IReadOnlyList<ConsoleFilterChoice> Filters { get; }

    /// <summary>The filter picked.</summary>
    [ObservableProperty]
    public partial ConsoleFilterChoice? SelectedFilter { get; set; }

    /// <summary>Text the lines must contain; empty for any.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>Whether each line shows its bytes in hex beside the text (behavior 3).</summary>
    [ObservableProperty]
    public partial bool ShowHex { get; set; }

    /// <summary>Whether new lines wait, because the viewer scrolled up (behavior 4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PausedText))]
    public partial bool IsPaused { get; private set; }

    /// <summary>"12 new" while paused.</summary>
    public string? PausedText =>
        IsPaused ? _text.Format("Console.NewLines", new Dictionary<string, object?> { ["count"] = _held.Count }) : null;

    /// <summary>Why the console can't show anything (a gateway viewer, behavior 11), or null.</summary>
    [ObservableProperty]
    public partial string? Unavailable { get; private set; }

    /// <summary>Why the last command failed, in words; null when it didn't.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Whether the command line is offered: a KISS over serial source (behavior 9).</summary>
    public bool CanType => SelectedSource?.Kind == SourceKind.KissSerial;

    /// <summary>Whether the command line is open, which puts the TNC in manual mode (F3.2 behavior 9).</summary>
    [ObservableProperty]
    public partial bool IsCommandLineOpen { get; private set; }

    /// <summary>The command to send.</summary>
    [ObservableProperty]
    public partial string CommandText { get; set; } = string.Empty;

    /// <summary>Whether <see cref="CommandText"/> is hex bytes rather than a line.</summary>
    [ObservableProperty]
    public partial bool SendBytes { get; set; }

    partial void OnSelectedSourceChanged(ConsoleSource? value)
    {
        if (IsCommandLineOpen && value?.Id != _manualSource)
        {
            _ = CloseCommandLineAsync();
        }
        Watch(value?.Id);
    }

    partial void OnSelectedFilterChanged(ConsoleFilterChoice? value) => Refilter();

    partial void OnFilterTextChanged(string value) => Refilter();

    partial void OnShowHexChanged(bool value)
    {
        foreach (var line in _all)
        {
            line.ShowHex = value;
        }
    }

    /// <summary>Shows the active profile's sources; keeps the one watched when it's still there.</summary>
    internal void ShowSources(IReadOnlyList<SourceStatus> sources)
    {
        if (Sources.Select(s => (s.Id, s.Name)).SequenceEqual(sources.Select(s => (s.Id, s.Name))))
        {
            return;
        }
        var keep = SelectedSource?.Id;
        Sources.Clear();
        foreach (var source in sources)
        {
            Sources.Add(new ConsoleSource(source.Id, source.Name, source.Kind));
        }
        SelectedSource = Sources.FirstOrDefault(s => s.Id == keep) ?? Sources.FirstOrDefault();
    }

    /// <summary>Opens on <paramref name="sourceId"/>, with the command line open when asked (F3.2 behavior 8).</summary>
    internal async Task OpenAsync(string sourceId, bool commandLine)
    {
        if (_status()?.Sources is { } sources)
        {
            ShowSources(sources);
        }
        SelectedSource = Sources.FirstOrDefault(s => s.Id == sourceId) ?? SelectedSource;
        if (commandLine && CanType && !IsCommandLineOpen)
        {
            await OpenCommandLineAsync();
        }
    }

    private void Watch(string? sourceId)
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
        _all.Clear();
        _held.Clear();
        Lines.Clear();
        IsPaused = false;
        Unavailable = null;
        if (sourceId is null)
        {
            return;
        }
        _watch = new CancellationTokenSource();
        _ = WatchAsync(sourceId, _watch.Token);
    }

    private async Task WatchAsync(string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in _engine.WatchSourceTrafficAsync(sourceId, cancellationToken))
            {
                Add(entry);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Another source was picked.
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Unavailable = _text.Describe(ex);
        }
    }

    /// <summary>Adds one entry: to the line before when it's more of the same prompt (behavior 2), else a new line.</summary>
    internal void Add(TrafficEntry entry)
    {
        var last = _all.Count > 0 ? _all[^1] : null;
        if (
            last is { Entry: { Kind: TrafficKind.Unframed, EndsLine: false } before }
            && entry.Kind == TrafficKind.Unframed
            && entry.Direction == before.Direction
        )
        {
            last.Append(entry);
            return;
        }
        var line = new ConsoleLine(entry, _text, _time.LocalTimeZone) { ShowHex = ShowHex };
        _all.Add(line);
        if (_all.Count > MaxLines)
        {
            var gone = _all[0];
            _all.RemoveAt(0);
            Lines.Remove(gone);
            _held.Remove(gone);
        }
        if (!Shows(line))
        {
            return;
        }
        if (IsPaused)
        {
            _held.Add(line);
            OnPropertyChanged(nameof(PausedText));
        }
        else
        {
            Lines.Add(line);
        }
    }

    /// <summary>The viewer scrolled: away from the bottom pauses, back to it catches up (behavior 4).</summary>
    internal void Scrolled(bool atBottom)
    {
        if (!atBottom)
        {
            IsPaused = true;
            return;
        }
        if (!IsPaused)
        {
            return;
        }
        foreach (var line in _held)
        {
            Lines.Add(line);
        }
        _held.Clear();
        IsPaused = false;
    }

    private bool Shows(ConsoleLine line) =>
        (SelectedFilter?.Filter ?? ConsoleFilter.All) switch
        {
            ConsoleFilter.Problems => line.Entry.IsProblem,
            ConsoleFilter.Packets => line.Entry.Kind is TrafficKind.Packet or TrafficKind.Sent,
            _ => true,
        }
        && (
            FilterText.Trim().Length == 0
            || line.Content.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase)
        );

    private void Refilter()
    {
        Lines.Clear();
        _held.Clear();
        IsPaused = false;
        foreach (var line in _all.Where(Shows))
        {
            Lines.Add(line);
        }
    }

    /// <summary>The lines shown, with times, kinds and content, for Copy (behavior 3).</summary>
    public static string CopyText(IEnumerable<ConsoleLine> lines) =>
        string.Join(Environment.NewLine, lines.Select(l => $"{l.Time} {l.DirectionText} {l.KindText} {l.Content}"));

    private string? _manualSource;

    /// <summary>Opens or closes the command line (F3.2 behavior 9).</summary>
    [RelayCommand]
    private Task ToggleCommandLineAsync() => IsCommandLineOpen ? CloseCommandLineAsync() : OpenCommandLineAsync();

    private async Task OpenCommandLineAsync()
    {
        if (SelectedSource is not { } source || !CanType)
        {
            return;
        }
        if (await TryAsync(() => _engine.SetTncManualAsync(source.Id, true)))
        {
            _manualSource = source.Id;
            IsCommandLineOpen = true;
        }
    }

    private async Task CloseCommandLineAsync()
    {
        if (_manualSource is { } id)
        {
            await TryAsync(() => _engine.SetTncManualAsync(id, false));
        }
        _manualSource = null;
        IsCommandLineOpen = false;
    }

    /// <summary>Sends the typed line or bytes (F3.2 behaviors 10, 11).</summary>
    [RelayCommand]
    private async Task SendAsync()
    {
        if (_manualSource is not { } id || CommandText.Length == 0)
        {
            return;
        }
        var command = new TncCommand(SendBytes ? TncCommandKind.Bytes : TncCommandKind.Line, CommandText);
        if (await TryAsync(() => _engine.SendTncCommandAsync(id, command)))
        {
            CommandText = string.Empty;
        }
    }

    /// <summary>Runs the TNC type's script into KISS (F3.2 behavior 10.1).</summary>
    [RelayCommand]
    private Task EnterKissAsync() =>
        SelectedSource is { } source
            ? TryAsync(() => _engine.SendTncCommandAsync(source.Id, new TncCommand(TncCommandKind.EnterKiss)))
            : Task.CompletedTask;

    /// <summary>Runs the TNC type's script out of KISS, or sends the KISS exit frame.</summary>
    [RelayCommand]
    private Task LeaveKissAsync() =>
        SelectedSource is { } source
            ? TryAsync(() => _engine.SendTncCommandAsync(source.Id, new TncCommand(TncCommandKind.LeaveKiss)))
            : Task.CompletedTask;

    private async Task<bool> TryAsync(Func<Task> call)
    {
        Error = null;
        try
        {
            await call();
            return true;
        }
        catch (Exception ex)
            when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Error = _text.Describe(ex);
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
    }

    /// <summary>Stops watching; the command line closes, ending manual mode.</summary>
    internal async Task CloseAsync()
    {
        await CloseCommandLineAsync();
        Watch(null);
    }
}

/// <summary>A source in the console's picker.</summary>
public sealed record ConsoleSource(string Id, string Name, SourceKind Kind)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A filter choice.</summary>
public sealed record ConsoleFilterChoice(ConsoleFilter Filter, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>One console line (F1.28 behavior 2).</summary>
public sealed partial class ConsoleLine : ObservableObject
{
    private readonly StringBuilder _text = new();
    private readonly List<byte> _bytes = [];

    internal ConsoleLine(TrafficEntry entry, Localizer text, TimeZoneInfo zone)
    {
        Entry = entry;
        Time = TimeZoneInfo.ConvertTime(entry.At, zone).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        DirectionText = text[entry.Direction == TrafficDirection.In ? "Console.In" : "Console.Out"];
        KindText = text[$"Console.Kind.{entry.Kind}"];
        _text.Append(entry.Text);
        _bytes.AddRange(entry.Bytes ?? []);
        Detail = entry.Detail;
        Refresh();
    }

    /// <summary>The first entry of the line.</summary>
    public TrafficEntry Entry { get; private set; }

    /// <summary>"12:34:56.789", local time.</summary>
    public string Time { get; }

    /// <summary>"In" or "Out".</summary>
    public string DirectionText { get; }

    /// <summary>The kind in words.</summary>
    public string KindText { get; }

    /// <summary>Why a frame was dropped, or the setup step; null otherwise.</summary>
    public string? Detail { get; }

    /// <summary>Whether it's a problem, shown in the warning colour (behavior 2).</summary>
    public bool IsProblem => Entry.IsProblem;

    /// <summary>Whether it went out.</summary>
    public bool IsOut => Entry.Direction == TrafficDirection.Out;

    /// <summary>The text, or the bytes in hex when there's no text.</summary>
    [ObservableProperty]
    public partial string Content { get; private set; } = string.Empty;

    /// <summary>The bytes in hex.</summary>
    [ObservableProperty]
    public partial string Hex { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Hex"/> shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHex))]
    public partial bool ShowHex { get; set; }

    /// <summary>Whether the hex shows: asked for, and there are bytes and text beside them.</summary>
    public bool ShowsHex => ShowHex && Hex.Length > 0 && _text.Length > 0;

    /// <summary>Adds more of a prompt's text: what the TNC sent next on the same line.</summary>
    internal void Append(TrafficEntry entry)
    {
        _text.Append(entry.Text);
        _bytes.AddRange(entry.Bytes ?? []);
        Entry = Entry with { EndsLine = entry.EndsLine };
        Refresh();
    }

    private void Refresh()
    {
        Hex = Convert.ToHexString([.. _bytes]) is { Length: > 0 } hex
            ? string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)))
            : string.Empty;
        Content = _text.Length > 0 ? _text.ToString().TrimEnd('\r', '\n') : Hex;
        OnPropertyChanged(nameof(ShowsHex));
    }
}
