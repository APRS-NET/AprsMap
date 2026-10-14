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

    /// <summary>The user's settings. A gateway may return them read-only, with passcodes hidden.</summary>
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and stores <paramref name="settings"/>. Changing the active profile, or its login or filter,
    /// disconnects from APRS-IS and reconnects with the new values.
    /// </summary>
    /// <exception cref="ArgumentException">The settings are not valid; the message says why.</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not change settings (a gateway without operator sign-in).</exception>
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

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
}
