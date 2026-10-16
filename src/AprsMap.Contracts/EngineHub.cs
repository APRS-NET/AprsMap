// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace AprsMap.Contracts;

/// <summary>The SignalR wire contract between the gateway and remote clients. Both sides use these names.</summary>
public static class EngineHub
{
    /// <summary>The hub's path on the gateway.</summary>
    public const string Path = "/hubs/engine";

    /// <summary>
    /// The contract's version: raised whenever a hub member or record changes in a way an older app can't follow. The
    /// app sends it with its connection (<see cref="ContractQuery"/>), and a gateway on another version refuses it with
    /// <see cref="EngineErrorCodes.GatewayOutdated"/> (F1.14 behavior 8).
    /// </summary>
    public const int ContractVersion = 1;

    /// <summary>The query value the app's contract version travels in, on the hub's address.</summary>
    public const string ContractQuery = "contract";

    /// <summary>English text of <see cref="EngineErrorCodes.GatewayOutdated"/>.</summary>
    public const string OutdatedMessage = "A new version of the app is available. Reload the page to use it.";

    /// <summary>Hub method for <see cref="IEngineService.GetStatusAsync"/>.</summary>
    public const string GetStatus = "GetStatus";

    /// <summary>Hub streaming method for <see cref="IEngineService.WatchPacketsAsync"/>.</summary>
    public const string WatchPackets = "WatchPackets";

    /// <summary>Hub streaming method for <see cref="IEngineService.WatchStationsAsync"/>.</summary>
    public const string WatchStations = "WatchStations";

    /// <summary>Hub method for <see cref="IEngineService.GetStationAsync"/>.</summary>
    public const string GetStation = "GetStation";

    /// <summary>Hub method for <see cref="IEngineService.SetMapViewAsync"/>.</summary>
    public const string SetMapView = "SetMapView";

    /// <summary>Hub method for <see cref="IEngineService.QueryStationsAsync"/>.</summary>
    public const string QueryStations = "QueryStations";

    /// <summary>Hub method for <see cref="IEngineService.GetSettingsAsync"/>.</summary>
    public const string GetSettings = "GetSettings";

    /// <summary>Hub method for <see cref="IEngineService.SaveSettingsAsync"/>.</summary>
    public const string SaveSettings = "SaveSettings";

    /// <summary>Hub method for <see cref="IEngineService.GetViewFacetsAsync"/>.</summary>
    public const string GetViewFacets = "GetViewFacets";

    /// <summary>Hub method for <see cref="IEngineService.GetClearCountsAsync"/>.</summary>
    public const string GetClearCounts = "GetClearCounts";

    /// <summary>Hub method for <see cref="IEngineService.ClearAsync"/>.</summary>
    public const string Clear = "Clear";

    /// <summary>Hub method for <see cref="IEngineService.GetRouteAsync"/>.</summary>
    public const string GetRoute = "GetRoute";

    /// <summary>Hub method for <see cref="IEngineService.ClearTrackAsync"/>.</summary>
    public const string ClearTrack = "ClearTrack";

    /// <summary>Hub method for <see cref="IEngineService.ReconnectAsync"/>.</summary>
    public const string Reconnect = "Reconnect";

    /// <summary>Hub method for <see cref="IEngineService.ResetSettingsAsync"/>.</summary>
    public const string ResetSettings = "ResetSettings";

    /// <summary>Hub method for <see cref="IEngineService.GetGeocodingInfoAsync"/>.</summary>
    public const string GetGeocodingInfo = "GetGeocodingInfo";

    /// <summary>Hub method for <see cref="IEngineService.GeocodeAsync"/>.</summary>
    public const string Geocode = "Geocode";

    /// <summary>Hub method for <see cref="IEngineService.ComputePasscodeAsync"/>.</summary>
    public const string ComputePasscode = "ComputePasscode";

    /// <summary>Hub method for <see cref="IEngineService.GetServerPresetsAsync"/>.</summary>
    public const string GetServerPresets = "GetServerPresets";

    /// <summary>Hub method for <see cref="IEngineService.ParseLocatorAsync"/>.</summary>
    public const string ParseLocator = "ParseLocator";

    /// <summary>Hub method for <see cref="IEngineService.GetLocatorAsync"/>.</summary>
    public const string GetLocator = "GetLocator";

    /// <summary>English text of <see cref="EngineErrorCodes.OperatorOnly"/>: address lookup, the passcode calculator and reconnecting.</summary>
    public const string OperatorOnlyMessage =
        "Only this gateway's operator can do that, and operator sign-in isn't available yet.";

    /// <summary>English text of <see cref="EngineErrorCodes.GatewayBusy"/>.</summary>
    public const string BusyMessage = "This gateway has as many viewers as it allows. Try again in a little while.";

    /// <summary>English text of <see cref="EngineErrorCodes.GatewayRateLimited"/>.</summary>
    public const string RateLimitedMessage = "Too many requests to the gateway at once; slowing down.";

    /// <summary>English text of <see cref="EngineErrorCodes.SettingsReadOnly"/>.</summary>
    public const string SettingsReadOnlyMessage =
        "This gateway's settings can't be changed from the browser: operator sign-in isn't available yet.";

    /// <summary>Marks an <see cref="EngineError"/> sent as JSON in a hub error message.</summary>
    public const string ErrorMarker = "engine-error:";

    /// <summary>The hub error message that carries <paramref name="error"/>.</summary>
    public static string EncodeError(EngineError error) =>
        ErrorMarker + JsonSerializer.Serialize(error, EngineJsonContext.Default.EngineError);

    /// <summary>
    /// Reads the <see cref="EngineError"/> from a hub error message. SignalR puts its own text in front ("An
    /// unexpected error occurred invoking … HubException: "), so the marker is searched for, not expected first.
    /// </summary>
    public static bool TryDecodeError(
        string? message,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EngineError? error
    )
    {
        error = null;
        var at = message?.IndexOf(ErrorMarker, StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return false;
        }
        try
        {
            error = JsonSerializer.Deserialize(
                message![(at + ErrorMarker.Length)..],
                EngineJsonContext.Default.EngineError
            );
        }
        catch (JsonException)
        {
            return false;
        }
        return error is { Code: not null, Message: not null };
    }
}
