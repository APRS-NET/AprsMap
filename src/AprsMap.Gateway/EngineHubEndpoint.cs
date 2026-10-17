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
    Core.Engine filterEngine
) : Hub
{
    /// <summary>The widest a viewer's map part may be on a gateway that follows viewers (F1.2 behavior 9).</summary>
    internal const double MaxViewerViewKm = 500;

    private bool AllowSettingsChanges => options.Value.AllowSettingsChanges;

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
    /// on needs a signed-in operator, which doesn't exist yet, so it's refused for everyone (F2.1 behavior 9).
    /// </summary>
    [HubMethodName(EngineHub.SetToggle)]
    public async Task SetToggle(Toggle toggle, bool enabled)
    {
        if (toggle == Toggle.Transmit && enabled)
        {
            RefuseTransmit();
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

    /// <summary>Sending needs a signed-in operator, which doesn't exist yet: refused for everyone (F2.1 behavior 9).</summary>
    [HubMethodName(EngineHub.SendPosition)]
    public Task<IReadOnlyList<PacketView>> SendPosition(string? sourceId)
    {
        RefuseTransmit();
        return engine.SendPositionAsync(sourceId, Context.ConnectionAborted);
    }

    /// <summary>Test send transmits too: refused for everyone until operator sign-in (F2.1 behavior 8).</summary>
    [HubMethodName(EngineHub.TestSend)]
    public Task<TestSendResult> TestSend(string sourceId)
    {
        RefuseTransmit();
        return engine.TestSendAsync(sourceId, Context.ConnectionAborted);
    }

    /// <summary>A listener ID changes nothing until it's saved, which has the settings gate.</summary>
    [HubMethodName(EngineHub.NewListenerId)]
    public Task<string> NewListenerId() => engine.NewListenerIdAsync(Context.ConnectionAborted);

    /// <summary>Transmitting from a browser waits for F2.9's operator sign-in, whatever <c>AllowSettingsChanges</c> says.</summary>
    private static void RefuseTransmit() =>
        throw new HubException(
            EngineHub.EncodeError(new EngineError(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage))
        );

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
        return AllowSettingsChanges
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

    /// <summary>Address lookup is offered only where settings can be changed, so the gateway isn't an open proxy.</summary>
    [HubMethodName(EngineHub.GetGeocodingInfo)]
    public async Task<GeocodingInfo> GetGeocodingInfo() =>
        AllowSettingsChanges
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
        if (!AllowSettingsChanges)
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
