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

    /// <summary>The filters <see cref="WatchStationsAsync"/> was asked with, in order.</summary>
    public List<StationFilter?> StationFilters { get; } = [];

    /// <summary>What <see cref="GetViewFacetsAsync"/> answers.</summary>
    public ViewFacets Facets { get; set; } = new([], [], [], []);

    public Task<ViewFacets> GetViewFacetsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Facets);

    public async IAsyncEnumerable<StationBatch> WatchStationsAsync(
        StationFilter? filter = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        StationFilters.Add(filter);
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

    /// <summary>The map views reported, in order.</summary>
    public List<MapViewReport> MapViews { get; } = [];

    public Task SetMapViewAsync(MapViewReport view, CancellationToken cancellationToken = default)
    {
        MapViews.Add(view);
        return Task.CompletedTask;
    }

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

    /// <summary>The sources <see cref="SetSourceEnabledAsync"/> switched, in order.</summary>
    public List<(string Id, bool Enabled)> SourcesSwitched { get; } = [];

    /// <summary>The sources <see cref="ReconnectSourceAsync"/> was asked to reconnect, in order.</summary>
    public List<string> SourcesReconnected { get; } = [];

    /// <summary>When set, <see cref="SetSourceEnabledAsync"/> throws it (a gateway viewer without sign-in).</summary>
    public Exception? SourceSwitchFailure { get; set; }

    public Task SetSourceEnabledAsync(string sourceId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (SourceSwitchFailure is { } failure)
        {
            return Task.FromException(failure);
        }
        SourcesSwitched.Add((sourceId, enabled));
        Settings = Settings with { Toggles = Settings.EffectiveToggles.WithSource(sourceId, enabled) };
        return Task.CompletedTask;
    }

    public Task ReconnectSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        SourcesReconnected.Add(sourceId);
        return Task.CompletedTask;
    }

    /// <summary>The serial ports <see cref="GetSerialPortsAsync"/> gives.</summary>
    public List<string> SerialPorts { get; } = ["/dev/ttyUSB0"];

    public Task<IReadOnlyList<string>> GetSerialPortsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(SerialPorts);

    /// <summary>The traffic <see cref="WatchSourceTrafficAsync"/> gives, by source id; it then waits.</summary>
    public Dictionary<string, List<TrafficEntry>> Traffic { get; } = [];

    /// <summary>New traffic for a watcher, after what <see cref="Traffic"/> held.</summary>
    public System.Threading.Channels.Channel<TrafficEntry> LiveTraffic { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<TrafficEntry>();

    public async IAsyncEnumerable<TrafficEntry> WatchSourceTrafficAsync(
        string sourceId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var entry in Traffic.GetValueOrDefault(sourceId) ?? [])
        {
            yield return entry;
        }
        await foreach (var entry in LiveTraffic.Reader.ReadAllAsync(cancellationToken))
        {
            if (entry.SourceId == sourceId)
            {
                yield return entry;
            }
        }
    }

    /// <summary>Manual mode as the console set it, by source id.</summary>
    public Dictionary<string, bool> Manual { get; } = [];

    public Task SetTncManualAsync(string sourceId, bool manual, CancellationToken cancellationToken = default)
    {
        Manual[sourceId] = manual;
        return Task.CompletedTask;
    }

    /// <summary>The TNC commands sent, in order.</summary>
    public List<(string SourceId, TncCommand Command)> TncCommands { get; } = [];

    /// <summary>When set, <see cref="SendTncCommandAsync"/> throws it.</summary>
    public Exception? TncCommandFailure { get; set; }

    public Task SendTncCommandAsync(string sourceId, TncCommand command, CancellationToken cancellationToken = default)
    {
        if (TncCommandFailure is { } failure)
        {
            return Task.FromException(failure);
        }
        TncCommands.Add((sourceId, command));
        return Task.CompletedTask;
    }

    /// <summary>Bulletins for <see cref="WatchBulletinsAsync"/>: written here, read by the watcher.</summary>
    public System.Threading.Channels.Channel<BulletinView> Bulletins { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<BulletinView>();

    public IAsyncEnumerable<BulletinView> WatchBulletinsAsync(CancellationToken cancellationToken = default) =>
        Bulletins.Reader.ReadAllAsync(cancellationToken);

    /// <summary>What <see cref="GetDestinationAsync"/> gives, by destination; <see cref="DestinationInfo.Unknown"/> otherwise.</summary>
    public Dictionary<(double, double), DestinationInfo> Destinations { get; } = [];

    public Task<DestinationInfo> GetDestinationAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(Destinations.GetValueOrDefault((latitude, longitude)) ?? DestinationInfo.Unknown);

    /// <summary>What <see cref="GetCaptureAsync"/> gives.</summary>
    public CaptureView Capture { get; set; } = new([], [], []);

    /// <summary>The queries asked, in order.</summary>
    public List<CaptureQuery> CaptureQueries { get; } = [];

    public Task<CaptureView> GetCaptureAsync(CaptureQuery query, CancellationToken cancellationToken = default)
    {
        CaptureQueries.Add(query);
        return Task.FromResult(Capture);
    }

    /// <summary>The exports asked for, in order.</summary>
    public List<CaptureExport> CaptureExports { get; } = [];

    public Task<CaptureExportResult> ExportCaptureAsync(
        CaptureExport request,
        CancellationToken cancellationToken = default
    )
    {
        CaptureExports.Add(request);
        return Task.FromResult(
            new CaptureExportResult("capture-2026-10-18.log", "2026-10-18T12:00:00.000Z Q0AAA>APRS:x\n", "note", 1)
        );
    }

    /// <summary>How many times capture was cleared.</summary>
    public int CaptureCleared { get; private set; }

    public Task ClearCaptureAsync(CancellationToken cancellationToken = default)
    {
        CaptureCleared++;
        Capture = new([], [], []);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TncTypeInfo>> GetTncTypesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TncTypeInfo>>([new TncTypeInfo("th-d72", "Kenwood TH-D72")]);

    /// <summary>The toggles <see cref="SetToggleAsync"/> set, in order.</summary>
    public List<(Toggle Toggle, bool Enabled)> Toggled { get; } = [];

    /// <summary>When set, <see cref="SetToggleAsync"/> throws it (a gateway viewer without sign-in).</summary>
    public Exception? ToggleFailure { get; set; }

    public Task SetToggleAsync(Toggle toggle, bool enabled, CancellationToken cancellationToken = default)
    {
        if (ToggleFailure is { } failure)
        {
            return Task.FromException(failure);
        }
        Toggled.Add((toggle, enabled));
        var toggles = Settings.EffectiveToggles;
        Settings = Settings with
        {
            Toggles = toggle switch
            {
                Toggle.Internet => toggles with { Internet = enabled },
                Toggle.AprsIs => toggles with { AprsIs = enabled },
                _ => toggles with { Tiles = enabled },
            },
        };
        return Task.CompletedTask;
    }

    /// <summary>What <see cref="GetClearCountsAsync"/> answers.</summary>
    public ClearCounts ClearCounts { get; set; } = new(0, 0, 0, 0);

    /// <summary>The scopes <see cref="ClearAsync"/> cleared, in order.</summary>
    public List<ClearScope> Cleared { get; } = [];

    /// <summary>When set, <see cref="ClearAsync"/> throws it.</summary>
    public Exception? ClearFailure { get; set; }

    public Task<ClearCounts> GetClearCountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ClearCounts);

    public Task<int> ClearAsync(ClearScope scope, CancellationToken cancellationToken = default)
    {
        if (ClearFailure is { } failure)
        {
            return Task.FromException<int>(failure);
        }
        Cleared.Add(scope);
        return Task.FromResult(1);
    }

    /// <summary>What <see cref="GetDashboardAsync"/> answers; an empty hour by default.</summary>
    public Dashboard? Dashboard { get; set; }

    /// <summary>The ranges <see cref="GetDashboardAsync"/> was asked for, in order.</summary>
    public List<DashboardRange> DashboardRanges { get; } = [];

    public Task<Dashboard> GetDashboardAsync(DashboardRange range, CancellationToken cancellationToken = default)
    {
        DashboardRanges.Add(range);
        return Task.FromResult(Dashboard ?? EmptyDashboard(range));
    }

    /// <summary>A dashboard with nothing counted: 60 zero points a minute from the epoch.</summary>
    public static Dashboard EmptyDashboard(DashboardRange range = DashboardRange.Hour)
    {
        int[] zeros = [.. Enumerable.Repeat(0, 60)];
        return new Dashboard(
            DateTimeOffset.UnixEpoch.AddHours(1),
            range,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UnixEpoch,
            zeros,
            0,
            [],
            zeros,
            0,
            zeros,
            0,
            [],
            [],
            [],
            zeros,
            0,
            [],
            [],
            []
        );
    }

    /// <summary>What <see cref="GetNetworkLinksAsync"/> answers.</summary>
    public List<NetworkLink> Links { get; } = [];

    public Task<IReadOnlyList<NetworkLink>> GetNetworkLinksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NetworkLink>>([.. Links]);

    /// <summary>What <see cref="GetTelemetryAsync"/> answers, by station; none by default.</summary>
    public Dictionary<string, TelemetryView> Telemetry { get; } = [];

    public Task<TelemetryView?> GetTelemetryAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Telemetry.GetValueOrDefault(name));

    /// <summary>What <see cref="GetRouteAsync"/> answers, by station; none by default.</summary>
    public Dictionary<string, PacketRoute> Routes { get; } = [];

    /// <summary>The queries <see cref="GetRouteAsync"/> was asked, in order.</summary>
    public List<RouteQuery> RouteQueries { get; } = [];

    public Task<PacketRoute?> GetRouteAsync(RouteQuery query, CancellationToken cancellationToken = default)
    {
        RouteQueries.Add(query);
        return Task.FromResult(Routes.GetValueOrDefault(query.Name));
    }

    /// <summary>How many times <see cref="SendPositionAsync"/> was called.</summary>
    public int PositionsSent { get; private set; }

    /// <summary>When set, <see cref="SendPositionAsync"/> throws it.</summary>
    public Exception? SendFailure { get; set; }

    /// <summary>The source each <see cref="SendPositionAsync"/> was asked for (null for all), in order.</summary>
    public List<string?> PositionSources { get; } = [];

    public Task<IReadOnlyList<PacketView>> SendPositionAsync(
        string? sourceId = null,
        CancellationToken cancellationToken = default
    )
    {
        if (SendFailure is { } failure)
        {
            return Task.FromException<IReadOnlyList<PacketView>>(failure);
        }
        PositionsSent++;
        PositionSources.Add(sourceId);
        return Task.FromResult<IReadOnlyList<PacketView>>([SentPacket(sourceId)]);
    }

    private static PacketView SentPacket(string? sourceId) =>
        new(
            DateTimeOffset.UnixEpoch,
            "N0TEST",
            "Position",
            "Position",
            "N0TEST>APZAMP:!",
            Markers: PacketMarkers.Sent,
            SourceId: sourceId
        );

    /// <summary>What <see cref="TestSendAsync"/> answers: heard back through this station, or not.</summary>
    public string? TestHeardThrough { get; set; }

    /// <summary>The sources <see cref="TestSendAsync"/> sent on, in order.</summary>
    public List<string> TestSends { get; } = [];

    public Task<TestSendResult> TestSendAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        if (SendFailure is { } failure)
        {
            return Task.FromException<TestSendResult>(failure);
        }
        TestSends.Add(sourceId);
        return Task.FromResult(new TestSendResult(SentPacket(sourceId), TestHeardThrough));
    }

    public Task<string> NewListenerIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("SWL123456");

    /// <summary>What <see cref="GetConversationsAsync"/> answers.</summary>
    public List<Conversation> Conversations { get; } = [];

    private Channel<ChatMessage> _messages = Channel.CreateUnbounded<ChatMessage>();

    /// <summary>Streams <paramref name="message"/> to the UI, as the engine would.</summary>
    public void Push(ChatMessage message) => _messages.Writer.TryWrite(message);

    /// <summary>The messages <see cref="SendMessageAsync"/> was asked to send, in order.</summary>
    public List<(string To, string Text)> SentMessages { get; } = [];

    /// <summary>The conversations marked read, in order.</summary>
    public List<string> MarkedRead { get; } = [];

    /// <summary>The settings each <see cref="SetConversationAsync"/> made.</summary>
    public List<(string Correspondent, SendVia Via, MessageRetries Retries)> ConversationSettings { get; } = [];

    /// <summary>The ids cancelled and resent.</summary>
    public List<string> Cancelled { get; } = [];

    public List<string> Resent { get; } = [];

    public Task<IReadOnlyList<Conversation>> GetConversationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Conversation>>([.. Conversations]);

    public async IAsyncEnumerable<ChatMessage> WatchMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (var message in _messages.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }

    public Task<IReadOnlyList<ChatMessage>> SendMessageAsync(
        string correspondent,
        string text,
        CancellationToken cancellationToken = default
    )
    {
        if (SendFailure is { } failure)
        {
            return Task.FromException<IReadOnlyList<ChatMessage>>(failure);
        }
        SentMessages.Add((correspondent, text));
        var message = new ChatMessage(
            $"m{SentMessages.Count}",
            correspondent,
            Outgoing: true,
            text,
            DateTimeOffset.UnixEpoch,
            ChatMessageState.Queued
        );
        Push(message);
        return Task.FromResult<IReadOnlyList<ChatMessage>>([message]);
    }

    public Task CancelMessageAsync(string id, CancellationToken cancellationToken = default)
    {
        Cancelled.Add(id);
        return Task.CompletedTask;
    }

    public Task ResendMessageAsync(string id, CancellationToken cancellationToken = default)
    {
        Resent.Add(id);
        return Task.CompletedTask;
    }

    public Task MarkReadAsync(string correspondent, CancellationToken cancellationToken = default)
    {
        MarkedRead.Add(correspondent);
        return Task.CompletedTask;
    }

    public Task SetConversationAsync(
        string correspondent,
        SendVia sendVia,
        MessageRetries retries,
        CancellationToken cancellationToken = default
    )
    {
        ConversationSettings.Add((correspondent, sendVia, retries));
        return Task.CompletedTask;
    }

    /// <summary>The stations whose track <see cref="ClearTrackAsync"/> cleared, in order.</summary>
    public List<string> ClearedTracks { get; } = [];

    public Task ClearTrackAsync(string name, CancellationToken cancellationToken = default)
    {
        ClearedTracks.Add(name);
        return Task.CompletedTask;
    }

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

    /// <summary>The APRS-IS source the test profiles use.</summary>
    public static SourceSettings Rotate { get; } =
        new("rotate", "rotate.aprs2.net", SourceKind.AprsIs, "rotate.aprs2.net:14580");

    public static AppSettings TwoProfiles() =>
        new(
            [
                new("home", "Home", "N0CALL", -1, [Rotate.Id], null, null),
                new("portable", "Portable", "N0CALL-7", -1, [Rotate.Id], null, null),
            ],
            "home",
            UnitSettings.Metric,
            Sources: [Rotate]
        );

    public static PacketView Packet(string source) =>
        new(DateTimeOffset.UnixEpoch, source, "Status", "Status", $"{source}>APRS:>hi");

    public static StationState Station(string name) => new(name, DateTimeOffset.UnixEpoch, 49.0, -72.0, "/>");
}
