// SPDX-License-Identifier: AGPL-3.0-only
namespace AprsMap.UI.Platform;

/// <summary>
/// Which station the desktop app shows (docs/features/F1.39-desktop-gateway-client.md, behavior 1): its own engine, or
/// a gateway's as its client.
/// </summary>
/// <param name="Gateway">The gateway's address; null for this computer's own engine.</param>
/// <param name="ApiKey">The gateway's API key (F1.38); null to only watch.</param>
public sealed record GatewayClientSettings(Uri? Gateway = null, string? ApiKey = null)
{
    /// <summary>The address offered first: the gateway's admin port on this computer (F1.37 behavior 2).</summary>
    public static Uri DefaultGateway { get; } = new("http://127.0.0.1:8081/");
}

/// <summary>The desktop's choice of station (F1.39 behavior 1), kept in its settings folder; null in the browser.</summary>
public interface IGatewayClientHost
{
    /// <summary>The saved choice: what the app starts with next time.</summary>
    GatewayClientSettings Settings { get; }

    /// <summary>The gateway this run shows, or null when it runs its own engine.</summary>
    Uri? Connected { get; }

    /// <summary>Saves the choice for the next start (the key readable by this user only, behavior 7).</summary>
    Task SaveAsync(GatewayClientSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Closes the app and opens it again, into the saved choice; false when it can't open itself again.</summary>
    bool Restart();

    /// <summary>Tests a gateway's address and API key without saving them (#419).</summary>
    Task<ConnectionTestOutcome> TestAsync(Uri gateway, string? apiKey, CancellationToken cancellationToken = default);
}
