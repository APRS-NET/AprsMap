// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>Engine settings, bound from the <c>Engine</c> configuration section.</summary>
public sealed class EngineOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Engine";

    /// <summary>Whether the engine connects to APRS-IS at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Runs nothing but the update checks (docs/features/F4.15-updates.md): the desktop app as a gateway's client
    /// (docs/features/F1.39-desktop-gateway-client.md, behavior 5), whose station is the gateway's but whose updates are
    /// its own. No source, beacon, history or listener starts.
    /// </summary>
    public bool UpdatesOnly { get; set; }

    /// <summary>
    /// Where settings are stored. Empty means the user's own settings folder (<see cref="SettingsLocation"/>); the
    /// gateway's container points it at a volume.
    /// </summary>
    public string? SettingsFolder { get; set; }

    /// <summary>
    /// Where message history and packet captures are stored. Empty means <see cref="SettingsFolder"/> when that's set,
    /// otherwise the user's data folder (<see cref="AppFolders.Data"/>).
    /// </summary>
    public string? DataFolder { get; set; }

    /// <summary>
    /// Where packet capture writes (docs/features/F1.27-packet-capture.md, behavior 4): <c>capture</c> in the data
    /// folder (<see cref="DataFolder"/>) when the engine is added to a host; null, as in tests that build the engine themselves, turns it off.
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

    /// <summary>
    /// Whether the APRS-IS switch keeps its last state when the engine starts (#415). False turns it off as the engine
    /// starts, before connecting, so APRS-IS is connected only by hand; the switch still turns it on while it runs.
    /// </summary>
    public bool AprsIsOnStart { get; set; } = true;

    /// <summary>First-run default: the server-side filter sent at login.</summary>
    public string? Filter { get; set; }

    /// <summary>
    /// First-run default: a KISS over TCP source (docs/features/F1.26-public-console.md, behavior 14), ticked for the first
    /// profile. With <see cref="Server"/> empty the first profile has no APRS-IS source: a TNC-only station.
    /// </summary>
    public KissSeedOptions Kiss { get; set; } = new();

    /// <summary>
    /// First-run default: a TNC on a serial port, such as a USB one (docs/features/F1.37-gateway-service.md, behavior
    /// 4), added to the first profile.
    /// </summary>
    public KissSerialSeedOptions KissSerial { get; set; } = new();

    /// <summary>First-run default: gpsd, for the station's position (F1.37 behavior 4), added to the first profile.</summary>
    public GpsdSeedOptions Gpsd { get; set; } = new();

    /// <summary>The update check (docs/features/F4.15-updates.md): whether it runs and what kind of install this is.</summary>
    public UpdateOptions Updates { get; set; } = new();

    /// <summary>The device list (docs/features/F4.16-device-list.md): where it's fetched from.</summary>
    public DeviceListOptions DeviceList { get; set; } = new();

    /// <summary>
    /// The packet history (docs/features/F1.41-persistent-history.md): where it's kept, and the length and size the
    /// gateway's configuration sets (<c>[engine.history]</c>), which win over the stored settings on every start.
    /// </summary>
    public HistoryOptions History { get; set; } = new();

    /// <summary>
    /// How many recent packets the engine keeps for clients that connect later, and for filtering after the fact
    /// (F1.8 behavior 9): a few minutes of a full APRS-IS feed, or hours of a filtered one.
    /// </summary>
    public int RecentPacketCapacity { get; set; } = 5_000;

    /// <summary>The most stations, objects and items kept; beyond it the least recently heard goes (F1.3 behavior 12).</summary>
    public int MaxStations { get; set; } = StationStore.DefaultMaxEntries;

    /// <summary>
    /// Where the dashboard's and the GPS's history is kept between runs (issue #386): <c>dashboard-history.json</c> in
    /// the data folder (<see cref="DataFolder"/>) when the engine is added to a host; null, as in tests that build the
    /// engine themselves, keeps it in memory only.
    /// </summary>
    public string? HistoryFile { get; set; }

    /// <summary>How often the history is saved while the engine runs; it's also saved as it stops.</summary>
    public TimeSpan HistorySaveInterval { get; set; } = TimeSpan.FromMinutes(5);

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

    /// <summary>
    /// How long a radio's GPS may be silent before it's gone, and before a radio set to On warns that none came
    /// (docs/features/F3.13-tnc-gps.md, behavior 3).
    /// </summary>
    public TimeSpan TncGpsTimeout { get; set; } = TimeSpan.FromSeconds(10);
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

