// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Text;
using AprsMap.Contracts;
using AprsMap.UI.Platform;

namespace AprsMap.UI.Tests;

/// <summary>The connection test's request and what it makes of the answers (#419).</summary>
public sealed class GatewayConnectionTestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Request = request;
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ItSendsTheKeyAsAHeaderAndReadsTheAnswer()
    {
        using var handler = new Answering(_ =>
            Json($$"""{"product":"{{Brand.Slug}}","version":"0.8.0","contractVersion":2,"key":1}""")
        );

        var outcome = await GatewayConnectionTest.RunAsync(new Uri("http://gw:8081/aprs"), "the-key", handler, Ct);

        Assert.Equal(new GatewayTest(Brand.Slug, "0.8.0", 2, ApiKeyCheck.Accepted), outcome.Gateway);
        Assert.Equal("http://gw:8081/aprs/auth/test", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer the-key", handler.Request.Headers.Authorization!.ToString());
        Assert.DoesNotContain("the-key", handler.Request.RequestUri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnythingButAGatewaysAnswerIsNotAGateway()
    {
        using var missing = new Answering(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var page = new Answering(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html></html>"),
        });

        Assert.True((await GatewayConnectionTest.RunAsync(new Uri("http://gw/"), null, missing, Ct)).NotAGateway);
        Assert.True((await GatewayConnectionTest.RunAsync(new Uri("http://gw/"), null, page, Ct)).NotAGateway);
    }

    [Fact]
    public async Task NothingAnsweringIsUnreachableWithTheReason()
    {
        using var down = new Answering(_ => throw new HttpRequestException("Connection refused"));

        var outcome = await GatewayConnectionTest.RunAsync(new Uri("http://gw/"), null, down, Ct);

        Assert.True(outcome.Unreachable);
        Assert.Equal("Connection refused", outcome.Detail);
    }
}
