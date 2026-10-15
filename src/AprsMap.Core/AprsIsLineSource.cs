// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.AprsIsTcpClient;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>Reads packet lines from APRS-IS with the APRS.NET client.</summary>
internal sealed class AprsIsLineSource(AprsIsClient client) : IPacketLineSource
{
    public async Task<string> ConnectAsync(CancellationToken cancellationToken)
    {
        var login = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return login.Server ?? string.Empty;
    }

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) =>
        client.ReadLinesAsync(cancellationToken);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

/// <summary>Creates APRS.NET clients from a <see cref="StationProfile"/>.</summary>
internal sealed class AprsIsLineSourceFactory(ILoggerFactory loggerFactory, TimeProvider timeProvider)
    : IPacketLineSourceFactory
{
    public IPacketLineSource Create(StationProfile profile) =>
        new AprsIsLineSource(
            new AprsIsClient(ClientOptions(profile, timeProvider), loggerFactory.CreateLogger<AprsIsClient>())
        );

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
