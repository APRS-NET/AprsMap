// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AprsMap.Gateway;

/// <summary>Exposes the in-process <see cref="IEngineService"/> to remote UIs. Method names come from <see cref="EngineHub"/>.</summary>
internal sealed class EngineHubEndpoint(IEngineService engine, IOptions<GatewayOptions> options) : Hub
{
    private bool AllowSettingsChanges => options.Value.AllowSettingsChanges;

    [HubMethodName(EngineHub.GetStatus)]
    public Task<EngineStatus> GetStatus() => engine.GetStatusAsync(Context.ConnectionAborted);

    [HubMethodName(EngineHub.WatchPackets)]
    public IAsyncEnumerable<PacketView> WatchPackets(PacketFilter? filter, CancellationToken cancellationToken) =>
        engine.WatchPacketsAsync(filter, cancellationToken);

    [HubMethodName(EngineHub.WatchStations)]
    public IAsyncEnumerable<StationBatch> WatchStations(CancellationToken cancellationToken) =>
        engine.WatchStationsAsync(cancellationToken);

    [HubMethodName(EngineHub.GetStation)]
    public Task<StationDetail?> GetStation(string name) => engine.GetStationAsync(name, Context.ConnectionAborted);

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

    /// <summary>Reconnecting affects every viewer, so it has the same gate as changing settings.</summary>
    [HubMethodName(EngineHub.Reconnect)]
    public Task Reconnect()
    {
        Require(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage);
        return engine.ReconnectAsync(Context.ConnectionAborted);
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
