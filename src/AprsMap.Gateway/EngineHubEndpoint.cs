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
    public IAsyncEnumerable<PacketView> WatchPackets(CancellationToken cancellationToken) =>
        engine.WatchPacketsAsync(cancellationToken);

    [HubMethodName(EngineHub.WatchStations)]
    public IAsyncEnumerable<StationBatch> WatchStations(CancellationToken cancellationToken) =>
        engine.WatchStationsAsync(cancellationToken);

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
        if (!AllowSettingsChanges)
        {
            throw new HubException(EngineHub.SettingsReadOnlyMessage);
        }

        try
        {
            await engine.SaveSettingsAsync(settings, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // Only HubException messages reach the client; this one is meant for the user.
            throw new HubException(ex.Message);
        }
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
        RequireOperator();
        try
        {
            return await engine.GeocodeAsync(query, language, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    [HubMethodName(EngineHub.ComputePasscode)]
    public async Task<int> ComputePasscode(string callsign)
    {
        RequireOperator();
        try
        {
            return await engine.ComputePasscodeAsync(callsign, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    private void RequireOperator()
    {
        if (!AllowSettingsChanges)
        {
            throw new HubException(EngineHub.OperatorOnlyMessage);
        }
    }
}
