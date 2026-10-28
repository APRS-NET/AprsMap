// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>A packet as the history keeps it (docs/features/F1.41-persistent-history.md, behavior 1).</summary>
/// <param name="ReceivedAt">When it arrived.</param>
/// <param name="SourceId">The source it came from (F1.25), or null.</param>
/// <param name="Own">This station sent it.</param>
/// <param name="Duplicate">A copy of one heard before on the same source, listed but not applied (F1.3 behavior 11).</param>
/// <param name="Name">The station, object or item it's about, to find it again; null when it has none.</param>
/// <param name="Kind">Whether <paramref name="Name"/> is a station, an object or an item.</param>
/// <param name="Latitude">Its position's latitude, if it has one.</param>
/// <param name="Longitude">Its position's longitude, if it has one.</param>
/// <param name="Raw">The packet as received, byte for byte.</param>
internal sealed record HistoryRecord(
    DateTimeOffset ReceivedAt,
    string? SourceId,
    bool Own,
    bool Duplicate,
    string? Name,
    StationKind Kind,
    double? Latitude,
    double? Longitude,
    byte[] Raw
);

/// <summary>
/// The packet history (docs/features/F1.41-persistent-history.md): every packet the engine lists, kept in one SQLite
/// file a day (UTC) in the <c>history</c> folder, so rotating is deleting whole files (behavior 4). Packets are queued
/// and written in the background, a transaction for whatever has queued, so receiving never waits (behavior 6). A lock
/// file keeps a second engine on the same folder out (behavior 13).
/// </summary>
internal sealed partial class PacketHistory : IAsyncDisposable
{
    /// <summary>The folder's name in the data folder.</summary>
    public const string FolderName = "history";

