// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// Everything a UI can ask of the engine. The desktop app calls it in-process; the browser app calls the same
/// interface over SignalR through the gateway, so both UIs stay identical (docs/architecture.md).
/// </summary>
public interface IEngineService
{
    /// <summary>A snapshot of the engine's state.</summary>
    Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams packets as the engine receives them, starting with the most recent ones it still holds. For the raw
    /// packet view; the map uses <see cref="WatchStationsAsync"/>, which is far smaller.
    /// </summary>
    IAsyncEnumerable<PacketView> WatchPacketsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the map's stations. The first batches hold every station the engine knows (a late joiner catches up
    /// without a separate snapshot call); after that, each batch holds what changed since the previous one, at most
    /// every few hundred milliseconds, with only the latest state per station.
    /// </summary>
    IAsyncEnumerable<StationBatch> WatchStationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything known about one station, object or item (F1.3 behavior 14), for the details pane and the station list;
    /// null when the store has no entry by that name.
    /// </summary>
    Task<StationDetail?> GetStationAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The user's settings. A gateway may return them read-only, with passcodes hidden.</summary>
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and stores <paramref name="settings"/>. Changing the active profile, or its login or server,
    /// disconnects from APRS-IS and reconnects with the new values; a change to only its filter (or, without a filter of
    /// its own, its location) is sent on the live connection instead.
    /// </summary>
    /// <exception cref="ArgumentException">The settings are not valid; the message says why.</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not change settings (a gateway without operator sign-in).</exception>
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the APRS-IS connection and starts a new one at once, with the backoff reset. Also the way to try again
    /// after a refused login.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task ReconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts over (docs/features/F0.10-start-over.md): deletes every stored profile and setting, disconnects, and
    /// forgets the packets and stations received, so the next step is first-run setup. Defaults from configuration are
    /// not applied again until the engine restarts.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not change settings (a gateway without operator sign-in).</exception>
    Task ResetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether <see cref="GeocodeAsync"/> is available to this caller, and which server it sends queries to.</summary>
    Task<GeocodingInfo> GetGeocodingInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up an address or postal code (docs/features/F0.5-first-run-wizard.md, behaviors 13–22). Results are
    /// cached in memory, so repeating a query sends nothing; requests are spaced at least a second apart, and a lookup
    /// that has to wait is replaced by a newer one. A blank query returns no results without sending anything.
    /// </summary>
    /// <param name="query">The text the user typed. It is not stored or logged above Debug level.</param>
    /// <param name="language">The UI's language (an IETF tag such as <c>en-GB</c>) for the place names, or null.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>Up to five matches, best first; empty when nothing matched.</returns>
    /// <exception cref="InvalidOperationException">The lookup failed or is unavailable; the message is meant for the user.</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not use lookup (a gateway without operator sign-in).</exception>
    Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(
        string query,
        string? language,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Computes the APRS-IS passcode for <paramref name="callsign"/>'s base callsign (the SSID doesn't change it), for
    /// the passcode calculator. Only for a licensed amateur's own callsign; the UI asks the user to confirm that first.
    /// </summary>
    /// <exception cref="ArgumentException">The callsign can't have a passcode; the message says why.</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not use it (a gateway without operator sign-in).</exception>
    Task<int> ComputePasscodeAsync(string callsign, CancellationToken cancellationToken = default);

    /// <summary>The APRS-IS servers to offer by region, worldwide first (from APRS.NET).</summary>
    Task<IReadOnlyList<ServerPreset>> GetServerPresetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a Maidenhead locator of 4, 6, 8 or 10 characters, in any case, and returns the centre of its square.
    /// </summary>
    /// <exception cref="ArgumentException">It isn't a locator; the error's code says why and where.</exception>
    Task<LocatorInfo> ParseLocatorAsync(string locator, CancellationToken cancellationToken = default);

    /// <summary>The 6-character Maidenhead locator of a position, for showing next to coordinates.</summary>
    /// <exception cref="ArgumentException">The position is off the globe.</exception>
    Task<string> GetLocatorAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}
