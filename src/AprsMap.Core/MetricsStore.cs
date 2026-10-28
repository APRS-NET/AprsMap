// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AprsMap.Core;

/// <summary>
/// The dashboard's counters by the hour (docs/features/F1.41-persistent-history.md, behavior 10): <c>metrics.db</c> in
/// the history's folder, kept for <see cref="KeptFor"/> whatever the packet history's length or size limit, as they're
/// small. The minutes stay in <c>dashboard-history.json</c> (#386); each hour is written here once it has ended.
/// </summary>
internal sealed class MetricsStore(string folder) : IDisposable
{
    /// <summary>The file's name in the history's folder.</summary>
    public const string FileName = "metrics.db";

    /// <summary>How long hours are kept (decision 5: 90 days).</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(90);

    private const int SchemaVersion = 2; // 2: hours per source (#454)

    private readonly Lock _lock = new();
    private SqliteConnection? _connection;

    private string FilePath => Path.Combine(folder, FileName);

    /// <summary>
    /// Keeps <paramref name="hours"/> of <paramref name="source"/> (empty for the whole feed), replacing any already kept,
    /// and drops those past <see cref="KeptFor"/>.
    /// </summary>
    public void Save(IReadOnlyList<DashboardHour> hours, DateTimeOffset now, string source = "")
    {
        lock (_lock)
        {
            var connection = Connection();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO hours (hour, source, packets, problems, new, stations, by_source, arrivals, kinds)
                VALUES ($hour, $source, $packets, $problems, $new, $stations, $bySource, $arrivals, $kinds)
                """;
            insert.Parameters.AddWithValue("$source", source);
            var hour = insert.Parameters.Add("$hour", SqliteType.Integer);
            var packets = insert.Parameters.Add("$packets", SqliteType.Integer);
            var problems = insert.Parameters.Add("$problems", SqliteType.Integer);
            var fresh = insert.Parameters.Add("$new", SqliteType.Integer);
            var stations = insert.Parameters.Add("$stations", SqliteType.Integer);
            var bySource = insert.Parameters.Add("$bySource", SqliteType.Text);
            var arrivals = insert.Parameters.Add("$arrivals", SqliteType.Text);
            var kinds = insert.Parameters.Add("$kinds", SqliteType.Text);
            foreach (var counts in hours)
            {
                hour.Value = counts.Hour;
                packets.Value = counts.Packets;
                problems.Value = counts.Problems;
                fresh.Value = counts.New;
                stations.Value = counts.Stations;
                bySource.Value = Write(counts.BySource);
                arrivals.Value = Write(counts.Arrivals);
                kinds.Value = Write(counts.Kinds);
                insert.ExecuteNonQuery();
            }
            using var prune = connection.CreateCommand();
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM hours WHERE hour < $oldest";
            prune.Parameters.AddWithValue("$oldest", HourOf(now - KeptFor));
            prune.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>The hours of <paramref name="source"/> (empty for the whole feed) kept from <paramref name="since"/> on, oldest first.</summary>
    public IReadOnlyList<DashboardHour> Read(DateTimeOffset since, string source = "")
    {
        lock (_lock)
        {
            using var query = Connection().CreateCommand();
            query.CommandText = """
                SELECT hour, packets, problems, new, stations, by_source, arrivals, kinds
                FROM hours WHERE hour >= $since AND source = $source ORDER BY hour
                """;
            query.Parameters.AddWithValue("$since", HourOf(since));
            query.Parameters.AddWithValue("$source", source);
            using var reader = query.ExecuteReader();
            var hours = new List<DashboardHour>();
            while (reader.Read())
            {
                hours.Add(
                    new DashboardHour(
                        reader.GetInt64(0),
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        Read(reader.GetString(5)),
                        Read(reader.GetString(6)),
                        Read(reader.GetString(7))
                    )
                );
            }
            return hours;
        }
    }

    /// <summary>Deletes every hour kept: Start over (F1.41 behavior 12).</summary>
    public void DeleteAll()
    {
        lock (_lock)
        {
            _connection?.Dispose();
            _connection = null;
            foreach (var file in new[] { FilePath, FilePath + "-wal", FilePath + "-shm" }.Where(File.Exists))
            {
                File.Delete(file);
            }
        }
    }

    private SqliteConnection Connection()
    {
        if (_connection is not null)
        {
            return _connection;
        }
        try
        {
            return _connection = Open();
        }
        catch (Exception ex) when (ex is SqliteException { SqliteErrorCode: 11 or 26 } or InvalidDataException)
        {
            // Not a file of this version: set aside, and the hours start afresh (behavior 13).
            foreach (var file in new[] { FilePath, FilePath + "-wal", FilePath + "-shm" }.Where(File.Exists))
            {
                File.Move(file, file + ".invalid", overwrite: true);
            }
            return _connection = Open();
        }
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(folder);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString()
        );
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version == 0)
            {
                command.CommandText = $"""
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE hours (
                        hour INTEGER NOT NULL,
                        source TEXT NOT NULL,
                        packets INTEGER NOT NULL,
                        problems INTEGER NOT NULL,
                        new INTEGER NOT NULL,
                        stations INTEGER NOT NULL,
                        by_source TEXT NOT NULL,
                        arrivals TEXT NOT NULL,
                        kinds TEXT NOT NULL,
                        PRIMARY KEY (hour, source)
                    );
                    PRAGMA user_version = {SchemaVersion};
                    """;
                command.ExecuteNonQuery();
            }
            else if (version != SchemaVersion)
            {
                throw new InvalidDataException($"Metrics file version {version}, not {SchemaVersion}.");
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

    private static long HourOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / 3600;

    private static string Write(IReadOnlyDictionary<string, int> counts) =>
        JsonSerializer.Serialize(counts, HistoryJsonContext.Default.IReadOnlyDictionaryStringInt32);

    private static Dictionary<string, int> Read(string json) =>
        JsonSerializer.Deserialize(json, HistoryJsonContext.Default.DictionaryStringInt32) ?? [];

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