/// <summary>A first-run TNC on a serial port (docs/features/F1.37-gateway-service.md, behavior 4).</summary>
public sealed class KissSerialSeedOptions
{
    /// <summary>The serial port, such as <c>/dev/serial/by-id/usb-…</c>; empty for none.</summary>
    public string? Port { get; set; }

    /// <summary>The speed; null for 9600.</summary>
    public int? Baud { get; set; }

    /// <summary>The TNC's radio port, 0–15; null for 0.</summary>
    public int? TncPort { get; set; }

    /// <summary>The source's name; null for the port.</summary>
    public string? Name { get; set; }
}

/// <summary>The packet history's folder and configured limits (docs/features/F1.41-persistent-history.md, behavior 2).</summary>
public sealed class HistoryOptions
{
    /// <summary>
    /// Where the history's day files are kept: <c>history</c> in the data folder when the engine is added to a host;
    /// null, as in tests that build the engine themselves, keeps no history.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>How many days are kept (0 is off), from the configuration; null leaves it to the stored settings.</summary>
    public int? Days { get; set; }

    /// <summary>The size limit in MB, from the configuration; null leaves it to the stored settings.</summary>
    public int? MaxMegabytes { get; set; }

    /// <summary>Whether the configuration sets either.</summary>
    internal bool Configured => Days is not null || MaxMegabytes is not null;

    /// <summary>
    /// <paramref name="stored"/> with what the configuration sets in place, brought into range: the file isn't checked
    /// as Settings is.
    /// </summary>
    internal HistorySettings Apply(HistorySettings stored) =>
        new(
            Math.Clamp(Days ?? stored.Days, 0, HistorySettings.MaxDays),
            Math.Clamp(
                MaxMegabytes ?? stored.MaxMegabytes,
                HistorySettings.MinMegabytes,
                HistorySettings.MaxMegabytesLimit
            )
        );
}

/// <summary>A first-run gpsd (docs/features/F1.37-gateway-service.md, behavior 4).</summary>
public sealed class GpsdSeedOptions
{
    /// <summary>gpsd's host, such as <c>localhost</c>; empty for none.</summary>
    public string? Host { get; set; }

    /// <summary>gpsd's port; null for 2947.</summary>
    public int? Port { get; set; }

    /// <summary>The source's name; null for the host.</summary>
    public string? Name { get; set; }
}

/// <summary>What kind of install the engine runs in, which decides what makes a release ready for it (F4.15 behavior 4).</summary>
public enum InstallKind
{
    /// <summary>A desktop archive: ready when its archive for this runtime is attached.</summary>
    Archive,

    /// <summary>A desktop install that updates itself: ready when its installer feed is attached.</summary>
    Installed,

    /// <summary>A gateway: ready when the release says its container image is pushed.</summary>
    Gateway,

    /// <summary>
    /// The Linux service gateway (docs/features/F1.40-gateway-service-updates.md): ready when its tarball for this
    /// architecture and the tarball's checksum are attached; updated by its updater, from here.
    /// </summary>
    Service,
}

/// <summary>The device list's download, where the engine starts: <c>Engine:DeviceList:…</c> (F4.16 behavior 10).</summary>
public sealed class DeviceListOptions
{
    /// <summary>Where the list is fetched from: the APRS Foundation's by default, a mirror's, or empty to never download.</summary>
    public string? Url { get; set; } = AprsNet.DeviceId.AprsDeviceListSource.DefaultUrl.ToString();

    /// <summary>
    /// Where the downloaded list is kept (<c>tocalls.json</c>, as published); the host sets it beside the data
    /// (<c>deviceid</c> in the data folder). Null keeps nothing: a list downloaded is used until the engine stops.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>How long a fetch may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the address may be changed in Settings › Data › Advanced (the desktop host sets it); otherwise only
    /// configuration sets it, as on a gateway, where a browser mustn't change where the server fetches from.
    /// </summary>
    public bool UrlIsSetting { get; set; }
}

/// <summary>The update check where the engine starts: <c>Engine:Updates:…</c>.</summary>
public sealed class UpdateOptions
{
    /// <summary>
    /// Whether the update check may run at all (behavior 6): false turns it off and hides Check now, for networks that
    /// block the internet or for store packages that update themselves (behavior 17).
    /// </summary>
    public bool Check { get; set; } = true;

    /// <summary>What kind of install this is; the host sets it.</summary>
    public InstallKind Kind { get; set; } = InstallKind.Archive;

    /// <summary>The runtime id (<c>linux-x64</c>…), for the archive or installer feed; the host sets it.</summary>
    public string? RuntimeId { get; set; }

    /// <summary>The GitHub API's address; another for tests.</summary>
    public Uri ApiBase { get; set; } = new("https://api.github.com/");
}