    private const string Extension = ".db";
    private const string LockName = ".lock";
    private const int SchemaVersion = 1;
    private const int QueueLength = 10_000;
    private static readonly TimeSpan WarnEvery = TimeSpan.FromDays(1);

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Channel<HistoryRecord> _queue = Channel.CreateBounded<HistoryRecord>(
        new BoundedChannelOptions(QueueLength) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true }
    );
    private readonly SemaphoreSlim _files = new(1, 1);
    private readonly List<DateOnly> _setAside = [];
    private readonly Task _writer;
    private FileStream? _lockFile;
    private HistorySettings _settings = HistorySettings.Default;
    private HistoryProblem _blocked; // InUseElsewhere or CantWrite: nothing is kept this run
    private DateOnly? _fullDay; // today's file reached the size limit (behavior 5)
    private int? _daysFit;
    private DateTimeOffset _warnedAt = DateTimeOffset.MinValue;
    private SqliteConnection? _connection;
    private DateOnly _connectionDay;
    private int _pending;
    private long _dropped;

    /// <param name="folder">The <c>history</c> folder.</param>
    /// <param name="time">For days and the age limit.</param>
    /// <param name="logger">For what's set aside and the size limit's warning.</param>
    public PacketHistory(string folder, TimeProvider time, ILogger logger)
    {
        _folder = folder;
        _time = time;
        _logger = logger;
        try
        {
            Directory.CreateDirectory(folder);
            // Held for the engine's life: a second engine on this folder can't open it, and keeps no history.
            _lockFile = new FileStream(
                Path.Combine(folder, LockName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose
            );
        }
        catch (IOException ex) when (ex is not DirectoryNotFoundException and not PathTooLongException)
        {
            _blocked = HistoryProblem.InUseElsewhere;
            LogInUse(_logger, folder, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _blocked = HistoryProblem.CantWrite;
            LogCantWrite(_logger, folder, ex);
        }
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>The length and size limit in force. Turned off, what's kept is deleted (behavior 2).</summary>
    public HistorySettings Settings
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
            bool wasOn;
            lock (_lock)
            {
                wasOn = _settings.On;
                _settings = value;
            }
            if (wasOn && !value.On && _blocked == HistoryProblem.None)
            {
                _ = DeleteAllAsync(); // turned off and saved: after the confirmation
            }
        }
    }

    /// <summary>The history's folder.</summary>
    public string Folder => _folder;

    /// <summary>Whether this engine has the folder: not when another process does, or it can't be written.</summary>
    public bool Usable => _blocked == HistoryProblem.None;

    /// <summary>Packets dropped because the disk couldn't keep up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The station or object <paramref name="packet"/> is about, as the station store files it (F1.3).</summary>
    public static (string? Name, StationKind Kind) EntryOf(AprsPacket packet)
    {
        if (packet.Payload is ThirdPartyPayload { Packet: { HasHeader: true } inner })
        {
            packet = inner;
        }
        return packet.Payload switch
        {
            ObjectReport report => (report.Name, StationKind.Object),
            ItemReport report => (report.Name, StationKind.Item),
            _ => (packet.Source?.Value, StationKind.Station),
        };
    }

    /// <summary>Keeps <paramref name="record"/>, unless history is off or can't be kept today. Never blocks.</summary>
    public void Record(HistoryRecord record)
    {
        lock (_lock)
        {
            if (!_settings.On || _blocked != HistoryProblem.None || _fullDay == Day(record.ReceivedAt))
            {
                return;
            }
        }
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(record))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task WriteAsync()
    {
        var batch = new List<HistoryRecord>();
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (batch.Count < QueueLength && _queue.Reader.TryRead(out var record))
            {
                batch.Add(record);
            }
            await _files.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var day in batch.GroupBy(r => Day(r.ReceivedAt)))
                {
                    Write(day.Key, day);
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                Interlocked.Add(ref _dropped, batch.Count);
                LogNotWritten(_logger, ex);
                CloseConnection(); // opened afresh next time
            }
            finally
            {
                _files.Release();
                Interlocked.Add(ref _pending, -batch.Count);
                batch.Clear();
            }
        }
    }

    /// <summary>Writes <paramref name="records"/> to <paramref name="day"/>'s file in one transaction.</summary>
    private void Write(DateOnly day, IEnumerable<HistoryRecord> records)
    {
        var connection = ConnectionFor(day);
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO packets (received_at, source, own, duplicate, name, kind, lat, lon, raw)
            VALUES ($at, $source, $own, $duplicate, $name, $kind, $lat, $lon, $raw)
            """;
        var at = insert.Parameters.Add("$at", SqliteType.Integer);
        var source = insert.Parameters.Add("$source", SqliteType.Text);
        var own = insert.Parameters.Add("$own", SqliteType.Integer);
        var duplicate = insert.Parameters.Add("$duplicate", SqliteType.Integer);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var kind = insert.Parameters.Add("$kind", SqliteType.Integer);
        var lat = insert.Parameters.Add("$lat", SqliteType.Real);
        var lon = insert.Parameters.Add("$lon", SqliteType.Real);
        var raw = insert.Parameters.Add("$raw", SqliteType.Blob);
        foreach (var record in records)
        {
            at.Value = record.ReceivedAt.ToUnixTimeMilliseconds();
            source.Value = (object?)record.SourceId ?? DBNull.Value;
            own.Value = record.Own ? 1 : 0;
            duplicate.Value = record.Duplicate ? 1 : 0;
            name.Value = (object?)record.Name ?? DBNull.Value;
            kind.Value = (int)record.Kind;
            lat.Value = (object?)record.Latitude ?? DBNull.Value;
            lon.Value = (object?)record.Longitude ?? DBNull.Value;
            raw.Value = record.Raw;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>The writer's connection to <paramref name="day"/>'s file, made when the day changes.</summary>
    private SqliteConnection ConnectionFor(DateOnly day)
    {
        if (_connection is not null && _connectionDay == day)
        {
            return _connection;
        }
        CloseConnection();
        _connection = Open(day, create: true);
        _connectionDay = day;
        return _connection;
    }

    private void CloseConnection()
    {
        _connection?.Dispose();
        _connection = null;
    }

    /// <summary>
    /// Opens <paramref name="day"/>'s file, made with the table when <paramref name="create"/> is set. A file that isn't
    /// a history of this version is renamed <c>.invalid</c> and left out (behavior 13), and a new one made in its place.
    /// </summary>
    private SqliteConnection Open(DateOnly day, bool create)
    {
        var path = PathOf(day);
        try
        {
            return OpenFile(path, create);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT, SQLITE_NOTADB
        {
            SetAside(day, path, ex);
            return OpenFile(path, create);
        }
        catch (InvalidDataException ex)
        {
            SetAside(day, path, ex);
            return OpenFile(path, create);
        }
    }

    private static SqliteConnection OpenFile(string path, bool create)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
                Pooling = false, // a day's file is deleted whole: no handle may stay open in a pool
            }.ToString()
        );
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version == 0 && create)
            {
                command.CommandText = $"""
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE packets (
                        received_at INTEGER NOT NULL,
                        source TEXT,
                        own INTEGER NOT NULL,
                        duplicate INTEGER NOT NULL,
                        name TEXT,
                        kind INTEGER NOT NULL,
                        lat REAL,
                        lon REAL,
                        raw BLOB NOT NULL
                    );
                    CREATE INDEX packets_name ON packets (name, received_at);
                    PRAGMA user_version = {SchemaVersion};
                    """;
                command.ExecuteNonQuery();
            }
            else if (version != SchemaVersion)
            {
                throw new InvalidDataException($"History file version {version}, not {SchemaVersion}.");
            }
            command.CommandText = "PRAGMA synchronous = NORMAL";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void SetAside(DateOnly day, string path, Exception error)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in FilesOf(path))
        {
            File.Move(file, file + ".invalid", overwrite: true);
        }
        lock (_lock)
        {
            if (!_setAside.Contains(day))
            {
                _setAside.Add(day);
                _setAside.Sort();
            }
        }
        LogSetAside(_logger, path, error);
    }

    /// <summary>
    /// The packets kept since <paramref name="since"/>, oldest first, from every day's file that may have some.
    /// </summary>
    public async IAsyncEnumerable<HistoryRecord> ReadAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        foreach (var day in DaysKept().Where(d => d >= Day(since)).Order())
        {
            List<HistoryRecord> records;
            await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                records = ReadDay(day, since);
            }
            finally
            {
                _files.Release();
            }
            foreach (var record in records)
            {
                yield return record;
            }
        }
    }

    private List<HistoryRecord> ReadDay(DateOnly day, DateTimeOffset since)
    {
        var records = new List<HistoryRecord>();
        try
        {
            using var connection = day == _connectionDay && _connection is not null ? null : Open(day, create: false);
            using var query = (connection ?? _connection!).CreateCommand();
            query.CommandText = """
                SELECT received_at, source, own, duplicate, name, kind, lat, lon, raw
                FROM packets WHERE received_at >= $since ORDER BY rowid
                """;
            query.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                records.Add(RecordAt(reader));
            }
        }
        catch (SqliteException ex)
        {
            LogNotRead(_logger, PathOf(day), ex); // left for the writer or the next start to set aside
        }
        return records;
    }

    /// <summary>
    /// How many stations were heard since <paramref name="since"/>, and the <paramref name="top"/> that sent the most,
    /// from every day kept (behavior 10): the dashboard's lists over 7 days. Duplicates and own packets aren't counted.
    /// </summary>
    public async Task<(int Stations, IReadOnlyList<(string Name, int Count)> Busiest)> StationsSinceAsync(
        DateTimeOffset since,
        int top,
        CancellationToken cancellationToken = default
    )
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var day in DaysKept().Where(d => d >= Day(since)))
        {
            await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var connection = day == _connectionDay && _connection is not null ? null : Open(day, false);
                using var query = (connection ?? _connection!).CreateCommand();
                query.CommandText = """
                    SELECT name, COUNT(*) FROM packets
                    WHERE received_at >= $since AND name IS NOT NULL AND duplicate = 0 AND own = 0
                    GROUP BY name
                    """;
                query.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    counts[name] = counts.GetValueOrDefault(name) + reader.GetInt32(1);
                }
            }
            catch (SqliteException ex)
            {
                LogNotRead(_logger, PathOf(day), ex);
            }
            finally
            {
                _files.Release();
            }
        }
        return (
            counts.Count,
            [
                .. counts
                    .OrderByDescending(c => c.Value)
                    .ThenBy(c => c.Key, StringComparer.Ordinal)
                    .Take(top)
                    .Select(c => (c.Key, c.Value)),
            ]
        );
    }

    /// <summary>
    /// <paramref name="name"/>'s positions kept since <paramref name="since"/>, oldest first (behavior 8): duplicates
    /// left out, as the store leaves them out of the track.
    /// </summary>
    public async Task<IReadOnlyList<TrackPoint>> PositionsAsync(
        string name,
        DateTimeOffset since,
        CancellationToken cancellationToken = default
    )
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var points = new List<TrackPoint>();
        foreach (var day in DaysKept().Where(d => d >= Day(since)).Order())
        {
            await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var connection = day == _connectionDay && _connection is not null ? null : Open(day, false);
                using var query = (connection ?? _connection!).CreateCommand();
                query.CommandText = """
                    SELECT received_at, lat, lon FROM packets
                    WHERE name = $name AND received_at >= $since AND lat IS NOT NULL AND duplicate = 0
                    ORDER BY rowid
                    """;
                query.Parameters.AddWithValue("$name", name);
                query.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    points.Add(
                        new TrackPoint(
                            reader.GetDouble(1),
                            reader.GetDouble(2),
                            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0))
                        )
                    );
                }
            }
            catch (SqliteException ex)
            {
                LogNotRead(_logger, PathOf(day), ex);
            }
            finally
            {
                _files.Release();
            }
        }
        return points;
    }

    /// <summary>
    /// Up to <paramref name="count"/> of <paramref name="name"/>'s packets kept from before <paramref name="before"/>
    /// (all of them with null), newest first (behavior 9).
    /// </summary>
    public async Task<IReadOnlyList<HistoryRecord>> PacketsOfAsync(
        string name,
        DateTimeOffset? before,
        int count,
        CancellationToken cancellationToken = default
    )
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<HistoryRecord>();
        var until = before ?? DateTimeOffset.MaxValue.AddYears(-1);
        foreach (var day in DaysKept().Where(d => d <= Day(until)).OrderDescending())
        {
            if (records.Count >= count)
            {
                break;
            }
            await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var connection = day == _connectionDay && _connection is not null ? null : Open(day, false);
                using var query = (connection ?? _connection!).CreateCommand();
                query.CommandText = """
                    SELECT received_at, source, own, duplicate, name, kind, lat, lon, raw FROM packets
                    WHERE name = $name AND received_at < $before
                    ORDER BY rowid DESC LIMIT $count
                    """;
                query.Parameters.AddWithValue("$name", name);
                query.Parameters.AddWithValue("$before", until.ToUnixTimeMilliseconds());
                query.Parameters.AddWithValue("$count", count - records.Count);
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(RecordAt(reader));
                }
            }
            catch (SqliteException ex)
            {
                LogNotRead(_logger, PathOf(day), ex);
            }
            finally
            {
                _files.Release();
            }
        }
        return records;
    }

    private static HistoryRecord RecordAt(SqliteDataReader reader) =>
        new(
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2) != 0,
            reader.GetInt64(3) != 0,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            (StationKind)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            (byte[])reader.GetValue(8)
        );

    /// <summary>When the oldest packet kept was received; null with none.</summary>
    public async Task<DateTimeOffset?> KeptSinceAsync(CancellationToken cancellationToken = default) =>
        (await StatusAsync(false, cancellationToken).ConfigureAwait(false)).KeptSince;

    /// <summary>
    /// The once-a-minute pass (behaviors 4, 5): deletes the days past the age limit, then the oldest past the size
    /// limit, and notes when the limit is what's cutting the history short.
    /// </summary>
    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        var settings = Settings;
        if (!settings.On || _blocked != HistoryProblem.None)
        {
            return;
        }
        var now = _time.GetUtcNow();
        var today = Day(now);
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var days = DaysKept().OrderDescending().ToList(); // newest first
            var oldest = today.AddDays(-settings.Days + 1);
            foreach (var day in days.Where(d => d < oldest).ToList())
            {
                DeleteDay(day);
                days.Remove(day);
            }
            var limit = (long)settings.MaxMegabytes * 1024 * 1024;
            // At the rate of the whole days kept, before the limit trims them, how many fit (behavior 5).
            var past = days.Where(d => d < today).Select(SizeOf).ToList();
            int? fit = past.Count > 0 ? (int)Math.Max(1, limit / Math.Max(1, past.Sum() / past.Count)) : null;
            while (days.Count > 1 && days.Sum(SizeOf) > limit)
            {
                DeleteDay(days[^1]);
                days.RemoveAt(days.Count - 1);
            }
            var full = days.Count > 0 && days[0] == today && days.Sum(SizeOf) > limit ? today : (DateOnly?)null;
            lock (_lock)
            {
                _fullDay = full;
                _daysFit = fit < settings.Days ? fit : null;
            }
            if ((full is not null || fit < settings.Days) && now - _warnedAt >= WarnEvery)
            {
                _warnedAt = now;
                LogSizeLimit(_logger, settings.MaxMegabytes, fit ?? 0, settings.Days);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotTrimmed(_logger, ex); // tried again in a minute
        }
        finally
        {
            _files.Release();
        }
    }

    /// <summary>
    /// The size limit's problem as the last pass found it, without reading the folder: for the dashboard, asked every
    /// 10 seconds (behavior 5). Null when there's none.
    /// </summary>
    public HistoryStatus? SizeProblem()
    {
        lock (_lock)
        {
            var problem =
                !_settings.On || _blocked != HistoryProblem.None ? HistoryProblem.None
                : _fullDay == Day(_time.GetUtcNow()) ? HistoryProblem.TodayFull
                : _daysFit is not null ? HistoryProblem.SizeLimitShortens
                : HistoryProblem.None;
            return problem == HistoryProblem.None
                ? null
                : new HistoryStatus(true, _settings, Problem: problem, DaysFit: _daysFit);
        }
    }

    /// <summary>What the history holds now (behavior 3).</summary>
    public async Task<HistoryStatus> StatusAsync(bool configured, CancellationToken cancellationToken = default)
    {
        var settings = Settings;
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var days = DaysKept().Order().ToList();
            DateTimeOffset? since = null;
            foreach (var day in days)
            {
                if (ReadDay(day, DateTimeOffset.MinValue.AddYears(1970)).FirstOrDefault() is { } first)
                {
                    since = first.ReceivedAt;
                    break;
                }
            }
            HistoryProblem problem;
            int? fit;
            List<DateOnly> setAside;
            lock (_lock)
            {
                problem =
                    _blocked != HistoryProblem.None ? _blocked
                    : _fullDay == Day(_time.GetUtcNow()) ? HistoryProblem.TodayFull
                    : _daysFit is not null ? HistoryProblem.SizeLimitShortens
                    : HistoryProblem.None;
                fit = _daysFit;
                setAside = [.. _setAside];
            }
            return new HistoryStatus(
                true,
                settings,
                configured,
                days.Sum(SizeOf),
                since,
                days.Count,
                settings.On ? problem : _blocked,
                problem is HistoryProblem.SizeLimitShortens or HistoryProblem.TodayFull ? fit : null,
                setAside.Count > 0 ? setAside : null
            );
        }
        finally
        {
            _files.Release();
        }
    }

    /// <summary>Deletes every day kept: history turned off (behavior 2), or Start over (behavior 12).</summary>
    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        if (_blocked != HistoryProblem.None)
        {
            return; // another engine's history: not ours to delete
        }
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloseConnection();
            foreach (var day in DaysKept().ToList())
            {
                DeleteDay(day);
            }
            lock (_lock)
            {
                _fullDay = null;
                _daysFit = null;
                _setAside.Clear();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotTrimmed(_logger, ex);
        }
        finally
        {
            _files.Release();
        }
    }

    /// <summary>Waits until every packet queued so far is written.</summary>
    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _pending) > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void DeleteDay(DateOnly day)
    {
        if (day == _connectionDay)
        {
            CloseConnection();
        }
        foreach (var file in FilesOf(PathOf(day)))
        {
            File.Delete(file);
        }
    }

    /// <summary>A day's file with its write-ahead log and shared memory, those that exist.</summary>
    private static IEnumerable<string> FilesOf(string path) =>
        new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists);

    private long SizeOf(DateOnly day) => FilesOf(PathOf(day)).Sum(f => new FileInfo(f).Length);

    private string PathOf(DateOnly day) =>
        Path.Combine(_folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Extension);

    /// <summary>The days with a file.</summary>
    private IEnumerable<DateOnly> DaysKept() =>
        Directory.Exists(_folder)
            ? Directory
                .EnumerateFiles(_folder, "*" + Extension)
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
            : [];

    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        CloseConnection();
        _lockFile?.Dispose();
        _lockFile = null;
        _files.Dispose();
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Packet history in {Folder} is in use by another process; keeping no history this run"
    )]
    private static partial void LogInUse(ILogger logger, string folder, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Packet history can't be kept in {Folder}")]
    private static partial void LogCantWrite(ILogger logger, string folder, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Packet history {Path} set aside: it can't be read")]
    private static partial void LogSetAside(ILogger logger, string path, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Packet history {Path} couldn't be read")]
    private static partial void LogNotRead(ILogger logger, string path, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Packets couldn't be written to the history")]
    private static partial void LogNotWritten(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Old packet history couldn't be deleted")]
    private static partial void LogNotTrimmed(ILogger logger, Exception error);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Packet history's size limit ({Megabytes} MB) keeps about {Fit} of the {Days} days asked for"
    )]
    private static partial void LogSizeLimit(ILogger logger, int megabytes, int fit, int days);
}
