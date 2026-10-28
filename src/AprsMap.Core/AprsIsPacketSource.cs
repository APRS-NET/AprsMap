// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.AprsIsTcpClient;
using AprsNet.Filters;
using AprsNet.Kiss;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AprsMap.Core;

/// <summary>An APRS-IS session kept up by APRS.NET's <see cref="AprsIsConnection"/>.</summary>
internal sealed class AprsIsPacketSource(AprsIsConnection connection) : IPacketSource, IPacketSender, ITrafficSource
{
    private readonly Lock _lock = new();
    private Action<TrafficEntry>? _traffic;

    /// <summary>
    /// Watches the lines to and from the server only while a console wants them (docs/features/F1.28-source-console.md,
    /// behavior 7): with no subscriber APRS.NET does no work for them.
    /// </summary>
    public void WatchTraffic(Action<TrafficEntry>? sink)
    {
        lock (_lock)
        {
            if (_traffic is null && sink is not null)
            {
                connection.Traffic += OnTraffic;
            }
            else if (_traffic is not null && sink is null)
            {
                connection.Traffic -= OnTraffic;
            }
            _traffic = sink;
        }
    }

    private void OnTraffic(object? sender, AprsIsTrafficEventArgs e) => _traffic?.Invoke(EntryOf(e, string.Empty));

    /// <summary>APRS.NET's line in the console's terms (F1.28 behaviors 2, 6); the login's passcode is masked already.</summary>
    internal static TrafficEntry EntryOf(AprsIsTrafficEventArgs e, string sourceId) =>
        new(
            e.At,
            sourceId,
            e.Direction == AprsIsTrafficDirection.Sent ? TrafficDirection.Out : TrafficDirection.In,
            e.Kind switch
            {
                AprsIsTrafficKind.LoginSent => TrafficKind.LoginSent,
                AprsIsTrafficKind.LoginResponse => TrafficKind.LoginResponse,
                AprsIsTrafficKind.ServerComment => TrafficKind.ServerComment,
                AprsIsTrafficKind.Packet => TrafficKind.Packet,
                AprsIsTrafficKind.FilterSent => TrafficKind.FilterSent,
                AprsIsTrafficKind.PacketSent => TrafficKind.Sent,
                _ => TrafficKind.CommandSent,
            },
            e.Line
        );

    public async Task<PacketSourceLogin> StartAsync(
        Action<PacketSourceEvent> stateChanged,
        CancellationToken cancellationToken
    )
    {
        // Whether the attempt that just failed had logged in: a drop then, an unreachable server otherwise.
        var wasConnected = false;
        connection.StateChanged += (_, e) =>
        {
            switch (e.State)
            {
                case AprsIsConnectionState.Connecting:
                    stateChanged(new PacketSourceEvent(ConnectionState.Connecting));
                    break;
                case AprsIsConnectionState.Connected when e.Login is { } login:
                    wasConnected = true;
                    stateChanged(new PacketSourceEvent(ConnectionState.Connected, ToLogin(login)));
                    break;
                case AprsIsConnectionState.Reconnecting:
                    var error = AprsIsErrors.From(e.Error, wasConnected);
                    wasConnected = false;
                    stateChanged(
                        // Retrying won't help either: a refused login, or a KISS TNC where a server should be.
                        e.Error
                            is LoginRejectedException
                                or KissTncLoginException
                            ? new PacketSourceEvent(ConnectionState.Refused, Error: error)
                            : new PacketSourceEvent(ConnectionState.Reconnecting, RetryIn: e.RetryIn, Error: error)
                    );
                    break;
                default:
                    break; // NotStarted and Closed: the engine reports those itself
            }
        };
        return ToLogin(await connection.StartAsync(cancellationToken).ConfigureAwait(false));
    }

    public IAsyncEnumerable<AprsPacket> ReadPacketsAsync(CancellationToken cancellationToken) =>
        connection.ReadPacketsAsync(cancellationToken);

    public ValueTask SetFilterAsync(string filter, CancellationToken cancellationToken) =>
        connection.SetFilterAsync(filter, cancellationToken);

    public ValueTask SendAsync(AprsPacket packet, CancellationToken cancellationToken) =>
        connection.SendAsync(packet, cancellationToken);

    public ValueTask DisposeAsync()
    {
        WatchTraffic(null);
        return connection.DisposeAsync();
    }

    private static PacketSourceLogin ToLogin(LoginResult login) =>
        new(login.Server ?? string.Empty, login.Status == LoginStatus.Verified);
}

