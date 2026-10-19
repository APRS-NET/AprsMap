// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace AprsMap.Hosting;

/// <summary>
/// The log file (docs/features/F1.32-logging.md, behaviors 7–12): one more logging provider beside the console and
/// OpenTelemetry, so it gets the same lines through the same level rules. Serilog's file sink writes them (decision 8),
/// rolling past the size limit within a day; this provider starts each UTC day's file itself (Serilog rolls by local
/// time), rebuilds the sink when the settings change, and deletes files by age and by the total cap.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly LoggingState _state;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly ITimer _midnight;
    private Serilog.Core.Logger? _sink;
    private string? _currentFile;
    private bool _disposed;

    /// <summary>Starts writing as <paramref name="state"/> says, now and whenever it changes.</summary>
    public FileLoggerProvider(LoggingState state, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(time);
        _state = state;
        _time = time;
        _midnight = time.CreateTimer(_ => Rebuild(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _state.Changed += OnChanged;
        Rebuild();
    }

    /// <summary>The file being written now, or null while the log file is off.</summary>
    public string? CurrentFile
    {
        get
        {
            lock (_gate)
            {
                return _currentFile;
            }
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _state.Changed -= OnChanged;
            _midnight.Dispose();
            _sink?.Dispose(); // what's written is flushed
            _sink = null;
        }
    }

    /// <summary>The file name for <paramref name="day"/>: the product's slug and the UTC date (behavior 9).</summary>
    public static string FileName(DateOnly day) =>
        string.Create(CultureInfo.InvariantCulture, $"{Brand.Slug}-{day:yyyy-MM-dd}.log");

    private void OnChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>Opens the day's file as the settings say, or closes it when the log file is off; then cleans up.</summary>
    private void Rebuild()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            var now = _time.GetUtcNow();
            var file = _state.Current.EffectiveFile;
            var path = _state.FileOn
                ? Path.Combine(_state.Folder, FileName(DateOnly.FromDateTime(now.UtcDateTime)))
                : null;
            var old = _sink;
            _sink = null;
            _currentFile = null;
            if (path is not null)
            {
                try
                {
                    Directory.CreateDirectory(_state.Folder);
                    _sink = new LoggerConfiguration()
                        .MinimumLevel.Verbose() // the level rules are .NET's, applied before a line gets here
                        .WriteTo.File(
                            path,
                            outputTemplate: "{Message:l}{NewLine}{Exception}",
                            formatProvider: CultureInfo.InvariantCulture,
                            fileSizeLimitBytes: (long)file.MaxFileMegabytes * 1024 * 1024,
                            rollOnFileSizeLimit: true, // aprsmap-2026-10-19_001.log and on, within the day
                            retainedFileCountLimit: null, // kept by age and total, below
                            encoding: new UTF8Encoding(false)
                        )
                        .CreateLogger();
                    _currentFile = path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // The folder can't be written: the console and OpenTelemetry still log (behavior 8).
                    Trace.TraceWarning($"The log file {path} can't be written: {ex.Message}");
                }
            }
            old?.Dispose();
            if (_state.FileOn)
            {
                Clean(_state.Folder, file, now, _currentFile);
            }
            // The next UTC day starts a new file, and is when old ones are cleaned up again (behavior 10).
            var next = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            _midnight.Change(next - now + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Deletes this app's log files older than Keep for, then the oldest while they total more than the cap (behaviors
    /// 10, 11), never <paramref name="current"/> and never another program's files.
    /// </summary>
    internal static void Clean(string folder, LogFileSettings file, DateTimeOffset now, string? current)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!Directory.Exists(folder))
        {
            return;
        }
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var oldest = today.AddDays(-(file.KeepDays - 1)); // Keep for 1 keeps today's only
        var files = new List<(FileInfo Info, DateOnly Day)>();
        foreach (var path in Directory.EnumerateFiles(folder, Brand.Slug + "-*.log"))
        {
            var info = new FileInfo(path);
            if (DayOf(info.Name) is not { } day)
            {
                continue; // not one of ours
            }
            if (day < oldest && !Same(path, current))
            {
                TryDelete(info);
                continue;
            }
            files.Add((info, day));
        }
        var cap = (long)file.TotalMegabytes * 1024 * 1024;
        var total = files.Sum(f => f.Info.Length);
        foreach (var (info, _) in files.OrderBy(f => f.Day).ThenBy(f => f.Info.Name, StringComparer.Ordinal))
        {
            if (total <= cap)
            {
                break;
            }
            if (Same(info.FullName, current))
            {
                continue;
            }
            total -= info.Length;
            TryDelete(info);
        }

        static bool Same(string path, string? other) =>
            other is not null
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(other), StringComparison.Ordinal);

        static void TryDelete(FileInfo info)
        {
            try
            {
                info.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // in use: next time
        }
    }

    /// <summary>The UTC day in a log file's name (<c>aprsmap-2026-10-19.log</c>, <c>aprsmap-2026-10-19_001.log</c>), or null.</summary>
    internal static DateOnly? DayOf(string name)
    {
        var prefix = Brand.Slug + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length < prefix.Length + 10)
        {
            return null;
        }
        var rest = name[(prefix.Length + 10)..];
        return
            (rest == ".log" || (rest.StartsWith('_') && rest.EndsWith(".log", StringComparison.Ordinal)))
            && DateOnly.TryParseExact(
                name.AsSpan(prefix.Length, 10),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var day
            )
            ? day
            : null;
    }

    /// <summary>Writes one line (behavior 12): UTC time, level, category, message, trace and span, then the exception.</summary>
    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var activity = Activity.Current;
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{_time.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} {Short(level)} {category} {message}"
        );
        if (activity is not null)
        {
            line += string.Create(CultureInfo.InvariantCulture, $" trace={activity.TraceId} span={activity.SpanId}");
        }
        lock (_gate)
        {
            _sink?.Write(LogEventLevel.Information, exception, "{Line:l}", line);
        }
    }

    private bool IsOn
    {
        get
        {
            lock (_gate)
            {
                return _sink is not null;
            }
        }
    }

    private static string Short(LogLevel level) =>
        level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && provider.IsOn;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
