// SPDX-License-Identifier: MIT
namespace AprsMap.Core;

/// <summary>Engine settings, bound from the <c>Engine</c> configuration section.</summary>
public sealed class EngineOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Engine";

    /// <summary>Whether the engine connects to APRS-IS at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where settings are stored. Empty means the user's own settings folder (<see cref="SettingsLocation"/>); the
    /// gateway's container points it at a volume.
    /// </summary>
    public string? SettingsFolder { get; set; }

    /// <summary>
    /// Where packet capture writes (docs/features/F1.27-packet-capture.md, behavior 4): <c>capture</c> in the settings
    /// folder when the engine is added to a host; null, as in tests that build the engine themselves, turns it off.
    /// </summary>
    public string? CaptureFolder { get; set; }

    /// <summary>
    /// First-run defaults: when no settings are stored yet and <see cref="Callsign"/> is set, the engine creates its
    /// first profile from these values. After that the stored settings win, and these are ignored.
    /// </summary>
    public string Callsign { get; set; } = string.Empty;

    /// <summary>First-run default: the APRS-IS passcode, or -1 for a receive-only login.</summary>
    public int Passcode { get; set; } = -1;

    /// <summary>First-run default: APRS-IS server as <c>host</c> or <c>host:port</c>.</summary>
    public string Server { get; set; } = "rotate.aprs2.net:14580";

    /// <summary>
    /// Receive only, whatever the settings (docs/features/F1.26-public-console.md behavior 7): turning Transmit on and
    /// every send are refused with <c>transmit.receive-only</c> (docs/features/F2.1-own-station.md, behavior 5.1). Set
    /// only where the engine starts.
    /// </summary>
    public bool ReceiveOnly { get; set; }

    /// <summary>First-run default: the server-side filter sent at login.</summary>
    public string? Filter { get; set; }

    /// <summary>
    /// First-run default: a KISS over TCP source (docs/features/F1.26-public-console.md, behavior 14), ticked for the first
    /// profile. With <see cref="Server"/> empty the first profile has no APRS-IS source: a TNC-only station.
    /// </summary>
    public KissSeedOptions Kiss { get; set; } = new();

    /// <summary>
    /// How many recent packets the engine keeps for clients that connect later, and for filtering after the fact
    /// (F1.8 behavior 9): a few minutes of a full APRS-IS feed, or hours of a filtered one.
    /// </summary>
    public int RecentPacketCapacity { get; set; } = 5_000;

    /// <summary>The most stations, objects and items kept; beyond it the least recently heard goes (F1.3 behavior 12).</summary>
    public int MaxStations { get; set; } = StationStore.DefaultMaxEntries;

    /// <summary>How often stale and expired stations are looked for (F1.3 behavior 10).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the map's station stream sends what changed. Longer means fewer, larger messages.</summary>
    public TimeSpan StationBatchInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The shortest time between two filter changes sent to APRS-IS (F1.2 behavior 5); later ones are merged.</summary>
    public TimeSpan FilterInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The first wait before reconnecting after a failure; doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between reconnect attempts.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>A first-run KISS over TCP source: <c>Engine__Kiss__Host</c> and the rest.</summary>
public sealed class KissSeedOptions
{
    /// <summary>The TNC's host; empty for none.</summary>
    public string? Host { get; set; }

    /// <summary>The TNC's port; null for 8001.</summary>
    public int? Port { get; set; }

    /// <summary>The TNC's radio port, 0–15; null for 0.</summary>
    public int? TncPort { get; set; }

    /// <summary>The source's name; null for the host and port.</summary>
    public string? Name { get; set; }
}
