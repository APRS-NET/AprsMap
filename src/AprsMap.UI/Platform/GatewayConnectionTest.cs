// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AprsMap.Contracts;

namespace AprsMap.UI.Platform;

/// <summary>What a connection test found (#419): the gateway's answer, or why there was none.</summary>
/// <param name="Gateway">The gateway's answer; null when there was none.</param>
/// <param name="Unreachable">Nothing answered at the address: no network, wrong address, or the gateway down.</param>
/// <param name="Detail">Why it couldn't be reached, in the system's words; null otherwise.</param>
public sealed record ConnectionTestOutcome(GatewayTest? Gateway, bool Unreachable = false, string? Detail = null)
{
    /// <summary>Something answered, but not as a gateway of this kind (or one older than the test).</summary>
    public bool NotAGateway => Gateway is null && !Unreachable;
}

/// <summary>
/// Tests a gateway's address and API key without saving them (#419): one <c>GET</c> of <see cref="AuthPaths.Test"/>,
/// with the key as <c>Authorization: Bearer</c>, never in the address.
/// </summary>
public static class GatewayConnectionTest
{
    /// <summary>How long the test waits for an answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Tests <paramref name="gateway"/> with <paramref name="apiKey"/>, through <paramref name="handler"/>.</summary>
    public static async Task<ConnectionTestOutcome> RunAsync(
        Uri gateway,
        string? apiKey,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var baseUri = gateway.AbsolutePath.EndsWith('/') ? gateway : new Uri(gateway + "/");
        using var http = handler is null
            ? new HttpClient { BaseAddress = baseUri, Timeout = Timeout }
            : new HttpClient(handler, disposeHandler: false) { BaseAddress = baseUri, Timeout = Timeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(AuthPaths.Test, UriKind.Relative));
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(ApiKeys.Scheme, apiKey);
        }
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new ConnectionTestOutcome(null);
            }
            var answer = await response
                .Content.ReadFromJsonAsync(EngineJsonContext.Default.GatewayTest, cancellationToken)
                .ConfigureAwait(false);
            return new ConnectionTestOutcome(answer is { Product.Length: > 0, Version.Length: > 0 } ? answer : null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new ConnectionTestOutcome(null); // something else answered
        }
        catch (Exception ex)
            when (ex is HttpRequestException
                || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested
            )
        {
            return new ConnectionTestOutcome(null, Unreachable: true, ex.Message);
        }
    }
}
