// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// One place APRS traffic comes from (docs/features/F1.25-sources.md, behavior 1). Profiles pick sources from the
/// master list; what identifies the station (callsign, passcode, location) stays on the profile.
/// </summary>
/// <param name="Id">Stable identifier; never shown.</param>
/// <param name="Name">What the user calls it, such as the server's name.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Server">For APRS-IS: the server as <c>host</c> or <c>host:port</c>.</param>
/// <param name="Filter">For APRS-IS: filter text added to the automatic filter (F1.2), or null.</param>
public sealed record SourceSettings(
    string Id,
    string Name,
    SourceKind Kind,
    string? Server = null,
    string? Filter = null
)
{
    /// <summary>A new APRS-IS source for <paramref name="server"/>, named after it.</summary>
    public static SourceSettings AprsIsFor(string server, string? filter = null) =>
        new(Guid.NewGuid().ToString("N"), NameFor(server), SourceKind.AprsIs, server, filter);

    /// <summary>A source's default name: the server's host, without the port.</summary>
    public static string NameFor(string server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var host = server.Trim();
        var colon = host.LastIndexOf(':');
        return colon > 0 && !host.EndsWith(']') ? host[..colon] : host;
    }
}

/// <summary>The kinds of source (F1.25 behavior 1).</summary>
public enum SourceKind
{
    /// <summary>An APRS-IS server: at most one in use at a time.</summary>
    AprsIs,

    /// <summary>A KISS TNC over TCP, such as Direwolf (0.4.0).</summary>
    KissTcp,

    /// <summary>A KISS TNC on a serial port (0.4.0).</summary>
    KissSerial,

    /// <summary>A GPS, over serial, TCP or gpsd (with beaconing).</summary>
    Gps,
}
