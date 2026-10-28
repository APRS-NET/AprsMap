// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>
/// The dashboard's and the GPS's history as kept between runs (issue #386): what <see cref="DashboardCounters"/> and
/// <see cref="GpsHistory"/> export, with the file's version. Enum values are kept by name, so reordering an enum
/// doesn't misread an older file.
/// </summary>
internal sealed record HistorySnapshot(
    int Version,
    DateTimeOffset SavedAt,
    DashboardCountersState? Dashboard,
    GpsHistoryState? Gps
)
{
    /// <summary>The version written now; a file of another version is set aside.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>Where the history is kept between runs, so a database can take the file's place later.</summary>
internal interface IHistoryStore
{
    /// <summary>The history kept, or null when there's none or it can't be read (it's set aside).</summary>
    Task<HistorySnapshot?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Keeps <paramref name="snapshot"/>, replacing what was kept.</summary>
    Task SaveAsync(HistorySnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>
/// The history in a JSON file, written to a temporary file and then renamed over the old one, so a crash mid-write
/// keeps the previous file. A file that can't be read is renamed <c>.invalid</c> and the history starts afresh.
/// </summary>
internal sealed partial class FileHistoryStore(string path, ILogger logger) : IHistoryStore
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "dashboard-history.json";

    public async Task<HistorySnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            await using var file = File.OpenRead(path);
            var snapshot = await JsonSerializer
                .DeserializeAsync(file, HistoryJsonContext.Default.HistorySnapshot, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is { Version: HistorySnapshot.CurrentVersion })
            {
                return snapshot;
            }
            LogSetAside(logger, path, "a version this app doesn't read", null);
        }
        catch (JsonException ex)
        {
            LogSetAside(logger, path, "it can't be read", ex);
        }
        catch (IOException ex)
        {
            LogNotRead(logger, path, ex);
            return null;
        }
        try
        {
            File.Move(path, path + ".invalid", overwrite: true);
        }
        catch (IOException) { } // left where it is: the next save replaces it
        return null;
    }

    public async Task SaveAsync(HistorySnapshot snapshot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        await using (var file = File.Create(temporary))
        {
            await JsonSerializer
                .SerializeAsync(file, snapshot, HistoryJsonContext.Default.HistorySnapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(temporary, path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard history {Path} set aside: {Reason}; starting afresh")]
    private static partial void LogSetAside(ILogger logger, string path, string reason, Exception? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard history {Path} couldn't be read; starting afresh")]
    private static partial void LogNotRead(ILogger logger, string path, Exception error);
}

/// <summary>The dashboard's counters as kept: the minutes, the distinct-station rings, the stations and relays.</summary>
internal sealed record DashboardCountersState(
    DateTimeOffset HistoryStart,
    IReadOnlyList<DashboardMinuteState> Minutes,
    IReadOnlyList<IReadOnlyList<long[]>> Distinct,
    IReadOnlyList<HeardState> Stations,
    IReadOnlyDictionary<string, long[][]> Relays,
    IReadOnlyDictionary<string, DateTimeOffset> Sources,
    IReadOnlyList<Contracts.DashboardMoment> Newest
);

/// <summary>One minute of the feed.</summary>
internal sealed record DashboardMinuteState(
    long Minute,
    int Packets,
    int Problems,
    int New,
    IReadOnlyDictionary<string, int> BySource,
    IReadOnlyDictionary<string, int> Arrivals,
    IReadOnlyDictionary<string, int> Kinds
);

/// <summary>A station heard in the window and its 5-minute tallies.</summary>
internal sealed record HeardState(
    string Name,
    DateTimeOffset FirstHeard,
    DateTimeOffset LastHeard,
    long[] LastSlots,
    IReadOnlyList<TallyState> Tallies
);

/// <summary>A station's packets in one 5-minute step that arrived one way and were one kind.</summary>
internal sealed record TallyState(long Step, int Count, int Problems, string Arrival, string Kind);

/// <summary>The GPS's history as kept: its minutes, outages and the wander's origin.</summary>
internal sealed record GpsHistoryState(
    IReadOnlyList<GpsMinuteState> Minutes,
    IReadOnlyList<GpsOutageState> Outages,
    double? OriginLatitude,
    double? OriginLongitude
);

/// <summary>One minute of GPS samples.</summary>
internal sealed record GpsMinuteState(
    long Minute,
    IReadOnlyDictionary<string, double> Seconds,
    IReadOnlyDictionary<string, double> BySource,
    int Losses,
    int FixSamples,
    GpsStatState Used,
    GpsStatState InView,
    GpsStatState Signal,
    IReadOnlyDictionary<string, double> UsedBySystem,
    IReadOnlyDictionary<string, int> SeenBySystem,
    IReadOnlyDictionary<string, GpsStatState> Dilutions,
    IReadOnlyDictionary<string, GpsStatState> Errors,
    double[] Wander
);

/// <summary>A running average, lowest and highest as kept.</summary>
internal sealed record GpsStatState(double Sum, int Count, double Lowest, double Highest);

/// <summary>A time without a fix.</summary>
internal sealed record GpsOutageState(DateTimeOffset Start, DateTimeOffset? End);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // generated code
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HistorySnapshot))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
