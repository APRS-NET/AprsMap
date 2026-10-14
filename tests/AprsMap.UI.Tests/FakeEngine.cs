// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;

namespace AprsMap.UI.Tests;

internal sealed class FakeEngine : IEngineService
{
    private Channel<PacketView> _packets = Channel.CreateUnbounded<PacketView>();
    private Channel<StationBatch> _stations = Channel.CreateUnbounded<StationBatch>();
    private int _stationSubscriptions;
    private int _packetSubscriptions;

    public EngineStatus Status { get; set; } = new(ConnectionState.Connected, "T2TEST", 0, "0.0.0", "home", "N0CALL");

    /// <summary>When set, <see cref="GetStatusAsync"/> throws it (the gateway is unreachable).</summary>
    public Exception? StatusFailure { get; set; }

    public int StationSubscriptions => Volatile.Read(ref _stationSubscriptions);

    public AppSettings Settings { get; set; } = TwoProfiles();

    /// <summary>When set, <see cref="SaveSettingsAsync"/> throws it.</summary>
    public Exception? SaveFailure { get; set; }

    public List<AppSettings> Saved { get; } = [];

    public int PacketSubscriptions => Volatile.Read(ref _packetSubscriptions);

    public void Push(PacketView packet) => _packets.Writer.TryWrite(packet);

    public void Push(StationBatch batch) => _stations.Writer.TryWrite(batch);

    /// <summary>Ends the current packet stream, as a gateway restart would.</summary>
    public void EndPackets()
    {
        var ended = _packets;
        _packets = Channel.CreateUnbounded<PacketView>();
        ended.Writer.TryComplete();
    }

    /// <summary>Ends the current station stream, as a gateway restart would.</summary>
    public void EndStations()
    {
        var ended = _stations;
        _stations = Channel.CreateUnbounded<StationBatch>();
        ended.Writer.TryComplete();
    }

    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        StatusFailure is { } failure ? Task.FromException<EngineStatus>(failure) : Task.FromResult(Status);

    public async IAsyncEnumerable<PacketView> WatchPacketsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var packets = _packets;
        Interlocked.Increment(ref _packetSubscriptions);
        await foreach (var packet in packets.Reader.ReadAllAsync(cancellationToken))
        {
            yield return packet;
        }
    }

    public async IAsyncEnumerable<StationBatch> WatchStationsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var stations = _stations;
        Interlocked.Increment(ref _stationSubscriptions);
        await foreach (var batch in stations.Reader.ReadAllAsync(cancellationToken))
        {
            yield return batch;
        }
    }

    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (SaveFailure is { } failure)
        {
            return Task.FromException(failure);
        }
        Saved.Add(settings);
        Settings = settings;
        Status = Status with { ProfileId = settings.ActiveProfileId };
        return Task.CompletedTask;
    }

    public static AppSettings TwoProfiles() =>
        new(
            [
                new("home", "Home", "N0CALL", -1, "rotate.aprs2.net:14580", null, null, null),
                new("portable", "Portable", "N0CALL-7", -1, "rotate.aprs2.net:14580", null, null, null),
            ],
            "home",
            UnitSettings.Metric
        );

    public static PacketView Packet(string source) =>
        new(DateTimeOffset.UnixEpoch, source, "Status", "Status", $"{source}>APRS:>hi");

    public static StationState Station(string name) => new(name, DateTimeOffset.UnixEpoch, 49.0, -72.0, "/>");
}
