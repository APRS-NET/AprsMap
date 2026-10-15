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
    /// First-run defaults: when no settings are stored yet and <see cref="Callsign"/> is set, the engine creates its
    /// first profile from these values. After that the stored settings win, and these are ignored.
    /// </summary>
    public string Callsign { get; set; } = string.Empty;

    /// <summary>First-run default: the APRS-IS passcode, or -1 for a receive-only login.</summary>
    public int Passcode { get; set; } = -1;

    /// <summary>First-run default: APRS-IS server as <c>host</c> or <c>host:port</c>.</summary>
    public string Server { get; set; } = "rotate.aprs2.net:14580";

    /// <summary>First-run default: the server-side filter sent at login.</summary>
    public string? Filter { get; set; }

    /// <summary>How many recent packets the engine keeps for clients that connect later.</summary>
    public int RecentPacketCapacity { get; set; } = 500;

    /// <summary>The most stations, objects and items kept; beyond it the least recently heard goes (F1.3 behavior 12).</summary>
    public int MaxStations { get; set; } = StationStore.DefaultMaxEntries;

    /// <summary>How often stale and expired stations are looked for (F1.3 behavior 10).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the map's station stream sends what changed. Longer means fewer, larger messages.</summary>
    public TimeSpan StationBatchInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The first wait before reconnecting after a failure; doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between reconnect attempts.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);
}
