// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AprsMap.Gateway;

/// <summary>Exposes the in-process <see cref="IEngineService"/> to remote UIs. Method names come from <see cref="EngineHub"/>.</summary>
internal sealed class EngineHubEndpoint(
    IEngineService engine,
    IOptions<GatewayOptions> options,
    Viewers viewers,
    Core.Engine filterEngine,
    OperatorStore operators
) : Hub
{
    /// <summary>The widest a viewer's map part may be on a gateway that follows viewers (F1.2 behavior 9).</summary>
    internal const double MaxViewerViewKm = 500;

    /// <summary>
    /// A signed-in operator (docs/features/F2.9-operator-sign-in.md, behavior 13): the session the connection was opened
    /// with decides, checked by <see cref="OperatorFilter"/> on every call; nothing the browser sends does.
    /// </summary>
    private bool IsOperator => Context.Items.ContainsKey(OperatorFilter.OperatorKey);

    /// <summary>
    /// Who may change the station: a signed-in operator, or with no operator set up, every browser on a gateway that
    /// allows settings changes (F2.9 behavior 17).
    /// </summary>
    private bool MayChangeStation => IsOperator || (!operators.Any && options.Value.AllowSettingsChanges);

    [HubMethodName(EngineHub.GetStatus)]
    public async Task<EngineStatus> GetStatus() =>
        (await engine.GetStatusAsync(Context.ConnectionAborted).ConfigureAwait(false)) with
        {
            Viewers = viewers.Count,
        };

    [HubMethodName(EngineHub.WatchPackets)]
    public IAsyncEnumerable<PacketView> WatchPackets(PacketFilter? filter, CancellationToken cancellationToken) =>
        engine.WatchPacketsAsync(filter, cancellationToken);

    [HubMethodName(EngineHub.WatchStations)]
    public IAsyncEnumerable<StationBatch> WatchStations(StationFilter? filter, CancellationToken cancellationToken) =>
        engine.WatchStationsAsync(filter, cancellationToken);

    /// <summary>
    /// A toggle changes the station for every viewer, so it has the settings gate (F1.15 behavior 6). Turning Transmit
    /// on needs a signed-in operator (F2.1 behavior 9, F2.9 behavior 12).
    /// </summary>
    [HubMethodName(EngineHub.SetToggle)]
    [Transmits]
    public async Task SetToggle(Toggle toggle, bool enabled)
    {
        if (toggle == Toggle.Transmit && enabled)
        {
            RequireOperator();
        }
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        try
        {
            await engine.SetToggleAsync(toggle, enabled, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ToHubException(ex) is { } hub)
        {
            throw hub;
        }
    }

    /// <summary>Sending needs a signed-in operator (F2.1 behavior 9, F2.9 behavior 12).</summary>
    [HubMethodName(EngineHub.SendPosition)]
    [Transmits]
    public Task<IReadOnlyList<PacketView>> SendPosition(string? sourceId)
    {
        RequireOperator();
        return engine.SendPositionAsync(sourceId, Context.ConnectionAborted);
    }

    /// <summary>Test send transmits too: a signed-in operator only (F2.1 behavior 8).</summary>
    [HubMethodName(EngineHub.TestSend)]
    [Transmits]
    public Task<TestSendResult> TestSend(string sourceId)
    {
        RequireOperator();
        return engine.TestSendAsync(sourceId, Context.ConnectionAborted);
    }

    /// <summary>
    /// The station's conversations are its operator's: only a signed-in operator reads or sends them
    /// (docs/features/F2.4-messaging.md, behavior 16).
    /// </summary>
    [HubMethodName(EngineHub.GetConversations)]
    public Task<IReadOnlyList<Conversation>> GetConversations()
    {
        RequireOperator();
        return engine.GetConversationsAsync(Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.WatchMessages)]
    public IAsyncEnumerable<ChatMessage> WatchMessages(CancellationToken cancellationToken)
    {
        RequireOperator();
        return engine.WatchMessagesAsync(cancellationToken);
    }

    [HubMethodName(EngineHub.SendMessage)]
    [Transmits]
    public Task<IReadOnlyList<ChatMessage>> SendMessage(string correspondent, string text)
    {
        RequireOperator();
        return engine.SendMessageAsync(correspondent, text, Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.CancelMessage)]
    public Task CancelMessage(string id)
    {
        RequireOperator();
        return engine.CancelMessageAsync(id, Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.ResendMessage)]
    [Transmits]
    public Task ResendMessage(string id)
    {
        RequireOperator();
        return engine.ResendMessageAsync(id, Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.MarkRead)]
    public Task MarkRead(string correspondent)
    {
        RequireOperator();
        return engine.MarkReadAsync(correspondent, Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.SetConversation)]
    public Task SetConversation(string correspondent, SendVia sendVia, MessageRetries retries)
    {
        RequireOperator();
        return engine.SetConversationAsync(correspondent, sendVia, retries, Context.ConnectionAborted);
    }

    /// <summary>A listener ID changes nothing until it's saved, which has the settings gate.</summary>
    [HubMethodName(EngineHub.NewListenerId)]
    public Task<string> NewListenerId() => engine.NewListenerIdAsync(Context.ConnectionAborted);

    /// <summary>
    /// Transmitting and the station's conversations need a signed-in operator, whatever <c>AllowSettingsChanges</c>
    /// says (F2.9 behavior 12); the engine's own rules still apply after (behavior 14).
    /// </summary>
    private void RequireOperator()
    {
        if (!IsOperator)
        {
            throw new HubException(
                EngineHub.EncodeError(new EngineError(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage))
            );
        }
    }

    [HubMethodName(EngineHub.GetViewFacets)]
    public Task<ViewFacets> GetViewFacets() => engine.GetViewFacetsAsync(Context.ConnectionAborted);

    [HubMethodName(EngineHub.GetClearCounts)]
    public Task<ClearCounts> GetClearCounts() => engine.GetClearCountsAsync(Context.ConnectionAborted);

    /// <summary>Clearing empties the store for every viewer, so it has the same gate as changing settings (F1.19 behavior 5).</summary>
    [HubMethodName(EngineHub.Clear)]
    public Task<int> Clear(ClearScope scope)
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.ClearAsync(scope, Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.GetDashboard)]
    public Task<Dashboard> GetDashboard(DashboardRange range) =>
        engine.GetDashboardAsync(range, Context.ConnectionAborted);

    [HubMethodName(EngineHub.GetNetworkLinks)]
    public Task<IReadOnlyList<NetworkLink>> GetNetworkLinks() => engine.GetNetworkLinksAsync(Context.ConnectionAborted);

    [HubMethodName(EngineHub.GetTelemetry)]
    public Task<TelemetryView?> GetTelemetry(string name) => engine.GetTelemetryAsync(name, Context.ConnectionAborted);

    [HubMethodName(EngineHub.GetRoute)]
    public Task<PacketRoute?> GetRoute(RouteQuery query) => engine.GetRouteAsync(query, Context.ConnectionAborted);

    [HubMethodName(EngineHub.GetRoutesOut)]
    public Task<IReadOnlyList<PacketRoute>> GetRoutesOut() => engine.GetRoutesOutAsync(Context.ConnectionAborted);

    [HubMethodName(EngineHub.GetStation)]
    public Task<StationDetail?> GetStation(string name) => engine.GetStationAsync(name, Context.ConnectionAborted);

    /// <summary>
    /// A browser's map view, for the station's filter (F1.2 behaviors 8 and 9): its followed station always, its map
    /// part only when this gateway follows viewers, and then at most <see cref="MaxViewerViewKm"/> wide.
    /// </summary>
    [HubMethodName(EngineHub.SetMapView)]
    public Task SetMapView(MapViewReport view)
    {
        ArgumentNullException.ThrowIfNull(view);
        filterEngine.SetMapView(
            Context.ConnectionId,
            options.Value.FollowViewers
                ? view with
                {
                    RadiusKm = Math.Min(view.RadiusKm, MaxViewerViewKm),
                }
                : view with
                {
                    Latitude = null,
                    Longitude = null,
                }
        );
        return Task.CompletedTask;
    }

    [HubMethodName(EngineHub.QueryStations)]
    public Task<StationPage> QueryStations(StationQuery query) =>
        engine.QueryStationsAsync(query, Context.ConnectionAborted);

    /// <summary>The settings; read-only, with passcodes hidden, unless this gateway allows settings changes.</summary>
    [HubMethodName(EngineHub.GetSettings)]
    public async Task<AppSettings> GetSettings()
    {
        var settings = await engine.GetSettingsAsync(Context.ConnectionAborted).ConfigureAwait(false);
        return MayChangeStation
            ? settings
            : settings with
            {
                ReadOnly = true,
                Profiles = [.. settings.Profiles.Select(p => p with { Passcode = -1 })],
            };
    }

    [HubMethodName(EngineHub.SaveSettings)]
    public async Task SaveSettings(AppSettings settings)
    {
        Require(EngineErrorCodes.SettingsReadOnly, EngineHub.SettingsReadOnlyMessage);
        try
        {
            await engine.SaveSettingsAsync(settings, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ToHubException(ex) is { } hub)
        {
            throw hub;
        }
    }

    /// <summary>Starting over deletes the station's settings for every viewer: the same gate as changing them.</summary>
    [HubMethodName(EngineHub.ResetSettings)]
    public Task ResetSettings()
    {
        Require(EngineErrorCodes.SettingsReadOnly, EngineHub.SettingsReadOnlyMessage);
        return engine.ResetSettingsAsync(Context.ConnectionAborted);
    }

    /// <summary>The track is every viewer's, so clearing it has the same gate as changing settings (F1.5 behavior 10).</summary>
    [HubMethodName(EngineHub.ClearTrack)]
    public Task ClearTrack(string name)
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.ClearTrackAsync(name, Context.ConnectionAborted);
    }

    /// <summary>Reconnecting affects every viewer, so it has the same gate as changing settings.</summary>
    [HubMethodName(EngineHub.Reconnect)]
    public Task Reconnect()
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.ReconnectAsync(Context.ConnectionAborted);
    }

    /// <summary>A source switched on or off changes the station for every viewer: the settings gate (F3.1 behavior 6).</summary>
    [HubMethodName(EngineHub.SetSourceEnabled)]
    public Task SetSourceEnabled(string sourceId, bool enabled)
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.SetSourceEnabledAsync(sourceId, enabled, Context.ConnectionAborted);
    }

    /// <summary>Reconnecting a source affects every viewer, so it has the same gate (F3.1 behavior 5).</summary>
    [HubMethodName(EngineHub.ReconnectSource)]
    public Task ReconnectSource(string sourceId)
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.ReconnectSourceAsync(sourceId, Context.ConnectionAborted);
    }

    /// <summary>
    /// The gateway host's serial ports are only listed where sources can be changed (F3.1 behavior 1.2, 12): the
    /// settings gate.
    /// </summary>
    [HubMethodName(EngineHub.GetSerialPorts)]
    public Task<IReadOnlyList<string>> GetSerialPorts()
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.GetSerialPortsAsync(Context.ConnectionAborted);
    }

    /// <summary>
    /// A source's traffic shows login lines, filters and what the radio sends: the operator's (F1.28 behavior 11).
    /// </summary>
    [HubMethodName(EngineHub.WatchSourceTraffic)]
    public IAsyncEnumerable<TrafficEntry> WatchSourceTraffic(string sourceId, CancellationToken cancellationToken)
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.WatchSourceTrafficAsync(sourceId, cancellationToken);
    }

    /// <summary>Manual mode is for typing to the TNC, which can key the radio: a signed-in operator only (F3.2 behavior 12).</summary>
    [HubMethodName(EngineHub.SetTncManual)]
    [Transmits]
    public Task SetTncManual(string sourceId, bool manual)
    {
        RequireOperator();
        return engine.SetTncManualAsync(sourceId, manual, Context.ConnectionAborted);
    }

    /// <summary>
    /// Switching a TNC into or out of KISS changes the station's source: the settings gate. A typed command can key the
    /// radio, so it needs a signed-in operator (F3.2 behaviors 11, 12).
    /// </summary>
    [HubMethodName(EngineHub.SendTncCommand)]
    [Transmits]
    public Task SendTncCommand(string sourceId, TncCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind is TncCommandKind.Line or TncCommandKind.Bytes)
        {
            RequireOperator();
        }
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.SendTncCommandAsync(sourceId, command, Context.ConnectionAborted);
    }

    /// <summary>Bulletins heard are public: every viewer may read them (F2.5 behavior 1).</summary>
    [HubMethodName(EngineHub.WatchBulletins)]
    public IAsyncEnumerable<BulletinView> WatchBulletins(CancellationToken cancellationToken) =>
        engine.WatchBulletinsAsync(cancellationToken);

    [HubMethodName(EngineHub.PostBulletin)]
    [Transmits]
    public Task<BulletinView> PostBulletin(BulletinPost post)
    {
        RequireOperator();
        return engine.PostBulletinAsync(post, Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.StopBulletin)]
    [Transmits]
    public Task StopBulletin(string addressee)
    {
        RequireOperator();
        return engine.StopBulletinAsync(addressee, Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.GetGroups)]
    public Task<IReadOnlyList<string>> GetGroups() => engine.GetGroupsAsync(Context.ConnectionAborted);

    [HubMethodName(EngineHub.JoinGroup)]
    [Transmits]
    public Task JoinGroup(string group)
    {
        RequireOperator();
        return engine.JoinGroupAsync(group, Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.LeaveGroup)]
    [Transmits]
    public Task LeaveGroup(string group)
    {
        RequireOperator();
        return engine.LeaveGroupAsync(group, Context.ConnectionAborted);
    }

    [HubMethodName(EngineHub.PostToGroup)]
    [Transmits]
    public Task<IReadOnlyList<ChatMessage>> PostToGroup(string group, string text)
    {
        RequireOperator();
        return engine.PostToGroupAsync(group, text, Context.ConnectionAborted);
    }

    /// <summary>Distance and bearing from the station's location to a viewer's destination (F1.18): open to every viewer.</summary>
    [HubMethodName(EngineHub.GetDestination)]
    public Task<DestinationInfo> GetDestination(double latitude, double longitude) =>
        engine.GetDestinationAsync(latitude, longitude, Context.ConnectionAborted);

    /// <summary>Capture is the station operator's (F1.27 behavior 11).</summary>
    [HubMethodName(EngineHub.GetCapture)]
    public Task<CaptureView> GetCapture(CaptureQuery query)
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.GetCaptureAsync(query, Context.ConnectionAborted);
    }

    /// <summary>Capture is the station operator's (F1.27 behavior 11).</summary>
    [HubMethodName(EngineHub.ExportCapture)]
    public Task<CaptureExportResult> ExportCapture(CaptureExport request)
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.ExportCaptureAsync(request, Context.ConnectionAborted);
    }

    /// <summary>Capture is the station operator's (F1.27 behavior 11).</summary>
    [HubMethodName(EngineHub.ClearCapture)]
    public Task ClearCapture()
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.ClearCaptureAsync(Context.ConnectionAborted);
    }

    /// <summary>The TNC types APRS.NET knows: the same for every gateway, so anyone may list them (F3.2 behavior 4).</summary>
    [HubMethodName(EngineHub.GetTncTypes)]
    public Task<IReadOnlyList<TncTypeInfo>> GetTncTypes() => engine.GetTncTypesAsync(Context.ConnectionAborted);

    /// <summary>Address lookup is offered only where settings can be changed, so the gateway isn't an open proxy.</summary>
    [HubMethodName(EngineHub.GetGeocodingInfo)]
    public async Task<GeocodingInfo> GetGeocodingInfo() =>
        MayChangeStation
            ? await engine.GetGeocodingInfoAsync(Context.ConnectionAborted).ConfigureAwait(false)
            : GeocodingInfo.Disabled;

    [HubMethodName(EngineHub.Geocode)]
    public async Task<IReadOnlyList<GeocodeResult>> Geocode(string query, string? language)
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        try
        {
            return await engine.GeocodeAsync(query, language, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ToHubException(ex) is { } hub)
        {
            throw hub;
        }
    }

    [HubMethodName(EngineHub.ComputePasscode)]
    public async Task<int> ComputePasscode(string callsign)
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        try
        {
            return await engine.ComputePasscodeAsync(callsign, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ToHubException(ex) is { } hub)
        {
            throw hub;
        }
    }

    [HubMethodName(EngineHub.GetServerPresets)]
    public Task<IReadOnlyList<ServerPreset>> GetServerPresets() =>
        engine.GetServerPresetsAsync(Context.ConnectionAborted);

    [HubMethodName(EngineHub.ParseLocator)]
    public async Task<LocatorInfo> ParseLocator(string locator)
    {
        try
        {
            return await engine.ParseLocatorAsync(locator, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ToHubException(ex) is { } hub)
        {
            throw hub;
        }
    }

    [HubMethodName(EngineHub.GetLocator)]
    public async Task<string> GetLocator(double latitude, double longitude)
    {
        try
        {
            return await engine.GetLocatorAsync(latitude, longitude, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ToHubException(ex) is { } hub)
        {
            throw hub;
        }
    }

    /// <summary>Refuses the call with <paramref name="code"/> unless this gateway allows settings changes.</summary>
    private void Require(string code, string message)
    {
        if (!MayChangeStation)
        {
            throw new HubException(EngineHub.EncodeError(new EngineError(code, message)));
        }
    }

    /// <summary>
    /// The hub error for an engine error the user should see. Only <see cref="HubException"/> messages reach the
    /// client, so the <see cref="EngineError"/> travels inside one as JSON; anything else stays a server error.
    /// </summary>
    private static HubException? ToHubException(Exception exception) =>
        exception switch
        {
            IHasEngineError coded => new HubException(EngineHub.EncodeError(coded.EngineError)),
            ArgumentException invalid => new HubException(
                EngineHub.EncodeError(new EngineError(EngineErrorCodes.Invalid, invalid.Message))
            ),
            _ => null,
        };
}
