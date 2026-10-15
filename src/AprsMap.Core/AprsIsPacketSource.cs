// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.AprsIsTcpClient;
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

    public ValueTask SetFilterAsync(StationProfile profile, CancellationToken cancellationToken) =>
        connection.SetFilterAsync(AprsIsPacketSourceFactory.EffectiveFilter(profile), cancellationToken);

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
    public IPacketSource Create(StationProfile profile)
    {
        var clientOptions = ClientOptions(profile, timeProvider);
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
    internal static AprsIsClientOptions ClientOptions(StationProfile profile, TimeProvider timeProvider)
    {
        var endpoint = AprsIsEndpoint.Parse(profile.Server);
        var clientOptions = new AprsIsClientOptions
        {
            Callsign = profile.Callsign,
            Passcode = profile.Passcode,
            Filter = EffectiveFilter(profile),
            Endpoint = endpoint,
            SoftwareName = SoftwareToken(Brand.DisplayName),
            SoftwareVersion = SoftwareToken(Brand.Version.Split('+')[0]),
            TimeProvider = timeProvider,
        };
        clientOptions.Validate();
        return clientOptions;
    }

    /// <summary>The radius of the stand-in filter, in km.</summary>
    internal const int DefaultRangeKm = 150;

    /// <summary>
    /// The filter sent at login: the profile's own, or a stand-in when it has none. On the user-defined filter port
    /// (14580) a login without a filter receives only messages addressed to it, so a new profile would show nothing.
    /// The stand-in is everything within <see cref="DefaultRangeKm"/> km of the station's location, or of its last
    /// position on APRS-IS when no location is set (aprs-is.net server-side filter commands <c>r/</c> and <c>m/</c>).
    /// </summary>
    // SHIM: G04 (typed APRS-IS filter builder), and until F1.2 makes the filter follow the map.
    internal static string EffectiveFilter(StationProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Filter))
        {
            return profile.Filter;
        }
        var range = DefaultRangeKm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return profile is { Latitude: { } lat, Longitude: { } lon }
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"r/{lat:0.####}/{lon:0.####}/{range}")
            : $"m/{range}";
    }

    // APRS-IS login fields are space-separated tokens.
    internal static string SoftwareToken(string value)
    {
        var token = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        return token.Length == 0 ? "unknown" : token;
    }
}
