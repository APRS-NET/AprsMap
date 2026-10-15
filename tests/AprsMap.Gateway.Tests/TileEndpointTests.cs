// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AprsMap.Contracts;

namespace AprsMap.Gateway.Tests;

/// <summary>The gateway's tile endpoint (docs/features/F1.4-map.md, behavior 10; acceptance test 2).</summary>
public sealed class TileEndpointTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItListsTheConfiguredProviders()
    {
        using var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync(
            new Uri("/tiles/providers", UriKind.Relative),
            EngineJsonContext.Default.IReadOnlyListTileProviderInfo,
            Ct
        );

        Assert.Equal("osm", Assert.Single(providers!).Key);
    }

    [Theory]
    [InlineData("/tiles/nope/1/0/0")]
    [InlineData("/tiles/osm/25/0/0")]
    [InlineData("/tiles/osm/2/4/0")]
    public async Task UnknownProvidersAndOffGridTilesAreNotFound(string path)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TwoViewersShareOneFetchAndTheRefererGoesUpstream()
    {
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();
        var before = factory.TileServer.Requests;
        using var a = new HttpRequestMessage(HttpMethod.Get, "/tiles/osm/9/100/200");
        a.Headers.Referrer = new Uri("https://club.example/map/");
        using var b = new HttpRequestMessage(HttpMethod.Get, "/tiles/osm/9/100/200");

        var responses = await Task.WhenAll(first.SendAsync(a, Ct), second.SendAsync(b, Ct));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal("image/png", responses[0].Content.Headers.ContentType?.MediaType);
        Assert.Equal(before + 1, factory.TileServer.Requests);
        Assert.Contains("https://club.example/map/", factory.TileServer.Referers);
        Assert.True(responses[0].Headers.CacheControl?.MaxAge > TimeSpan.Zero);
    }

    [Fact]
    public async Task TheBrowsersOwnRevalidationGetsA304()
    {
        using var client = factory.CreateClient();
        var first = await client.GetAsync(new Uri("/tiles/osm/9/101/200", UriKind.Relative), Ct);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        using var again = new HttpRequestMessage(HttpMethod.Get, "/tiles/osm/9/101/200");
        again.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Tag));

        var response = await client.SendAsync(again, Ct);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }
}
