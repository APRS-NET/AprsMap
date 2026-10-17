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
/// <param name="Host">For KISS over TCP: the TNC's host name or address (docs/features/F3.1-kiss-sources.md).</param>
/// <param name="Port">For KISS over TCP: the TNC's port; null for <see cref="DefaultKissPort"/>.</param>
/// <param name="TncPort">For KISS: the TNC's radio port, 0 to 15, for multi-port TNCs; null for 0.</param>
/// <param name="Transmit">
/// TX enabled (docs/features/F2.1-own-station.md, behavior 5): the station may send on this source. Off unless the user
/// ticks it.
/// </param>
/// <param name="TxPath">
/// For a TNC: the path packets are sent with, such as <c>WIDE1-1,WIDE2-1</c>; null for <see cref="DefaultTxPath"/>.
/// </param>
public sealed record SourceSettings(
    string Id,
    string Name,
    SourceKind Kind,
    string? Server = null,
    string? Filter = null,
    string? Host = null,
    int? Port = null,
    int? TncPort = null,
    bool Transmit = false,
    string? TxPath = null
)
{
    /// <summary>The path a TNC sends with unless it has its own: <c>WIDE1-1,WIDE2-1</c> (F2.1 behavior 5.2).</summary>
    public const string DefaultTxPath = "WIDE1-1,WIDE2-1";

    /// <summary>The TX path in effect for a TNC: <see cref="TxPath"/>, or <see cref="DefaultTxPath"/>.</summary>
    public string EffectiveTxPath => string.IsNullOrWhiteSpace(TxPath) ? DefaultTxPath : TxPath.Trim();

    /// <summary>Direwolf's and soundmodem's KISS over TCP port (F3.1 decision 4).</summary>
    public const int DefaultKissPort = 8001;

    /// <summary>A new KISS over TCP source for <paramref name="host"/>, named after it.</summary>
    public static SourceSettings KissTcpFor(string host, int? port = null, int? tncPort = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            host.Trim(),
            SourceKind.KissTcp,
            Host: host.Trim(),
            Port: port,
            TncPort: tncPort
        );

    /// <summary>"10.10.20.144:8001": where a KISS over TCP source connects; null for other kinds.</summary>
    public string? KissEndpoint =>
        Kind == SourceKind.KissTcp && Host is { } host
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{host}:{Port ?? DefaultKissPort}")
            : null;

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
