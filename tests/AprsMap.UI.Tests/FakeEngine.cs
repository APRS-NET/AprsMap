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

    /// <summary>The filter of the latest packet stream.</summary>
    public PacketFilter? PacketFilter { get; private set; }

    public async IAsyncEnumerable<PacketView> WatchPacketsAsync(
        PacketFilter? filter = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var packets = _packets;
        PacketFilter = filter;
        Interlocked.Increment(ref _packetSubscriptions);
        await foreach (var packet in packets.Reader.ReadAllAsync(cancellationToken))
        {
            if (filter?.Matches(packet) ?? true)
            {
                yield return packet;
            }
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

    public GeocodingInfo Geocoding { get; set; } = new(true, "nominatim.example", "https://nominatim.example/policy");

    /// <summary>What <see cref="GeocodeAsync"/> returns, or throws when it is an exception.</summary>
    public Func<string, IReadOnlyList<GeocodeResult>> Geocoder { get; set; } = _ => [];

    public List<(string Query, string? Language)> Lookups { get; } = [];

    public Dictionary<string, StationDetail> Details { get; } = [];

    /// <summary>A station with every detail line known: digipeated, with a device, motion, comment and status.</summary>
    public static StationDetail Detail(string name, DateTimeOffset heard) =>
        new(
            name,
            StationKind.Station,
            heard,
            12,
            false,
            40.015,
            -105.2705,
            0,
            "/>",
            "Heading home",
            "Net tonight",
            88,
            66.672,
            1609.344,
            "N2DIGI*,WIDE2-1,qAR,N1GATE",
            null,
            null,
            100,
            45,
            "DN70kb",
            new StationDevice("Yaesu", "FTM-500D", "Rig"),
            new StationArrival(ArrivalKind.Digipeated, ["N2DIGI"], "N1GATE"),
            $"{name}>APY500,N2DIGI*,WIDE2-1,qAR,N1GATE:!4000.90N/10516.23W>088/036/A=005280Heading home"
        );

    /// <summary>What <see cref="QueryStationsAsync"/> returns; the last query is kept in <see cref="LastQuery"/>.</summary>
    public StationPage StationPage { get; set; } = new([], 0);

    public StationQuery? LastQuery { get; private set; }

    public int Queries { get; private set; }

    public Task<StationPage> QueryStationsAsync(StationQuery query, CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        Queries++;
        return Task.FromResult(StationPage);
    }

    public Task<StationDetail?> GetStationAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Details.GetValueOrDefault(name));

    public int Resets { get; private set; }

    public Task ResetSettingsAsync(CancellationToken cancellationToken = default)
    {
        Resets++;
        Settings = AppSettings.Empty;
        return Task.CompletedTask;
    }

    public int Reconnects { get; private set; }

    /// <summary>When set, <see cref="ReconnectAsync"/> throws it.</summary>
    public Exception? ReconnectFailure { get; set; }

    public Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (ReconnectFailure is { } failure)
        {
            return Task.FromException(failure);
        }
        Reconnects++;
        return Task.CompletedTask;
    }

    public Task<GeocodingInfo> GetGeocodingInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Geocoding);

    public Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(
        string query,
        string? language,
        CancellationToken cancellationToken = default
    )
    {
        Lookups.Add((query, language));
        try
        {
            return Task.FromResult(Geocoder(query));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromException<IReadOnlyList<GeocodeResult>>(ex);
        }
    }

    /// <summary>Not the real algorithm: the UI only shows what the engine returns.</summary>
    public Task<int> ComputePasscodeAsync(string callsign, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(callsign)
            ? Task.FromException<int>(new ArgumentException("The callsign is empty.", nameof(callsign)))
            : Task.FromResult(callsign.Split('-')[0].Length * 1000);

    public IReadOnlyList<ServerPreset> ServerPresets { get; set; } =
    [
        new("rotate", "Worldwide", "rotate.aprs2.net:14580"),
        new("noam", "North America", "noam.aprs2.net:14580"),
        new("euro", "Europe and Africa", "euro.aprs2.net:14580"),
    ];

    public Task<IReadOnlyList<ServerPreset>> GetServerPresetsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ServerPresets);

    /// <summary>Knows only DN70, DN70kb and FN31pr; anything else is refused as the real engine would.</summary>
    public Task<LocatorInfo> ParseLocatorAsync(string locator, CancellationToken cancellationToken = default) =>
        locator.Trim().ToUpperInvariant() switch
        {
            "DN70" => Task.FromResult(new LocatorInfo("DN70", 40.5, -105.0)),
            "DN70KB" => Task.FromResult(new LocatorInfo("DN70kb", 40.0625, -105.125)),
            "FN31PR" => Task.FromResult(new LocatorInfo("FN31pr", 41.7291667, -72.7083333)),
            "" => Task.FromException<LocatorInfo>(
                EngineErrors.Create(EngineErrorCodes.LocatorEmpty, "Enter a locator.")
            ),
            _ => Task.FromException<LocatorInfo>(
                EngineErrors.Create(
                    EngineErrorCodes.LocatorWrongLength,
                    "A locator has 4, 6, 8 or 10 characters.",
                    new Dictionary<string, string> { [EngineErrorArgs.Value] = locator }
                )
            ),
        };

    public Task<string> GetLocatorAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(latitude > 40 ? "DN70kb" : "DM79xx");

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
