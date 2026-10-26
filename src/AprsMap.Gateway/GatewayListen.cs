// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;

namespace AprsMap.Gateway;

/// <summary>
/// Where the gateway listens (docs/features/F1.37-gateway-service.md, behavior 2): <c>Gateway:Listen</c> for the
/// published, read-only app and <c>Gateway:OperatorListen</c> for the admin app, each as <c>address:port</c>.
/// </summary>
internal static class GatewayListen
{
    /// <summary>The published app's address when only the admin one is set.</summary>
    public const string DefaultListen = "0.0.0.0:8080";

    /// <summary>
    /// The URLs to bind and the admin app's port, or null when neither address is set (the ports work as before:
    /// <c>HTTP_PORTS</c> and <c>Gateway:OperatorPort</c>). Throws <see cref="InvalidOperationException"/> naming the
    /// setting when an address can't be used.
    /// </summary>
    public static (IReadOnlyList<string> Urls, int? OperatorPort)? Urls(IConfiguration configuration)
    {
        var listen = configuration["Gateway:Listen"];
        var admin = configuration["Gateway:OperatorListen"];
        if (string.IsNullOrWhiteSpace(listen) && string.IsNullOrWhiteSpace(admin))
        {
            return null;
        }
        var (main, mainPort) = Parse(string.IsNullOrWhiteSpace(listen) ? DefaultListen : listen, "Gateway:Listen");
        if (string.IsNullOrWhiteSpace(admin))
        {
            return ([main], null);
        }
        var (adminUrl, adminPort) = Parse(admin, "Gateway:OperatorListen");
        if (adminPort == mainPort)
        {
            throw new InvalidOperationException(
                $"Gateway:OperatorListen ({admin}) needs a port of its own, not Gateway:Listen's {mainPort}."
            );
        }
        return ([main, adminUrl], adminPort);
    }

    /// <summary><c>127.0.0.1:8081</c>, <c>[::1]:8081</c> or <c>localhost:8081</c> as a URL to bind, and its port.</summary>
    internal static (string Url, int Port) Parse(string address, string setting)
    {
        var text = address.Trim();
        var colon = text.LastIndexOf(':');
        if (
            colon <= 0
            || !int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535
        )
        {
            throw new InvalidOperationException(
                $"{setting} is '{address}': it needs an address and a port, such as 127.0.0.1:8081."
            );
        }
        return ($"http://{text}", port);
    }
}
