// SPDX-License-Identifier: MIT
namespace AprsMap.Tiles.Tests;

/// <summary>Tile downloads follow the station's toggles (docs/features/F1.15-quick-toggles.md, acceptance test 1).</summary>
public sealed class TileDownloadGateTests
{
    private sealed class Switch(bool allowed) : ITileDownloads
    {
        public bool Allowed { get; set; } = allowed;
    }

    private sealed class Counting : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task WhileDownloadsAreOffNoRequestLeaves()
    {
        var downloads = new Switch(allowed: false);
        var server = new Counting();
        using var client = new HttpClient(new TileDownloadGate(downloads) { InnerHandler = server });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri("https://tiles.invalid/1/1/1.png"), TestContext.Current.CancellationToken)
        );
        Assert.Equal(0, server.Requests);

        downloads.Allowed = true;
        using var response = await client.GetAsync(
            new Uri("https://tiles.invalid/1/1/1.png"),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task WithoutAGateEverythingIsAllowed()
    {
        var server = new Counting();
        using var client = new HttpClient(new TileDownloadGate(null) { InnerHandler = server });

        using var response = await client.GetAsync(
            new Uri("https://tiles.invalid/1/1/1.png"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, server.Requests);
    }
}
