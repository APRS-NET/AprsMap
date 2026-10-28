// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Text;
using AprsMap.Contracts;
using AprsMap.UI.Platform;

namespace AprsMap.UI.Tests;

/// <summary>What the desktop makes of the gateway's <c>/auth/me</c> answers to its API key.</summary>
public sealed class GatewayOperatorSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Answering(HttpResponseMessage answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(answer);
    }

    private static async Task<AuthInfo> MeAsync(HttpStatusCode status, string body = "")
    {
        using var handler = new Answering(
            new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8081/") };
        return await new GatewayOperatorSession(client, passwordSignIn: false).GetAsync(Ct);
    }

    [Fact]
    public async Task AWaitIsToldApartFromAWrongKey()
    {
        // #450: a computer waiting after too many wrong keys is told how long, not that its key is wrong.
        var waiting = await MeAsync(
            HttpStatusCode.TooManyRequests,
            """{"code":"auth.too-many-tries","message":"Too many tries.","args":{"minutes":"12"}}"""
        );
        var refused = await MeAsync(HttpStatusCode.Unauthorized);

        Assert.Equal(new AuthInfo(false, ApiKey: true, WaitMinutes: 12), waiting);
        Assert.Equal(new AuthInfo(false, ApiKey: true), refused);
        Assert.Equal(1, (await MeAsync(HttpStatusCode.TooManyRequests, "not json")).WaitMinutes); // at least a minute
    }
}