/// <summary>Creates APRS.NET connections from a <see cref="StationProfile"/>.</summary>
internal sealed class AprsIsPacketSourceFactory(
    IOptions<EngineOptions> options,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider
) : IPacketSourceFactory
{
    /// <exception cref="NotSupportedException">A kind that isn't built yet: GPS.</exception>
    public IPacketSource Create(StationProfile profile, SourceSettings source, string filter)
    {
        if (source.IsSerial)
        {
            return new KissPacketSource(
                new KissSerialConnection(SerialOptions(source, options.Value, timeProvider)),
                source.SerialEndpoint ?? source.Name,
                source.Kind == SourceKind.KissSerial ? SetupOf(source) : null,
                source.EffectiveTncGps, // the radio's GPS, in its text output only (F3.13)
                timeProvider
            );
        }
        if (source.Kind == SourceKind.KissTcp)
        {
            // Sends only what the engine lets through: not RX only, a licensed profile, the Transmit switch (F2.1).
            return new KissPacketSource(
                new KissTcpConnection(
                    new KissTcpOptions
                    {
                        Host = source.Host ?? string.Empty,
                        Port = source.Port ?? SourceSettings.DefaultKissPort,
                        TncPort = source.TncPort ?? 0,
                        ReconnectDelay = options.Value.InitialRetryDelay,
                        MaxReconnectDelay = options.Value.MaxRetryDelay,
                        // A TNC server keeps the link up while the radio behind it is off (F3.1 decision 5).
                        QuietWarning = TncQuietWarning,
                        TimeProvider = timeProvider,
                    }
                ),
                source.KissEndpoint ?? source.Name
            );
        }
        if (source.Kind == SourceKind.Agwpe)
        {
            // Raw frames on one radio port, sent with the same gates as a KISS TNC (docs/features/F3.8-agwpe.md).
            return new KissPacketSource(
                new AgwpeConnection(
                    new AgwpeOptions
                    {
                        Host = source.Host ?? string.Empty,
                        Port = source.Port ?? SourceSettings.DefaultAgwpePort,
                        TncPort = source.TncPort ?? 0,
                        ReconnectDelay = options.Value.InitialRetryDelay,
                        MaxReconnectDelay = options.Value.MaxRetryDelay,
                        QuietWarning = TncQuietWarning, // as for KISS over TCP (#378)
                        TimeProvider = timeProvider,
                    }
                ),
                source.AgwpeEndpoint ?? source.Name
            );
        }
        if (source.Kind == SourceKind.Gps)
        {
            return GpsSource(source);
        }
        if (source.Kind != SourceKind.AprsIs)
        {
            throw new NotSupportedException($"{source.Kind} sources aren't built yet.");
        }
        var clientOptions = ClientOptions(
            profile,
            source.Server ?? string.Empty,
            filter,
            timeProvider,
            options.Value.ReceiveOnly
        );
        clientOptions.ReconnectDelay = options.Value.InitialRetryDelay;
        clientOptions.MaxReconnectDelay = options.Value.MaxRetryDelay;
        return new AprsIsPacketSource(
            new AprsIsConnection(clientOptions, loggerFactory.CreateLogger<AprsIsConnection>())
        );
    }

    /// <summary>
    /// A GPS by its link (docs/features/F3.6-gps.md, behavior 1): NMEA from a serial port or a TCP connection, read by
    /// the app and parsed by APRS.NET, or gpsd through APRS.NET's client.
    /// </summary>
    private IPacketSource GpsSource(SourceSettings source)
    {
        var endpoint = source.GpsEndpoint ?? source.Name;
        return source.GpsLink switch
        {
            GpsLink.Gpsd => new GpsdGpsSource(
                new AprsNet.Gps.GpsdOptions
                {
                    Host = source.Host ?? string.Empty,
                    Port = source.Port ?? SourceSettings.DefaultGpsdPort,
                    ReconnectDelay = options.Value.InitialRetryDelay,
                    MaxReconnectDelay = options.Value.MaxRetryDelay,
                    TimeProvider = timeProvider,
                },
                timeProvider
            ),
            GpsLink.Tcp => new NmeaGpsSource(
                async token =>
                {
                    var client = new System.Net.Sockets.TcpClient();
                    try
                    {
                        await client
                            .ConnectAsync(
                                source.Host ?? string.Empty,
                                source.Port ?? SourceSettings.DefaultNmeaPort,
                                token
                            )
                            .ConfigureAwait(false);
                        return new OwningStream(client.GetStream(), client);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                },
                endpoint,
                timeProvider
            ),
            _ => new NmeaGpsSource(
                _ =>
                {
                    var port = new System.IO.Ports.SerialPort(
                        source.SerialPort ?? string.Empty,
                        source.Baud ?? SourceSettings.DefaultGpsBaud
                    );
                    try
                    {
                        port.Open();
                        return Task.FromResult<Stream>(new OwningStream(port.BaseStream, port));
                    }
                    catch
                    {
                        port.Dispose();
                        throw;
                    }
                },
                endpoint,
                timeProvider
            ),
        };
    }

    /// <summary>
    /// The APRS.NET options for a KISS over serial source (docs/features/F3.1-kiss-sources.md, behaviors 1.2, 4, 13):
    /// its port, speed, TNC port and control lines, the engine's reconnect waits, and a warning after 30 minutes with
    /// nothing at all from the TNC (decision 5), for a radio switched off behind a port that stays open.
    /// </summary>
    /// <remarks>
    /// TNC text output is read as text with no setup (F3.2 behavior 2); a KISS TNC with a type is switched into
    /// KISS each time the port opens (behavior 5).
    /// </remarks>
    internal static KissSerialOptions SerialOptions(SourceSettings source, EngineOptions engine, TimeProvider time) =>
        new()
        {
            TextMode = source.Kind == SourceKind.AprsTextSerial,
            Setup = source.Kind == SourceKind.KissSerial ? SetupOf(source) : null,
            PortName = source.SerialPort ?? string.Empty,
            BaudRate = source.Baud ?? SourceSettings.DefaultBaud,
            TncPort = source.TncPort ?? 0,
            DtrEnable = source.DtrEnable ?? true,
            RtsEnable = source.RtsEnable ?? true,
            ReconnectDelay = engine.InitialRetryDelay,
            MaxReconnectDelay = engine.MaxRetryDelay,
            QuietWarning = TncQuietWarning,
            GpsTimeout = engine.TncGpsTimeout, // the radio's GPS gone (F3.13 behavior 3)
            TimeProvider = time,
        };

    /// <summary>
    /// How <paramref name="source"/>'s TNC is switched into KISS (F3.2 behavior 4): null for a TNC in KISS already, a
    /// type APRS.NET knows, or the source's own scripts.
    /// </summary>
    /// <exception cref="FormatException">A custom script can't be read.</exception>
    /// <exception cref="ArgumentException">The type isn't known.</exception>
    internal static TncSetup? SetupOf(SourceSettings source) =>
        source.TncType switch
        {
            null or "" => null,
            SourceSettings.CustomTncType => new TncSetup(
                SourceSettings.CustomTncType,
                source.Name,
                TncScript.Parse(source.EnterScript ?? string.Empty),
                string.IsNullOrWhiteSpace(source.LeaveScript) ? null : TncScript.Parse(source.LeaveScript)
            ),
            var id => TncSetup.Find(id) ?? throw new ArgumentException($"No TNC type '{id}'.", nameof(source)),
        };

    /// <summary>How long a TNC may send nothing before its pill asks whether it's switched on (F3.1 decision 5).</summary>
    internal static readonly TimeSpan TncQuietWarning = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The APRS.NET client options for <paramref name="profile"/>, validated by APRS.NET (which owns the login rules).
    /// </summary>
    /// <exception cref="AprsNet.Packets.AprsValidationException">The server, callsign or filter is not usable; it has a code.</exception>
    /// <exception cref="ArgumentException">Another setting is not usable.</exception>
    /// <param name="receiveOnly">The engine was started receive only (<c>Engine:ReceiveOnly</c>).</param>
    internal static AprsIsClientOptions ClientOptions(
        StationProfile profile,
        string server,
        string filter,
        TimeProvider timeProvider,
        bool receiveOnly = false
    )
    {
        var endpoint = AprsIsEndpoint.Parse(server);
        var clientOptions = new AprsIsClientOptions
        {
            Callsign = profile.Callsign,
            // Receive only for good (F1.26 behavior 7.2): an unverified login, which a server won't take packets from.
            Passcode = receiveOnly ? -1 : profile.Passcode,
            // A listener ID logs in with -1 and APRS.NET refuses to send on it (F2.1 behavior 4).
            ListenOnly = receiveOnly || AprsIsLogin.IsListenerId(profile.Callsign),
            Filter = filter,
            Endpoint = endpoint,
            SoftwareName = SoftwareToken(Brand.DisplayName),
            SoftwareVersion = SoftwareToken(Brand.Version.Split('+')[0]),
            TimeProvider = timeProvider,
        };
        clientOptions.Validate();
        return clientOptions;
    }

    // APRS-IS login fields are space-separated tokens.
    internal static string SoftwareToken(string value)
    {
        var token = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        return token.Length == 0 ? "unknown" : token;
    }
}
