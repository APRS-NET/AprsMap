// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using AprsMap.Tiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Tiles.Tests;

/// <summary>The tile cache and its policy (docs/features/F1.4-map.md, behaviors 9–11; acceptance test 1).</summary>
public sealed class TileServiceTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aprsmap-tiles-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeServer _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // A background refresh can still be writing for a moment, and Windows won't delete an open file.
        for (var attempt = 1; Directory.Exists(_folder); attempt++)
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    private TileService Service(int maxMegabytes = 512, Dictionary<string, TileProviderOptions>? providers = null) =>
        new(
            Options.Create(
                new TilesOptions
                {
                    CacheFolder = _folder,
                    MaxCacheMegabytes = maxMegabytes,
                    Providers = providers ?? new(StringComparer.Ordinal),
                }
            ),
            new Factory(_server),
            _time,
            NullLogger<TileService>.Instance
        );

    [Fact]
    public async Task AFreshTileIsServedFromDiskUntilItsMaxAgeLessAge()
    {
        _server.Respond = _ => FakeServer.Tile("max-age=3600", age: 600);
        using var tiles = Service();

        Assert.Equal(TileOutcome.Miss, (await tiles.GetTileAsync("osm", 5, 10, 12, cancellationToken: Ct))!.Outcome);
        _time.Advance(TimeSpan.FromMinutes(49));
        Assert.Equal(TileOutcome.Hit, (await tiles.GetTileAsync("osm", 5, 10, 12, cancellationToken: Ct))!.Outcome);

        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task WithoutCachingHeadersATileIsFreshForSevenDays()
    {
        _server.Respond = _ => FakeServer.Tile(cacheControl: null);
        using var tiles = Service();

        var first = await tiles.GetTileAsync("osm", 1, 0, 0, cancellationToken: Ct);

        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromDays(7), first!.Expires);
    }

    [Fact]
    public async Task AnExpiredTileIsRevalidatedAndA304RenewsIt()
    {
        _server.Respond = _ => FakeServer.Tile("max-age=60", etag: "\"v1\"");
        using var tiles = Service();
        await tiles.GetTileAsync("osm", 3, 1, 1, cancellationToken: Ct);
        _time.Advance(TimeSpan.FromMinutes(2));
        _server.Respond = request =>
        {
            Assert.Equal("\"v1\"", request.Headers.IfNoneMatch.Single().ToString());
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModified.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=3600");
            return notModified;
        };

        var revalidated = await tiles.GetTileAsync("osm", 3, 1, 1, cancellationToken: Ct);

        Assert.Equal(TileOutcome.Revalidated, revalidated!.Outcome);
        Assert.Equal(Png, revalidated.Data);
        Assert.Equal(TileOutcome.Hit, (await tiles.GetTileAsync("osm", 3, 1, 1, cancellationToken: Ct))!.Outcome);
    }

    [Fact]
    public async Task WithinStaleWhileRevalidateTheOldTileIsServedAtOnceAndRefreshedBehind()
    {
        _server.Respond = _ => FakeServer.Tile("max-age=60, stale-while-revalidate=604800");
        using var tiles = Service();
        await tiles.GetTileAsync("osm", 4, 2, 2, cancellationToken: Ct);
        _time.Advance(TimeSpan.FromHours(1));

        var stale = await tiles.GetTileAsync("osm", 4, 2, 2, cancellationToken: Ct);

        Assert.Equal(TileOutcome.StaleRevalidating, stale!.Outcome);
        await WaitUntil(() => _server.Requests.Count == 2); // the revalidation, in the background

        // Done once the refreshed tile is served from the cache.
        TileOutcome? outcome = null;
        for (var i = 0; i < 400 && outcome != TileOutcome.Hit; i++)
        {
            outcome = (await tiles.GetTileAsync("osm", 4, 2, 2, cancellationToken: Ct))?.Outcome;
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
        }
        Assert.Equal(TileOutcome.Hit, outcome);
    }

    [Theory]
    [InlineData(3, TileOutcome.Stale)]
    [InlineData(9, TileOutcome.Failed)]
    public async Task OnANetworkErrorAStaleTileIsServedOnlyWithinStaleIfError(int daysLater, TileOutcome expected)
    {
        _server.Respond = _ => FakeServer.Tile("max-age=60, stale-if-error=604800");
        using var tiles = Service();
        await tiles.GetTileAsync("osm", 4, 3, 3, cancellationToken: Ct);
        _time.Advance(TimeSpan.FromDays(daysLater));
        _server.Respond = _ => throw new HttpRequestException("offline");

        Assert.Equal(expected, (await tiles.GetTileAsync("osm", 4, 3, 3, cancellationToken: Ct))!.Outcome);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneTileShareOneDownloadAndAtMostFourAreInFlight()
    {
        var gate = new TaskCompletionSource();
        _server.Hold = gate.Task;
        _server.Respond = _ => FakeServer.Tile("max-age=3600");
        using var tiles = Service();

        var same = Enumerable
            .Range(0, 5)
            .Select(_ => tiles.GetTileAsync("osm", 6, 1, 1, cancellationToken: Ct))
            .ToList();
        var others = Enumerable
            .Range(2, 8)
            .Select(x => tiles.GetTileAsync("osm", 6, x, 1, cancellationToken: Ct))
            .ToList();
        await WaitUntil(() => _server.InFlight == 4);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
        Assert.Equal(4, _server.InFlight);
        gate.SetResult();
        await Task.WhenAll(same.Concat(others));

        Assert.Equal(9, _server.Requests.Count); // 1 for the five identical requests, 8 for the others
        Assert.True(_server.MaxInFlight <= 4);
    }

    [Fact]
    public async Task PastTheCapTheLeastRecentlyUsedTilesGo()
    {
        _server.Respond = _ => FakeServer.Tile("max-age=3600", size: 400 * 1024);
        using var tiles = Service(maxMegabytes: 1);
        await tiles.GetTileAsync("osm", 8, 1, 1, cancellationToken: Ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        await tiles.GetTileAsync("osm", 8, 2, 1, cancellationToken: Ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        await tiles.GetTileAsync("osm", 8, 1, 1, cancellationToken: Ct); // used again: now 2/1 is the oldest
        _time.Advance(TimeSpan.FromSeconds(1));

        await tiles.GetTileAsync("osm", 8, 3, 1, cancellationToken: Ct); // 1.2 MB: over the cap

        Assert.True(File.Exists(Path.Combine(_folder, "osm", "8", "1", "1.png")));
        Assert.False(File.Exists(Path.Combine(_folder, "osm", "8", "2", "1.png")));
        Assert.True(File.Exists(Path.Combine(_folder, "osm", "8", "3", "1.png")));
    }

    [Theory]
    [InlineData("nope", 1, 0, 0)]
    [InlineData("osm", 20, 0, 0)]
    [InlineData("osm", 2, 4, 0)]
    [InlineData("osm", 2, 0, -1)]
    public async Task UnknownProvidersAndCoordinatesOffTheGridAreRefused(string provider, int z, int x, int y)
    {
        using var tiles = Service();

        Assert.Null(await tiles.GetTileAsync(provider, z, x, y, cancellationToken: Ct));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task TheRefererGoesUpstreamAndAnInvertedRowIsComputed()
    {
        _server.Respond = _ => FakeServer.Tile("max-age=60");
        var tms = new TileProviderOptions
        {
            Name = "TMS",
            UrlTemplate = "https://tiles.example/{z}/{x}/{-y}.png",
            Attribution = "x",
        };
        using var tiles = Service(providers: new(StringComparer.Ordinal) { ["tms"] = tms });

        await tiles.GetTileAsync("tms", 2, 1, 0, "https://map.example/", Ct);

        var request = Assert.Single(_server.Requests);
        Assert.Equal("https://tiles.example/2/1/3.png", request.RequestUri!.ToString());
        Assert.Equal("https://map.example/", request.Headers.Referrer!.ToString());
        Assert.Equal(["tms"], tiles.Providers.Select(p => p.Key));
    }

    [Fact]
    public void TheDefaultProviderIsOpenStreetMapWithItsAttribution()
    {
        using var tiles = Service();

        var osm = Assert.Single(tiles.Providers);
        Assert.Equal("osm", osm.Key);
        Assert.Equal("© OpenStreetMap contributors", osm.Attribution);
        Assert.Equal("https://www.openstreetmap.org/copyright", osm.AttributionUrl);
    }

    [Fact]
    public void TheHttpClientIdentifiesTheAppNotALibrary()
    {
        using var provider = new ServiceCollection().AddAprsMapTiles().BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(TileService.HttpClientName);

        var agent = client.DefaultRequestHeaders.UserAgent.ToString();
        Assert.StartsWith(
            Contracts.Brand.DisplayName.Replace(" ", "", StringComparison.Ordinal) + "/",
            agent,
            StringComparison.Ordinal
        );
        Assert.Contains(Contracts.Brand.SourceUrl, agent, StringComparison.Ordinal);
        Assert.NotNull(provider.GetRequiredService<ITileService>());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }

    private sealed class Factory(FakeServer server) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(server, disposeHandler: false);
    }

    /// <summary>A tile server in memory: <see cref="Respond"/> answers, <see cref="Hold"/> delays every answer.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private int _inFlight;
        private int _maxInFlight;

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.NotFound);

        public Task Hold { get; set; } = Task.CompletedTask;

        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        public int InFlight => Volatile.Read(ref _inFlight);

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public static HttpResponseMessage Tile(
            string? cacheControl = null,
            int age = 0,
            string? etag = null,
            int size = 4
        )
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(size == 4 ? Png : new byte[size]),
            };
            if (cacheControl is not null)
            {
                response.Headers.CacheControl = CacheControlHeaderValue.Parse(cacheControl);
            }
            if (age > 0)
            {
                response.Headers.Age = TimeSpan.FromSeconds(age);
            }
            if (etag is not null)
            {
                response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            }
            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Enqueue(request);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (
                now > (seen = Volatile.Read(ref _maxInFlight))
                && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen
            ) { }
            try
            {
                await Hold.ConfigureAwait(false);
                return Respond(request);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
