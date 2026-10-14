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
    /// <exception cref="InvalidOperationException">The server, callsign, passcode or filter is not usable.</exception>
    internal static AprsIsClientOptions ClientOptions(StationProfile profile, TimeProvider timeProvider)
    {
        if (!AprsIsEndpoint.TryParse(profile.Server, out var endpoint))
        {
            throw new InvalidOperationException(
                $"'{profile.Server}' is not a valid APRS-IS server (host or host:port)."
            );
        }

        var clientOptions = new AprsIsClientOptions
        {
            Callsign = profile.Callsign,
            Passcode = profile.Passcode,
            Filter = profile.Filter,
            Endpoint = endpoint,
            SoftwareName = SoftwareToken(Brand.DisplayName),
            SoftwareVersion = SoftwareToken(Brand.Version.Split('+')[0]),
            TimeProvider = timeProvider,
        };
        try
        {
            clientOptions.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        return clientOptions;
    }

    // APRS-IS login fields are space-separated tokens.
    internal static string SoftwareToken(string value)
    {
        var token = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        return token.Length == 0 ? "unknown" : token;
    }
}
