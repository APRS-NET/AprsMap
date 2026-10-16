// SPDX-License-Identifier: MIT
using System.Net;
using AprsMap.Contracts;

namespace AprsMap.Client.Remote;

/// <summary>
/// Watches the hub connection's requests for the gateway refusing this app's contract version (a 409 to negotiate,
/// F1.14 behavior 8), which happens when the gateway is upgraded under a running browser: the automatic reconnect
/// keeps trying, and every call then reports <see cref="EngineErrorCodes.GatewayOutdated"/> instead of failing quietly.
/// </summary>
public sealed class ContractCheck
{
    private volatile bool _outdated;

    /// <summary>Whether the gateway has refused this app's contract version.</summary>
    public bool IsOutdated => _outdated;

    /// <summary>The connection's message handler: <paramref name="inner"/>, watched.</summary>
    public HttpMessageHandler Wrap(HttpMessageHandler inner) => new Watcher(this) { InnerHandler = inner };

    private sealed class Watcher(ContractCheck check) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (
                response.StatusCode == HttpStatusCode.Conflict
                && request.RequestUri?.AbsolutePath.EndsWith("/negotiate", StringComparison.Ordinal) == true
            )
            {
                check._outdated = true;
            }
            return response;
        }
    }
}
