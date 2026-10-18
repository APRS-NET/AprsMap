// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsNet.Corpus;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// Packet capture (docs/features/F1.27-packet-capture.md): the packets APRS.NET reported issues for, didn't decode or
/// only partly recognised, written byte for byte in the corpus line format to one file a day in <c>capture/</c>, with
/// their reasons in a JSON-lines file beside it. Repeats within an hour are counted, not written; writing happens in the
/// background and never slows receiving; the oldest days go first past the size and age limits.
/// </summary>
internal sealed class PacketCapture : IAsyncDisposable
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(1);
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastWritten = new(StringComparer.Ordinal);
    private readonly Channel<Captured> _queue = Channel.CreateBounded<Captured>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true }
    );
    private readonly Task _writer;
    private readonly SemaphoreSlim _files = new(1, 1);
    private CaptureSettings _settings = CaptureSettings.Off;
    private DateOnly _today;
    private int _writtenToday;
    private int _repeatsToday;
    private long _dropped;
    private int _pending;

    /// <param name="folder">The <c>capture</c> folder.</param>
    /// <param name="time">For arrival times and days.</param>
    public PacketCapture(string folder, TimeProvider time)
    {
        _folder = folder;
        _time = time;
        _today = Day(time.GetUtcNow());
        _writtenToday = CountDay(_today);
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>The settings in force.</summary>
    public CaptureSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
        set
        {
            lock (_lock)
            {
                _settings = value;
            }
        }
    }

    /// <summary>How many packets were written today, while capture is on; null while it's off (behavior 6).</summary>
    public int? CapturedToday
    {
        get
        {
            lock (_lock)
            {
                RollDay(_time.GetUtcNow());
                return _settings.On ? _writtenToday : null;
            }
        }
    }

    /// <summary>Lines dropped because the disk couldn't keep up (behavior 5).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Why <paramref name="packet"/> is captured (behavior 1), or nothing when it isn't.</summary>
    internal static IReadOnlyList<EngineError> ReasonsOf(AprsPacket packet, bool userDefined)
    {
        var reasons = new List<EngineError>(PacketSummarizer.Issues(packet));
        if (packet.Outcome == ParseOutcome.Unsupported)
        {
            reasons.Add(
                new EngineError(EngineErrorCodes.CaptureUnsupported, "APRS.NET doesn't support this data type.")
            );
        }
        else if (packet.Type == PacketType.Unknown)
        {
            reasons.Add(new EngineError(EngineErrorCodes.CaptureUnknownType, "APRS.NET doesn't know this data type."));
        }
        foreach (var part in packet.Unrecognized)
        {
            // A user-defined packet's data is never decoded, so it's only captured when asked for (decision 7).
            if (part.Code == UnrecognizedPartCode.UserDefinedData && !userDefined)
            {
                continue;
            }
            reasons.Add(
                new EngineError(
                    EngineErrorCodes.AprsUnrecognizedPrefix + AprsErrors.Kebab(part.Code.ToString()),
                    part.Code.Describe(),
                    new Dictionary<string, string>
                    {
                        [EngineErrorArgs.Offset] = part.Offset.ToString(CultureInfo.InvariantCulture),
                    }
                )
            );
        }
        return reasons;
    }

    /// <summary>
    /// Records <paramref name="packet"/> when capture is on and it's unexpected, unless the same station, reasons and
    /// type were written in the last hour (behavior 3). Never blocks.
    /// </summary>
    public void Consider(AprsPacket packet, string? sourceId)
    {
        CaptureSettings settings;
        lock (_lock)
        {
            settings = _settings;
        }
        if (!settings.On)
        {
            return;
        }
        var reasons = ReasonsOf(packet, settings.UserDefined);
        if (reasons.Count == 0)
        {
            return;
        }
        var now = _time.GetUtcNow();
        var station = packet.Source?.Value ?? string.Empty;
        var key =
            $"{station}|{string.Join(',', reasons.Select(r => r.Code).Distinct().Order(StringComparer.Ordinal))}|{packet.Type}";
        lock (_lock)
        {
            RollDay(now);
            if (_lastWritten.TryGetValue(key, out var last) && now - last < RepeatWindow)
            {
                _repeatsToday++;
                return;
            }
            _lastWritten[key] = now;
            if (_lastWritten.Count > 10_000)
            {
                foreach (var old in _lastWritten.Where(p => now - p.Value >= RepeatWindow).Select(p => p.Key).ToList())
                {
                    _lastWritten.Remove(old);
                }
            }
        }
        var captured = new Captured(new CorpusLine(now, packet.Raw.Span), sourceId, station, reasons);
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(captured))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
        }
    }

    private void RollDay(DateTimeOffset now)
    {
        var day = Day(now);
        if (day != _today)
        {
            _today = day;
            _writtenToday = 0;
            _repeatsToday = 0;
        }
    }

    private async Task WriteAsync()
    {
        await foreach (var captured in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _files.WaitAsync().ConfigureAwait(false);
            try
            {
                var day = Day(captured.Line.ReceivedAt);
                Directory.CreateDirectory(_folder);
                var log = LogPath(day);
                var isNewDay = !File.Exists(log);
                using (var writer = CorpusFile.OpenAppend(log))
                {
                    writer.Write(captured.Line);
                }
                await File.AppendAllTextAsync(
                        MetaPath(day),
                        JsonSerializer.Serialize(Meta.Of(captured), CaptureJson.Default.Meta) + "\n",
                        Encoding.UTF8
                    )
                    .ConfigureAwait(false);
                lock (_lock)
                {
                    if (day == _today)
                    {
                        _writtenToday++;
                    }
                }
                if (isNewDay || _writtenToday % 100 == 0)
                {
                    Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _dropped);
            }
            finally
            {
                _files.Release();
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    /// <summary>Deletes the oldest days past the age limit, then past the size limit (behavior 5).</summary>
    private void Trim()
    {
        var settings = Settings;
        var days = DaysKept().ToList(); // newest first
        var oldest = _today.AddDays(-Math.Max(1, settings.Days) + 1);
        foreach (var day in days.Where(d => d < oldest).ToList())
        {
            DeleteDay(day);
            days.Remove(day);
        }
        var limit = (long)Math.Max(1, settings.MaxMegabytes) * 1024 * 1024;
        while (days.Count > 1 && days.Sum(Size) > limit)
        {
            DeleteDay(days[^1]);
            days.RemoveAt(days.Count - 1);
        }
    }

    private long Size(DateOnly day) =>
        (File.Exists(LogPath(day)) ? new FileInfo(LogPath(day)).Length : 0)
        + (File.Exists(MetaPath(day)) ? new FileInfo(MetaPath(day)).Length : 0);

    private void DeleteDay(DateOnly day)
    {
        File.Delete(LogPath(day));
        File.Delete(MetaPath(day));
    }

    /// <summary>The days with a file, newest first.</summary>
    private IEnumerable<DateOnly> DaysKept() =>
        Directory.Exists(_folder)
            ? Directory
                .EnumerateFiles(_folder, "*.log")
                .Select(f =>
                    DateOnly.TryParseExact(
                        Path.GetFileNameWithoutExtension(f),
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var day
                    )
                        ? day
                        : (DateOnly?)null
                )
                .OfType<DateOnly>()
                .OrderDescending()
            : [];

    private int CountDay(DateOnly day) => File.Exists(MetaPath(day)) ? File.ReadLines(MetaPath(day)).Count() : 0;

    /// <summary>The captured packets that match <paramref name="query"/>, newest first (behavior 7).</summary>
    public async Task<CaptureView> ReadAsync(CaptureQuery query, CancellationToken cancellationToken)
    {
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var days = DaysKept().ToList();
            var packets = new List<CapturedPacket>();
            var reasons = new SortedSet<string>(StringComparer.Ordinal);
            var counts = new List<CaptureDay>();
            foreach (var day in days)
            {
                var all = Read(day);
                counts.Add(new CaptureDay(day, all.Count));
                foreach (var packet in all)
                {
                    reasons.UnionWith(packet.Reasons.Select(r => r.Code));
                }
                if (query.Day is { } only && only != day)
                {
                    continue;
                }
                packets.AddRange(
                    all.Where(p => query.Reason is null || p.Reasons.Any(r => r.Code == query.Reason)).Reverse()
                );
            }
            int repeats;
            lock (_lock)
            {
                repeats = _repeatsToday;
            }
            return new CaptureView([.. packets.Take(Math.Max(0, query.Max))], counts, [.. reasons], repeats);
        }
        finally
        {
            _files.Release();
        }
    }

    /// <summary>One day's packets, oldest first: the log's lines with their reasons.</summary>
    private List<CapturedPacket> Read(DateOnly day)
    {
        var lines = File.Exists(LogPath(day)) ? CorpusFile.ReadLines(LogPath(day)).ToList() : [];
        var metas = File.Exists(MetaPath(day))
            ? File.ReadLines(MetaPath(day), Encoding.UTF8)
                .Select(l => JsonSerializer.Deserialize(l, CaptureJson.Default.Meta))
                .ToList()
            : [];
        return
        [
            .. lines
                .Zip(metas)
                .Select(p => new CapturedPacket(
                    p.First.ReceivedAt,
                    p.Second?.Source,
                    p.Second?.Station ?? string.Empty,
                    p.First.Packet,
                    [.. (p.Second?.Reasons ?? []).Select(r => r.ToError())]
                )),
        ];
    }

    /// <summary>
    /// The captured packets as one corpus file, anonymized by APRS.NET's corpus anonymizer or as received, with a note
    /// for an APRS.NET issue (behavior 8).
    /// </summary>
    public async Task<CaptureExportResult> ExportAsync(
        CaptureExport export,
        string appName,
        string appVersion,
        string aprsNetVersion,
        CancellationToken cancellationToken
    )
    {
        var view = await ReadAsync(new CaptureQuery(export.Day, export.Reason, int.MaxValue), cancellationToken)
            .ConfigureAwait(false);
        var packets = view.Packets.Reverse().ToList(); // oldest first, as the corpus wants
        var lines = packets.Select(p => new CorpusLine(p.At, p.Raw)).ToList();
        var leftOut = 0;
        var clean = true;
        if (export.Anonymized)
        {
            var anonymized = new CorpusAnonymizer().Anonymize(lines);
            lines = [.. anonymized.Lines];
            leftOut = anonymized.LeftOut.Count;
            clean = anonymized.IsClean;
        }
        var text = string.Concat(lines.Select(l => l + "\n"));
        var first = packets.Count > 0 ? packets[0].At : (DateTimeOffset?)null;
        var last = packets.Count > 0 ? packets[^1].At : (DateTimeOffset?)null;
        var name = export.Day is { } day
            ? $"capture-{day:yyyy-MM-dd}.log"
            : $"capture-{Day(_time.GetUtcNow()):yyyy-MM-dd}-all.log";
        var note = new StringBuilder()
            .AppendLine(
                CultureInfo.InvariantCulture,
                $"Packets captured by {appName} {appVersion} with APRS.NET {aprsNetVersion}."
            )
            .AppendLine()
            .AppendLine(
                CultureInfo.InvariantCulture,
                $"- {lines.Count} lines, {(first is { } f ? f.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-")} to {(last is { } l ? l.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-")} UTC"
            )
            .AppendLine(
                export.Anonymized
                    ? "- Anonymized with APRS.NET's corpus anonymizer: ready for tests/corpus."
                    : "- As received: not anonymized, so not for tests/corpus as it is."
            );
        foreach (
            var (code, count) in packets
                .SelectMany(p => p.Reasons.Select(r => r.Code).Distinct())
                .GroupBy(c => c)
                .OrderByDescending(g => g.Count())
                .Select(g => (g.Key, g.Count()))
        )
        {
            note.AppendLine(CultureInfo.InvariantCulture, $"- `{code}`: {count}");
        }
        return new CaptureExportResult(name, text, note.ToString(), lines.Count, leftOut, clean);
    }

    /// <summary>Deletes every capture file (behavior 10).</summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var day in DaysKept().ToList())
            {
                DeleteDay(day);
            }
            lock (_lock)
            {
                _writtenToday = 0;
                _repeatsToday = 0;
                _lastWritten.Clear();
            }
        }
        finally
        {
            _files.Release();
        }
    }

    /// <summary>Waits for what's queued to be written; for tests.</summary>
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _pending) > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        _files.Dispose();
    }

    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);

    private string LogPath(DateOnly day) => Path.Combine(_folder, $"{day:yyyy-MM-dd}.log");

    private string MetaPath(DateOnly day) => Path.Combine(_folder, $"{day:yyyy-MM-dd}.json");

    internal sealed record Captured(
        CorpusLine Line,
        string? SourceId,
        string Station,
        IReadOnlyList<EngineError> Reasons
    );

    /// <summary>One line's reasons, as stored beside the log (behavior 4).</summary>
    internal sealed record Meta(string? Source, string Station, IReadOnlyList<MetaReason> Reasons)
    {
        public static Meta Of(Captured captured) =>
            new(
                captured.SourceId,
                captured.Station,
                [.. captured.Reasons.Select(r => new MetaReason(r.Code, r.Message, r.Arg(EngineErrorArgs.Offset)))]
            );
    }

    /// <summary>A reason as stored: its code, words and offset.</summary>
    internal sealed record MetaReason(string Code, string Message, string? Offset)
    {
        public EngineError ToError() =>
            new(
                Code,
                Message,
                Offset is null ? null : new Dictionary<string, string> { [EngineErrorArgs.Offset] = Offset }
            );
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PacketCapture.Meta))]
internal sealed partial class CaptureJson : JsonSerializerContext;
