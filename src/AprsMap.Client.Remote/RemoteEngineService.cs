// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AprsMap.Client.Remote;

/// <summary>
/// <see cref="IEngineService"/> backed by a gateway's SignalR hub. The UI cannot tell it apart from the in-process
/// engine, which is what keeps the desktop and web apps identical.
/// </summary>
public sealed class RemoteEngineService : IEngineService, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ContractCheck? _contract;

    /// <summary>
    /// Wraps an existing hub connection (useful for custom transports and tests); <paramref name="contract"/>, when it
    /// is the connection's message handler, says when the gateway has moved to another contract version.
    /// </summary>
    public RemoteEngineService(HubConnection connection, ContractCheck? contract = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _contract = contract;
    }

    /// <summary>Creates a client for the gateway at <paramref name="gatewayBaseUri"/>, reconnecting automatically.</summary>
    public static RemoteEngineService Create(Uri gatewayBaseUri)
    {
        ArgumentNullException.ThrowIfNull(gatewayBaseUri);
        var contract = new ContractCheck();
        var connection = new HubConnectionBuilder()
            .WithUrl(HubUri(gatewayBaseUri), o => o.HttpMessageHandlerFactory = contract.Wrap)
            .WithAutomaticReconnect(new KeepTryingPolicy())
            // The browser app has no reflection-based JSON (WebAssembly publish turns it off).
            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, EngineJsonContext.Default))
            .Build();
        return new RemoteEngineService(connection, contract);
    }

    /// <summary>
    /// The hub's address under <paramref name="gatewayBaseUri"/>, keeping its path (<c>https://host/aprs/</c> gives
    /// <c>https://host/aprs/hubs/engine</c>, for a gateway behind a proxy path), with this app's contract version.
    /// </summary>
    public static Uri HubUri(Uri gatewayBaseUri)
    {
        ArgumentNullException.ThrowIfNull(gatewayBaseUri);
        var path = gatewayBaseUri.AbsolutePath.EndsWith('/') ? gatewayBaseUri : new Uri(gatewayBaseUri + "/");
        var hub = new Uri(path, EngineHub.Path.TrimStart('/'));
        return new Uri(
            hub,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"?{EngineHub.ContractQuery}={EngineHub.ContractVersion}"
            )
        );
    }

    /// <summary>The underlying connection state.</summary>
    public HubConnectionState State => _connection.State;

    /// <summary>Connects to the gateway. Call once before using the service.</summary>
    /// <remarks>
    /// A gateway at its viewer limit refuses with <see cref="EngineErrorCodes.GatewayBusy"/> (F1.14 behavior 3), and
    /// one on another contract version with <see cref="EngineErrorCodes.GatewayOutdated"/> (behavior 8).
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            throw EngineErrors.Create(EngineErrorCodes.GatewayBusy, EngineHub.BusyMessage, innerException: ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw EngineErrors.Create(EngineErrorCodes.GatewayOutdated, EngineHub.OutdatedMessage, innerException: ex);
        }
    }

    /// <inheritdoc />
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<EngineStatus>(EngineHub.GetStatus, cancellationToken));

    /// <inheritdoc />
    public IAsyncEnumerable<PacketView> WatchPacketsAsync(
        PacketFilter? filter = null,
        CancellationToken cancellationToken = default
    ) => _connection.StreamAsync<PacketView>(EngineHub.WatchPackets, filter, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<StationBatch> WatchStationsAsync(
        StationFilter? filter = null,
        CancellationToken cancellationToken = default
    ) => _connection.StreamAsync<StationBatch>(EngineHub.WatchStations, filter, cancellationToken);

    /// <inheritdoc />
    public Task SetToggleAsync(Toggle toggle, bool enabled, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.SetToggle, toggle, enabled, cancellationToken));

    /// <inheritdoc />
    public Task<ViewFacets> GetViewFacetsAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<ViewFacets>(EngineHub.GetViewFacets, cancellationToken));

    /// <inheritdoc />
    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<AppSettings>(EngineHub.GetSettings, cancellationToken));

    /// <inheritdoc />
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.SaveSettings, settings, cancellationToken));

    /// <inheritdoc />
    public Task<StationDetail?> GetStationAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return WithEngineErrors(() =>
            _connection.InvokeAsync<StationDetail?>(EngineHub.GetStation, name, cancellationToken)
        );
    }

    /// <inheritdoc />
    public Task SetMapViewAsync(MapViewReport view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        return WithEngineErrors(() => _connection.InvokeAsync(EngineHub.SetMapView, view, cancellationToken));
    }

    /// <inheritdoc />
    public Task<StationPage> QueryStationsAsync(StationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return WithEngineErrors(() =>
            _connection.InvokeAsync<StationPage>(EngineHub.QueryStations, query, cancellationToken)
        );
    }

    /// <inheritdoc />
    public Task ResetSettingsAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.ResetSettings, cancellationToken));

    /// <inheritdoc />
    public Task<ClearCounts> GetClearCountsAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<ClearCounts>(EngineHub.GetClearCounts, cancellationToken));

    /// <inheritdoc />
    public Task<int> ClearAsync(ClearScope scope, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<int>(EngineHub.Clear, scope, cancellationToken));

    /// <inheritdoc />
    public Task<Dashboard> GetDashboardAsync(
        DashboardRange range,
        StationFilter? filter = null,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<Dashboard>(EngineHub.GetDashboard, range, filter, cancellationToken)
        );

    /// <inheritdoc />
    public Task SetSourceEnabledAsync(string sourceId, bool enabled, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync(EngineHub.SetSourceEnabled, sourceId, enabled, cancellationToken)
        );

    /// <inheritdoc />
    public Task ReconnectSourceAsync(string sourceId, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.ReconnectSource, sourceId, cancellationToken));

    /// <inheritdoc />
    public IAsyncEnumerable<TrafficEntry> WatchSourceTrafficAsync(
        string sourceId,
        CancellationToken cancellationToken = default
    ) =>
        StreamWithEngineErrors(
            _connection.StreamAsync<TrafficEntry>(EngineHub.WatchSourceTraffic, sourceId, cancellationToken),
            cancellationToken
        );

    /// <summary>
    /// A stream whose refusal arrives as an <see cref="EngineError"/> exception, as calls' do: a stream method's
    /// <see cref="HubException"/> carries the error inside SignalR's own wording.
    /// </summary>
    private static async IAsyncEnumerable<T> StreamWithEngineErrors<T>(
        IAsyncEnumerable<T> stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var items = stream.WithCancellation(cancellationToken).ConfigureAwait(false).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                try
                {
                    if (!await items.MoveNextAsync())
                    {
                        yield break;
                    }
                }
                catch (HubException ex) when (EngineHub.TryDecodeError(ex.Message, out var error))
                {
                    throw EngineErrors.ToException(error, ex);
                }
                yield return items.Current;
            }
        }
        finally
        {
            await items.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public Task SetTncManualAsync(string sourceId, bool manual, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.SetTncManual, sourceId, manual, cancellationToken));

    /// <inheritdoc />
    public Task SendTncCommandAsync(
        string sourceId,
        TncCommand command,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.SendTncCommand, sourceId, command, cancellationToken));

    /// <inheritdoc />
    public IAsyncEnumerable<BulletinView> WatchBulletinsAsync(CancellationToken cancellationToken = default) =>
        _connection.StreamAsync<BulletinView>(EngineHub.WatchBulletins, cancellationToken);

    /// <inheritdoc />
    public Task<UpdateStatus> GetUpdateStatusAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<UpdateStatus>(EngineHub.GetUpdateStatus, cancellationToken));

    /// <inheritdoc />
    public Task<LoggingStatus> GetLoggingStatusAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<LoggingStatus>(EngineHub.GetLoggingStatus, cancellationToken));

    /// <inheritdoc />
    public Task<ComponentReport> GetComponentsAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<ComponentReport>(EngineHub.GetComponents, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyList<StorageFolder>> GetStorageFoldersAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<StorageFolder>>(EngineHub.GetStorageFolders, cancellationToken)
        );

    /// <inheritdoc />
    public Task<UpdateStatus> CheckForUpdateAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<UpdateStatus>(EngineHub.CheckForUpdate, cancellationToken));

    /// <inheritdoc />
    public IAsyncEnumerable<UpdateStatus> WatchUpdateStatusAsync(CancellationToken cancellationToken = default) =>
        StreamWithEngineErrors(
            _connection.StreamAsync<UpdateStatus>(EngineHub.WatchUpdateStatus, cancellationToken),
            cancellationToken
        );

    /// <inheritdoc />
    public Task SkipUpdateAsync(string version, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.SkipUpdate, version, cancellationToken));

    /// <inheritdoc />
    public Task<BulletinView> PostBulletinAsync(BulletinPost post, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<BulletinView>(EngineHub.PostBulletin, post, cancellationToken));

    /// <inheritdoc />
    public Task StopBulletinAsync(string addressee, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.StopBulletin, addressee, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<IReadOnlyList<string>>(EngineHub.GetGroups, cancellationToken));

    /// <inheritdoc />
    public Task JoinGroupAsync(string group, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.JoinGroup, group, cancellationToken));

    /// <inheritdoc />
    public Task LeaveGroupAsync(string group, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.LeaveGroup, group, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessage>> PostToGroupAsync(
        string group,
        string text,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<ChatMessage>>(EngineHub.PostToGroup, group, text, cancellationToken)
        );

    /// <inheritdoc />
    public Task<DestinationInfo> GetDestinationAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<DestinationInfo>(EngineHub.GetDestination, latitude, longitude, cancellationToken)
        );

    /// <inheritdoc />
    public Task<CaptureView> GetCaptureAsync(CaptureQuery query, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<CaptureView>(EngineHub.GetCapture, query, cancellationToken));

    /// <inheritdoc />
    public Task<CaptureExportResult> ExportCaptureAsync(
        CaptureExport request,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<CaptureExportResult>(EngineHub.ExportCapture, request, cancellationToken)
        );

    /// <inheritdoc />
    public Task ClearCaptureAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.ClearCapture, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyList<TncTypeInfo>> GetTncTypesAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<TncTypeInfo>>(EngineHub.GetTncTypes, cancellationToken)
        );

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetSerialPortsAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<string>>(EngineHub.GetSerialPorts, cancellationToken)
        );

    /// <inheritdoc />
    public Task<IReadOnlyList<NetworkLink>> GetNetworkLinksAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<NetworkLink>>(EngineHub.GetNetworkLinks, cancellationToken)
        );

    /// <inheritdoc />
    public Task<TelemetryView?> GetTelemetryAsync(string name, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<TelemetryView?>(EngineHub.GetTelemetry, name, cancellationToken)
        );

    /// <inheritdoc />
    public Task<PacketRoute?> GetRouteAsync(RouteQuery query, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<PacketRoute?>(EngineHub.GetRoute, query, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyList<PacketRoute>> GetRoutesOutAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<PacketRoute>>(EngineHub.GetRoutesOut, cancellationToken)
        );

    /// <inheritdoc />
    public Task<IReadOnlyList<PacketView>> SendPositionAsync(
        string? sourceId = null,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<PacketView>>(EngineHub.SendPosition, sourceId, cancellationToken)
        );

    /// <inheritdoc />
    public Task<IReadOnlyList<PacketView>> SendStatusAsync(
        string? sourceId = null,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<PacketView>>(EngineHub.SendStatus, sourceId, cancellationToken)
        );

    /// <inheritdoc />
    public Task<IReadOnlyList<PacketView>> AskQruAsync(string group, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<PacketView>>(EngineHub.AskQru, group, cancellationToken)
        );

    /// <inheritdoc />
    public Task PlaceObjectAsync(OwnObject placed, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.PlaceObject, placed, cancellationToken));

    /// <inheritdoc />
    public Task RemoveObjectAsync(string id, bool kill, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.RemoveObject, id, kill, cancellationToken));

    /// <inheritdoc />
    public Task<TestSendResult> TestSendAsync(string sourceId, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<TestSendResult>(EngineHub.TestSend, sourceId, cancellationToken)
        );

    /// <inheritdoc />
    public Task<IReadOnlyList<Conversation>> GetConversationsAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<Conversation>>(EngineHub.GetConversations, cancellationToken)
        );

    /// <inheritdoc />
    public IAsyncEnumerable<ChatMessage> WatchMessagesAsync(CancellationToken cancellationToken = default) =>
        _connection.StreamAsync<ChatMessage>(EngineHub.WatchMessages, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessage>> SendMessageAsync(
        string correspondent,
        string text,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<ChatMessage>>(
                EngineHub.SendMessage,
                correspondent,
                text,
                cancellationToken
            )
        );

    /// <inheritdoc />
    public Task CancelMessageAsync(string id, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.CancelMessage, id, cancellationToken));

    /// <inheritdoc />
    public Task ResendMessageAsync(string id, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.ResendMessage, id, cancellationToken));

    /// <inheritdoc />
    public Task MarkReadAsync(string correspondent, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.MarkRead, correspondent, cancellationToken));

    /// <inheritdoc />
    public Task SetConversationAsync(
        string correspondent,
        SendVia sendVia,
        MessageRetries retries,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync(EngineHub.SetConversation, correspondent, sendVia, retries, cancellationToken)
        );

    /// <inheritdoc />
    public Task<string> NewListenerIdAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<string>(EngineHub.NewListenerId, cancellationToken));

    /// <inheritdoc />
    public Task ClearTrackAsync(string name, CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.ClearTrack, name, cancellationToken));

    /// <inheritdoc />
    public Task ReconnectAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync(EngineHub.Reconnect, cancellationToken));

    /// <inheritdoc />
    public Task<GeocodingInfo> GetGeocodingInfoAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() => _connection.InvokeAsync<GeocodingInfo>(EngineHub.GetGeocodingInfo, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(
        string query,
        string? language,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        return WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<GeocodeResult>>(EngineHub.Geocode, query, language, cancellationToken)
        );
    }

    /// <inheritdoc />
    public Task<int> ComputePasscodeAsync(string callsign, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        return WithEngineErrors(() =>
            _connection.InvokeAsync<int>(EngineHub.ComputePasscode, callsign, cancellationToken)
        );
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServerPreset>> GetServerPresetsAsync(CancellationToken cancellationToken = default) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<IReadOnlyList<ServerPreset>>(EngineHub.GetServerPresets, cancellationToken)
        );

    /// <inheritdoc />
    public Task<LocatorInfo> ParseLocatorAsync(string locator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return WithEngineErrors(() =>
            _connection.InvokeAsync<LocatorInfo>(EngineHub.ParseLocator, locator, cancellationToken)
        );
    }

    /// <inheritdoc />
    public Task<string> GetLocatorAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default
    ) =>
        WithEngineErrors(() =>
            _connection.InvokeAsync<string>(EngineHub.GetLocator, latitude, longitude, cancellationToken)
        );

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>
    /// Raises the gateway's <see cref="EngineError"/> as the same exception the in-process engine throws; any failure
    /// after the gateway moved to another contract version is <see cref="EngineErrorCodes.GatewayOutdated"/>.
    /// </summary>
    private async Task WithEngineErrors(Func<Task> call) =>
        await WithEngineErrors(async () =>
            {
                await call().ConfigureAwait(false);
                return true;
            })
            .ConfigureAwait(false);

    /// <inheritdoc cref="WithEngineErrors(Func{Task})"/>
    private async Task<T> WithEngineErrors<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (HubException ex) when (EngineHub.TryDecodeError(ex.Message, out var error))
        {
            throw EngineErrors.ToException(error, ex);
        }
        catch (Exception ex) when (_contract?.IsOutdated == true && ex is not OperationCanceledException)
        {
            throw EngineErrors.Create(EngineErrorCodes.GatewayOutdated, EngineHub.OutdatedMessage, innerException: ex);
        }
    }

    /// <summary>
    /// Reconnects quickly at first, then every 30 seconds for as long as it takes. SignalR's default gives up after
    /// four attempts, which would strand a browser while its gateway restarts or a Pi reboots.
    /// </summary>
    internal sealed class KeepTryingPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] FirstDelays =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < FirstDelays.Length
                ? FirstDelays[retryContext.PreviousRetryCount]
                : TimeSpan.FromSeconds(30);
    }
}
