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
internal sealed class AprsIsPacketSource(AprsIsConnection connection) : IPacketSource, IPacketSender
{
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

    public ValueTask DisposeAsync() => connection.DisposeAsync();

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
                source.SerialEndpoint ?? source.Name
            );
        }
        if (source.Kind == SourceKind.KissTcp)
        {
            // Sends only what the engine lets through: TX enabled, a licensed profile, the Transmit switch (F2.1).
            return new KissPacketSource(
                new KissTcpConnection(
                    new KissTcpOptions
                    {
                        Host = source.Host ?? string.Empty,
                        Port = source.Port ?? SourceSettings.DefaultKissPort,
                        TncPort = source.TncPort ?? 0,
                        ReconnectDelay = options.Value.InitialRetryDelay,
                        MaxReconnectDelay = options.Value.MaxRetryDelay,
                        TimeProvider = timeProvider,
                    }
                ),
                source.KissEndpoint ?? source.Name
            );
        }
        if (source.Kind != SourceKind.AprsIs)
        {
            throw new NotSupportedException($"{source.Kind} sources aren't built yet.");
        }
        var clientOptions = ClientOptions(profile, source.Server ?? string.Empty, filter, timeProvider);
        clientOptions.ReconnectDelay = options.Value.InitialRetryDelay;
        clientOptions.MaxReconnectDelay = options.Value.MaxRetryDelay;
        return new AprsIsPacketSource(
            new AprsIsConnection(clientOptions, loggerFactory.CreateLogger<AprsIsConnection>())
        );
    }

    /// <summary>
    /// The APRS.NET options for a KISS over serial source (docs/features/F3.1-kiss-sources.md, behaviors 1.2, 4, 13):
    /// its port, speed, TNC port and control lines, the engine's reconnect waits, and a warning after 30 minutes with
    /// nothing at all from the TNC (decision 5), for a radio switched off behind a port that stays open.
    /// </summary>
    /// <remarks>
    /// A radio in its APRS mode is read as text with no setup (F3.2 behavior 2); a KISS TNC with a type is switched into
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
            QuietWarning = SerialQuietWarning,
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

    /// <summary>How long a serial TNC may send nothing before its pill asks whether it's switched on (F3.1 decision 5).</summary>
    internal static readonly TimeSpan SerialQuietWarning = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The APRS.NET client options for <paramref name="profile"/>, validated by APRS.NET (which owns the login rules).
    /// </summary>
    /// <exception cref="AprsNet.Packets.AprsValidationException">The server, callsign or filter is not usable; it has a code.</exception>
    /// <exception cref="ArgumentException">Another setting is not usable.</exception>
    internal static AprsIsClientOptions ClientOptions(
        StationProfile profile,
        string server,
        string filter,
        TimeProvider timeProvider
    )
    {
        var endpoint = AprsIsEndpoint.Parse(server);
        var clientOptions = new AprsIsClientOptions
        {
            Callsign = profile.Callsign,
            Passcode = profile.Passcode,
            // A listener ID logs in with -1 and APRS.NET refuses to send on it (F2.1 behavior 4).
            ListenOnly = AprsIsLogin.IsListenerId(profile.Callsign),
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
