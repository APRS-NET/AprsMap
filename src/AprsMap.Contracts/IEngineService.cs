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
    /// packet view; the map uses <see cref="WatchStationsAsync"/>, which is far smaller. Only packets that pass
    /// <paramref name="filter"/> are sent, held ones included, so a browser doesn't receive what it hides (F1.8
    /// behavior 7); to change the filter, start a new stream.
    /// </summary>
    IAsyncEnumerable<PacketView> WatchPacketsAsync(
        PacketFilter? filter = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Streams the map's stations. The first batches hold every station the engine knows (a late joiner catches up
    /// without a separate snapshot call); after that, each batch holds what changed since the previous one, at most
    /// every few hundred milliseconds, with only the latest state per station. Only stations that pass
    /// <paramref name="filter"/> are sent (F1.9): one that stops matching is removed, and one that starts matching
    /// arrives with its track. To change the filter, start a new stream.
    /// </summary>
    IAsyncEnumerable<StationBatch> WatchStationsAsync(
        StationFilter? filter = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>The values heard so far with their counts, for the View filter's lists (F1.9 behavior 2).</summary>
    Task<ViewFacets> GetViewFacetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything known about one station, object or item (F1.3 behavior 14), for the details pane and the station list;
    /// null when the store has no entry by that name.
    /// </summary>
    Task<StationDetail?> GetStationAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a quick toggle on or off (docs/features/F1.15-quick-toggles.md) and saves it with the settings: APRS-IS
    /// disconnects or connects at once, and tile downloads and address lookups stop or start. Turning
    /// <see cref="Toggle.Transmit"/> on is checked first (docs/features/F2.1-own-station.md, behavior 5).
    /// </summary>
    /// <exception cref="InvalidOperationException">Transmit can't be turned on; the error's <c>transmit.*</c> code says why.</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task SetToggleAsync(Toggle toggle, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the active profile's position once, by hand (docs/features/F2.1-own-station.md, behaviors 10–13): its
    /// location, symbol and comment, on every source that can send now, or on <paramref name="sourceId"/> alone. Each
    /// copy is listed marked <see cref="PacketMarkers.Sent"/>, and the profile is marked
    /// <see cref="StationProfile.PositionConfirmed"/>.
    /// </summary>
    /// <param name="sourceId">The one source to send on, or null for every source that can.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The copies sent, one per source.</returns>
    /// <exception cref="InvalidOperationException">Nothing could be sent; the error's <c>transmit.*</c> code says why.</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not transmit (a gateway without operator sign-in).</exception>
    Task<IReadOnlyList<PacketView>> SendPositionAsync(
        string? sourceId = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends one status report on <paramref name="sourceId"/> alone, then listens for 30 seconds for it coming back
    /// through a digipeater or an IGate (docs/features/F2.1-own-station.md, behavior 9).
    /// </summary>
    /// <exception cref="InvalidOperationException">It can't be sent; the error's <c>transmit.*</c> code says why.</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not transmit (a gateway without operator sign-in).</exception>
    Task<TestSendResult> TestSendAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>The active profile's conversations, newest activity first (docs/features/F2.4-messaging.md, behavior 1).</summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not read messages (a gateway without operator sign-in).</exception>
    Task<IReadOnlyList<Conversation>> GetConversationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the active profile's messages: first the ones kept, then each one added or changed (its state, its tries,
    /// read), keyed by <see cref="ChatMessage.Id"/> (behaviors 1, 7). A profile switch starts a new stream.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not read messages (a gateway without operator sign-in).</exception>
    IAsyncEnumerable<ChatMessage> WatchMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends <paramref name="text"/> to <paramref name="correspondent"/>, split into messages of at most 67 characters (behavior 6);
    /// they wait, queued, while nothing can send (behavior 10).
    /// </summary>
    /// <returns>The messages it made.</returns>
    /// <exception cref="ArgumentException">The callsign or text can't be sent; the error's <c>messages.*</c> code says why.</exception>
    /// <exception cref="InvalidOperationException">This profile may not transmit; the <c>transmit.*</c> code says why.</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not send (a gateway without operator sign-in).</exception>
    Task<IReadOnlyList<ChatMessage>> SendMessageAsync(
        string correspondent,
        string text,
        CancellationToken cancellationToken = default
    );

    /// <summary>Stops sending a queued or retrying message (behavior 7). Does nothing to one that has finished.</summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not send (a gateway without operator sign-in).</exception>
    Task CancelMessageAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Sends a failed, rejected or cancelled message again, as a new message (behavior 7).</summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not send (a gateway without operator sign-in).</exception>
    Task ResendMessageAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Marks every message from <paramref name="correspondent"/> read (behavior 4).</summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not read messages (a gateway without operator sign-in).</exception>
    Task MarkReadAsync(string correspondent, CancellationToken cancellationToken = default);

    /// <summary>Sets which way a conversation's messages go and how they're retried (behaviors 8, 9).</summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not send (a gateway without operator sign-in).</exception>
    Task SetConversationAsync(
        string correspondent,
        SendVia sendVia,
        MessageRetries retries,
        CancellationToken cancellationToken = default
    );

    /// <summary>A new listener ID for an unlicensed user's profile (docs/features/F2.1-own-station.md, behavior 4).</summary>
    Task<string> NewListenerIdAsync(CancellationToken cancellationToken = default);

    /// <summary>How much each kind of Clear would clear now (docs/features/F1.19-housekeeping.md, behavior 1).</summary>
    Task<ClearCounts> GetClearCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears what <paramref name="scope"/> names (docs/features/F1.19-housekeeping.md) and returns how many it cleared;
    /// the removals stream to every viewer. Settings, profiles and preferences are never touched.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task<int> ClearAsync(ClearScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The RF network over the last hour (docs/features/F1.21-rf-paths.md, behavior 4): every link between two stations
    /// whose positions are known, with how many packets crossed it.
    /// </summary>
    Task<IReadOnlyList<NetworkLink>> GetNetworkLinksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The dashboard's numbers over <paramref name="range"/> (docs/features/F1.22-statistics.md, behaviors 3, 4): the
    /// whole feed, from counters the engine keeps for 24 hours, the same for every viewer; or with
    /// <paramref name="filter"/>, My view (behavior 7): only the stations that view filter shows.
    /// </summary>
    Task<Dashboard> GetDashboardAsync(
        DashboardRange range,
        StationFilter? filter = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>A station's held telemetry reports (F1.11); null when it has sent none.</summary>
    Task<TelemetryView?> GetTelemetryAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// How a packet travelled (docs/features/F1.21-rf-paths.md, behavior 2): the station's last packet, a track point's,
    /// or one from the packet list, with the position of each digipeater and IGate the engine knows. Null when the
    /// engine has no such station or track point.
    /// </summary>
    Task<PacketRoute?> GetRouteAsync(RouteQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// How this station's last position got out (docs/features/F3.3-rf-paths.md, behavior 3): one route per copy heard
    /// back, from where it was sent through the digipeaters that repeated it to the IGate that gated it. Empty with no
    /// position sent, or none heard back over RF.
    /// </summary>
    Task<IReadOnlyList<PacketRoute>> GetRoutesOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties a station's track except its latest point (docs/features/F1.5-tracks.md, behavior 10); the change streams
    /// to every viewer. Nothing happens when there's no such track.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task ClearTrackAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports what this app's map shows, for the APRS-IS filter (F1.2): call it when the map stops moving or starts or
    /// stops following a station. On a gateway the map part counts only with <c>Gateway:FollowViewers</c>.
    /// </summary>
    Task SetMapViewAsync(MapViewReport view, CancellationToken cancellationToken = default);

    /// <summary>One page of the station list, searched, filtered and sorted by the engine (F1.7).</summary>
    Task<StationPage> QueryStationsAsync(StationQuery query, CancellationToken cancellationToken = default);

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
    /// Switches a source other than APRS-IS on or off, for the station (docs/features/F3.1-kiss-sources.md, behavior
    /// 6). The choice is saved with the toggles; an unknown id does nothing.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task SetSourceEnabledAsync(string sourceId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a source's connection, or its wait to reconnect, and connects again now (docs/features/F3.1-kiss-sources.md,
    /// behavior 5). Does nothing for a source that isn't open.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task ReconnectSourceAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The serial ports present on the engine's machine, for a KISS over serial source (docs/features/F3.1-kiss-sources.md,
    /// behavior 1.2); on a gateway, the gateway's.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task<IReadOnlyList<string>> GetSerialPortsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The TNC types a KISS over serial source can be switched into KISS with, such as the Kenwood TH-D72
    /// (docs/features/F3.2-built-in-tncs.md, behavior 4).
    /// </summary>
    Task<IReadOnlyList<TncTypeInfo>> GetTncTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>The packets capture recorded, newest first (docs/features/F1.27-packet-capture.md, behavior 7).</summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task<CaptureView> GetCaptureAsync(CaptureQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captured packets as one file for APRS.NET's test corpus, anonymized or as received, with a note (F1.27 behavior 8).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task<CaptureExportResult> ExportCaptureAsync(CaptureExport request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the captured packets (F1.27 behavior 10).</summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task ClearCaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A source's traffic for the Source console (docs/features/F1.28-source-console.md, behaviors 2, 7): the entries
    /// kept, then new ones as they come. A TNC's are kept from when it opens; APRS-IS's only while someone watches.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    IAsyncEnumerable<TrafficEntry> WatchSourceTrafficAsync(
        string sourceId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Puts a serial KISS source in manual mode, or ends it (docs/features/F3.2-built-in-tncs.md, behavior 9): while on,
    /// the automatic switching into KISS holds off. It ends by itself after 10 minutes with nothing sent.
    /// </summary>
    /// <exception cref="ArgumentException">The source isn't a serial KISS source that's open (<c>console.source-unknown</c>).</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task SetTncManualAsync(string sourceId, bool manual, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends <paramref name="command"/> to a serial KISS source's TNC over its open connection (F3.2 behaviors 10, 11).
    /// The scripts need nothing more; a typed line or bytes need manual mode and a licensed profile that may transmit.
    /// </summary>
    /// <exception cref="ArgumentException">Not a serial KISS source that's open, or bytes that aren't hex.</exception>
    /// <exception cref="InvalidOperationException">Not in manual mode, or the profile may not transmit.</exception>
    /// <exception cref="UnauthorizedAccessException">This caller may not do it (a gateway without operator sign-in).</exception>
    Task SendTncCommandAsync(string sourceId, TncCommand command, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// The bulletins, announcements and NWS bulletins heard (docs/features/F2.5-bulletins.md, behaviors 1–3): those kept
    /// now, then each new one, new version or removal as it happens.
    /// </summary>
    IAsyncEnumerable<BulletinView> WatchBulletinsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a bulletin or announcement (docs/features/F2.5-bulletins.md, behaviors 5–7): sent now, then repeated on
    /// APRS.NET's bulletin schedule until it finishes or is stopped. Posting again to the same addressee replaces the
    /// text and starts the schedule again. It shows in <see cref="WatchBulletinsAsync"/> as ours, with its next send.
    /// </summary>
    /// <exception cref="ArgumentException">The id, group or text can't be sent.</exception>
    /// <exception cref="InvalidOperationException">Transmitting isn't allowed now (F2.1's <c>transmit.*</c> reasons).</exception>
    Task<BulletinView> PostBulletinAsync(BulletinPost post, CancellationToken cancellationToken = default);

    /// <summary>Stops repeating one of our bulletins, by addressee (behavior 6); nothing happens for one not ours.</summary>
    Task StopBulletinAsync(string addressee, CancellationToken cancellationToken = default);

    /// <summary>The ANSRVR groups the active profile has joined (behavior 10).</summary>
    Task<IReadOnlyList<string>> GetGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Joins an ANSRVR group (behavior 8): sends ANSRVR's join message through messaging and keeps the group alive
    /// every 8 hours from then on.
    /// </summary>
    /// <exception cref="ArgumentException">The group name can't be sent.</exception>
    Task JoinGroupAsync(string group, CancellationToken cancellationToken = default);

    /// <summary>Leaves an ANSRVR group (behavior 9): sends ANSRVR's leave message and stops keeping it alive.</summary>
    Task LeaveGroupAsync(string group, CancellationToken cancellationToken = default);

    /// <summary>Posts to an ANSRVR group (behavior 9): ANSRVR's post message, which counts as keeping it alive.</summary>
    /// <exception cref="ArgumentException">The group name or text can't be sent.</exception>
    Task<IReadOnlyList<ChatMessage>> PostToGroupAsync(
        string group,
        string text,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// The distance and bearing from the active profile's location to a destination, and the great-circle line to it
    /// (docs/features/F1.18-destination.md, behaviors 2, 3); <see cref="DestinationInfo.Unknown"/> without a location.
    /// </summary>
    Task<DestinationInfo> GetDestinationAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default
    );

    /// <summary>The 6-character Maidenhead locator of a position, for showing next to coordinates.</summary>
    /// <exception cref="ArgumentException">The position is off the globe.</exception>
    Task<string> GetLocatorAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}
