// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>The SignalR wire contract between the gateway and remote clients. Both sides use these names.</summary>
public static class EngineHub
{
    /// <summary>The hub's path on the gateway.</summary>
    public const string Path = "/hubs/engine";

    /// <summary>Hub method for <see cref="IEngineService.GetStatusAsync"/>.</summary>
    public const string GetStatus = "GetStatus";

    /// <summary>Hub streaming method for <see cref="IEngineService.WatchPacketsAsync"/>.</summary>
    public const string WatchPackets = "WatchPackets";

    /// <summary>Hub streaming method for <see cref="IEngineService.WatchStationsAsync"/>.</summary>
    public const string WatchStations = "WatchStations";

    /// <summary>Hub method for <see cref="IEngineService.GetSettingsAsync"/>.</summary>
    public const string GetSettings = "GetSettings";

    /// <summary>Hub method for <see cref="IEngineService.SaveSettingsAsync"/>.</summary>
    public const string SaveSettings = "SaveSettings";

    /// <summary>The gateway's refusal when settings may not be changed remotely; the client maps it back.</summary>
    public const string SettingsReadOnlyMessage =
        "This gateway's settings can't be changed from the browser: operator sign-in isn't available yet.";
}
