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
internal sealed class AprsIsPacketSource(AprsIsConnection connection) : IPacketSource
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
                        e.Error is LoginRejectedException
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
    /// <exception cref="NotSupportedException">A kind that isn't built yet: KISS over serial and GPS.</exception>
    public IPacketSource Create(StationProfile profile, SourceSettings source, string filter)
    {
        if (source.Kind == SourceKind.KissTcp)
        {
            // F3.1: receive only. Nothing here sends; transmitting through a TNC is Phase 2, behind a verified login.
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
