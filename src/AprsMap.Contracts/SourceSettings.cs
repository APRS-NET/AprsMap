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
/// <param name="RxOnly">
/// RX only (docs/features/F2.1-own-station.md, behavior 5): the station never sends on this source. Off unless the user
/// ticks it; a kind that can't send (<see cref="CanTransmit"/>) is RX only whatever it says.
/// </param>
/// <param name="TxPath">
/// For a TNC: the path packets are sent with, such as <c>WIDE1-1,WIDE2-1</c>; null for <see cref="DefaultTxPath"/>.
/// </param>
/// <param name="SerialPort">
/// For KISS over serial: the port's name, such as <c>/dev/ttyUSB0</c> or <c>COM3</c> (docs/features/F3.1-kiss-sources.md,
/// behavior 1.2).
/// </param>
/// <param name="Baud">For KISS over serial: the speed; null for <see cref="DefaultBaud"/>.</param>
/// <param name="DtrEnable">
/// For KISS over serial: whether DTR is raised when the port opens; null for raised. Some TNCs need it low.
/// </param>
/// <param name="RtsEnable">For KISS over serial: whether RTS is raised when the port opens; null for raised.</param>
/// <param name="TncType">
/// For KISS over serial: how the TNC is switched into KISS when the port opens (docs/features/F3.2-built-in-tncs.md,
/// behavior 4): null for a TNC in KISS already, a known type's id such as <c>th-d72</c>, or <see cref="CustomTncType"/>.
/// </param>
/// <param name="EnterScript">For a <see cref="CustomTncType"/>: the script that switches the TNC into KISS.</param>
/// <param name="LeaveScript">For a <see cref="CustomTncType"/>: the script that switches it out again, or null.</param>
/// <param name="GpsLink">
/// For a GPS (docs/features/F3.6-gps.md, behavior 1): how it's reached. Serial uses <paramref name="SerialPort"/> and
/// <paramref name="Baud"/>; TCP and gpsd use <paramref name="Host"/> and <paramref name="Port"/>.
/// </param>
/// <param name="TncGps">
/// For TNC text output (<see cref="SourceKind.AprsTextSerial"/>): whether the radio's GPS sentences place the station
/// (docs/features/F3.13-tnc-gps.md); null for <see cref="Contracts.TncGps.Auto"/>.
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
    bool RxOnly = false,
    string? TxPath = null,
    string? SerialPort = null,
    int? Baud = null,
    bool? DtrEnable = null,
    bool? RtsEnable = null,
    string? TncType = null,
    string? EnterScript = null,
    string? LeaveScript = null,
    GpsLink? GpsLink = null,
    TncGps? TncGps = null
)
{
    /// <summary>The radio's GPS choice in force: only TNC text output has one (F3.13 behavior 1).</summary>
    public TncGps EffectiveTncGps =>
        Kind == SourceKind.AprsTextSerial ? TncGps ?? Contracts.TncGps.Auto : Contracts.TncGps.Off;

    /// <summary>NMEA 0183's speed, the default for a GPS on a serial port (F3.6 decision 1).</summary>
    public const int DefaultGpsBaud = 4800;

    /// <summary>The speeds offered for a serial GPS; another can be typed.</summary>
    public static IReadOnlyList<int> CommonGpsBauds { get; } = [4800, 9600, 19200, 38400, 115200];

    /// <summary>The port commonly used for NMEA over TCP (F3.6 decision 1).</summary>
    public const int DefaultNmeaPort = 10110;

    /// <summary>gpsd's port.</summary>
    public const int DefaultGpsdPort = 2947;

    /// <summary>A new GPS on <paramref name="serialPort"/>, reading NMEA, named after it.</summary>
    public static SourceSettings GpsSerialFor(string serialPort, int? baud = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            serialPort.Trim(),
            SourceKind.Gps,
            SerialPort: serialPort.Trim(),
            Baud: baud,
            GpsLink: Contracts.GpsLink.Serial
        );

    /// <summary>A new GPS reached over TCP (<see cref="Contracts.GpsLink.Tcp"/>) or through gpsd, named after its host.</summary>
    public static SourceSettings GpsNetworkFor(GpsLink link, string host, int? port = null) =>
        new(Guid.NewGuid().ToString("N"), host.Trim(), SourceKind.Gps, Host: host.Trim(), Port: port, GpsLink: link);

    /// <summary>"/dev/ttyUSB0@4800", "pi.local:10110" or "pi.local:2947": where a GPS is read; null for other kinds.</summary>
    public string? GpsEndpoint =>
        Kind != SourceKind.Gps ? null
        : GpsLink == Contracts.GpsLink.Serial
            ? SerialPort is { } serial
                    ? string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{serial}@{Baud ?? DefaultGpsBaud}"
                    )
                : null
        : Host is { } host
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{host}:{Port ?? (GpsLink == Contracts.GpsLink.Gpsd ? DefaultGpsdPort : DefaultNmeaPort)}"
            )
        : null;

    /// <summary>The <see cref="TncType"/> of a TNC switched with the source's own scripts (F3.2 behavior 4.3).</summary>
    public const string CustomTncType = "custom";

    /// <summary>Whether it's on a serial port: KISS over serial, or TNC text output (F3.1, F3.2).</summary>
    public bool IsSerial => Kind is SourceKind.KissSerial or SourceKind.AprsTextSerial;

    /// <summary>Whether its kind can ever send: not TNC text output (F3.2 behavior 3), nor a GPS.</summary>
    public bool CanTransmit => Kind is SourceKind.AprsIs or SourceKind.KissTcp or SourceKind.KissSerial;

    /// <summary>Whether the station may send on it: a kind that can, not ticked RX only (F2.1 behavior 5).</summary>
    public bool MaySend => CanTransmit && !RxOnly;

    /// <summary>A new source reading a TNC's text output on <paramref name="serialPort"/> (F3.2 behavior 1.1).</summary>
    public static SourceSettings AprsTextSerialFor(string serialPort, int? baud = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            serialPort.Trim(),
            SourceKind.AprsTextSerial,
            SerialPort: serialPort.Trim(),
            Baud: baud
        );

    /// <summary>The speed a serial TNC runs at unless set: 9600, as most USB TNCs and radios (F3.1 decision 4).</summary>
    public const int DefaultBaud = 9600;

    /// <summary>The speeds offered for a serial TNC; another can be typed.</summary>
    public static IReadOnlyList<int> CommonBauds { get; } = [1200, 4800, 9600, 19200, 38400, 57600, 115200];

    /// <summary>A new KISS over serial source on <paramref name="serialPort"/>, named after it.</summary>
    public static SourceSettings KissSerialFor(string serialPort, int? baud = null, int? tncPort = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            serialPort.Trim(),
            SourceKind.KissSerial,
            TncPort: tncPort,
            SerialPort: serialPort.Trim(),
            Baud: baud
        );

    /// <summary>"/dev/ttyUSB0@9600": where a serial source connects; null for other kinds.</summary>
    public string? SerialEndpoint =>
        IsSerial && SerialPort is { } port
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{port}@{Baud ?? DefaultBaud}")
            : null;

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

    /// <summary>A KISS TNC on a serial port, such as a radio's built-in TNC on USB (0.5.0).</summary>
    KissSerial,

    /// <summary>
    /// TNC text output (TNC2) over serial, RX only: the lines a radio or TNC prints for the packets it hears, such as a
    /// TH-D72A in its APRS mode (F3.2 behaviors 1–3). Named "Radio in APRS mode" until 2026-10-18.
    /// </summary>
    AprsTextSerial,

    /// <summary>A GPS, over serial, TCP or gpsd (docs/features/F3.6-gps.md).</summary>
    Gps,
}

/// <summary>Whether a radio's GPS on its TNC port is used (docs/features/F3.13-tnc-gps.md, behavior 1).</summary>
public enum TncGps
{
    /// <summary>Used once its first sentence arrives (the default).</summary>
    Auto,

    /// <summary>Expected: a warning when no sentences arrive.</summary>
    On,

    /// <summary>Ignored, for when a better GPS is connected.</summary>
    Off,
}

/// <summary>How a GPS is reached (docs/features/F3.6-gps.md, behavior 1).</summary>
public enum GpsLink
{
    /// <summary>NMEA sentences on a serial port.</summary>
    Serial,

    /// <summary>NMEA sentences over a TCP connection.</summary>
    Tcp,

    /// <summary>gpsd, on this machine or another.</summary>
    Gpsd,
}
