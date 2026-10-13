// SPDX-License-Identifier: MIT
namespace AprsMap.Core;

/// <summary>Engine settings, bound from the <c>Engine</c> configuration section.</summary>
public sealed class EngineOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Engine";

    /// <summary>Whether the engine connects to APRS-IS at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The callsign used to log in. Without a passcode the login is receive-only.</summary>
    public string Callsign { get; set; } = "N0CALL";

    /// <summary>The APRS-IS passcode, or -1 for a receive-only login.</summary>
    public int Passcode { get; set; } = -1;

    /// <summary>APRS-IS server as <c>host</c> or <c>host:port</c>.</summary>
    public string Server { get; set; } = "rotate.aprs2.net:14580";

    /// <summary>The server-side filter sent at login.</summary>
    public string? Filter { get; set; }

    /// <summary>How many recent packets the engine keeps for clients that connect later.</summary>
    public int RecentPacketCapacity { get; set; } = 500;

    /// <summary>The first wait before reconnecting after a failure; doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between reconnect attempts.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);
}
