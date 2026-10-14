// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

public sealed class GeocodingTests : IDisposable
{
    private const string Boulder = """
        [
          {"place_id":1,"lat":"40.0149856","lon":"-105.2705456","display_name":"Boulder, Boulder County, Colorado, United States","type":"city"},
          {"place_id":2,"lat":"not a number","lon":"-105.0","display_name":"Broken"},
          {"place_id":3,"lat":"91","lon":"0","display_name":"Off the globe"},
          {"place_id":4,"lat":"40.0","lon":"-105.0","display_name":""},
          {"place_id":5,"lat":"42.9","lon":"-87.8","display_name":"Boulder, Wisconsin"},
          {"place_id":6,"display_name":"No coordinates"},
          {"place_id":7,"lat":39.7,"lon":-105.0,"display_name":"Numbers, not strings"}
        ]
        """;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHandler _handler = new();
    private readonly ListLogger _logger = new();
    private readonly ServiceProvider _services;

    public GeocodingTests()
    {
        _handler.Respond = _ => Json(Boulder);
        _handler.Time = _time;
        _services = new ServiceCollection()
            .AddHttpClient(NominatimGeocoder.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _handler)
            .Services.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _services.Dispose();

    private NominatimGeocoder Create(GeocodingOptions? options = null) =>
        new(
            _services.GetRequiredService<IHttpClientFactory>(),
            Options.Create(options ?? new GeocodingOptions()),
            _time,
            _logger
        );

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ALookupIdentifiesTheAppAndAsksForFiveResultsInTheUsersLanguage()
    {
        var geocoder = Create();

        var results = await geocoder.GeocodeAsync("  Boulder,\t CO  ", "en-GB", Ct);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://nominatim.openstreetmap.org/search?q=Boulder%2C%20CO&format=jsonv2&limit=5", request.Uri);
        Assert.Equal(NominatimGeocoder.UserAgent, request.UserAgent);
        Assert.StartsWith(Brand.Slug + "/", request.UserAgent, StringComparison.Ordinal);
        Assert.Contains(Brand.SourceUrl, request.UserAgent, StringComparison.Ordinal);
        Assert.Equal("en-GB", request.Language);

        // Unreadable, off-the-globe and unnamed places are left out.
        Assert.Equal(
            [
                new GeocodeResult("Boulder, Boulder County, Colorado, United States", 40.0149856, -105.2705456),
                new GeocodeResult("Boulder, Wisconsin", 42.9, -87.8),
                new GeocodeResult("Numbers, not strings", 39.7, -105.0),
            ],
            results
        );
    }

    [Fact]
    public async Task ARepeatedQueryIsAnsweredFromTheCache()
    {
        var geocoder = Create();

        var first = await geocoder.GeocodeAsync("Boulder CO", null, Ct);
        _time.Advance(TimeSpan.FromHours(1));
        var again = await geocoder.GeocodeAsync("  boulder   co ", null, Ct);

        Assert.Same(first, again);
        var request = Assert.Single(_handler.Requests);
        Assert.Null(request.Language);

        // Another language is another query: its place names differ.
        await geocoder.GeocodeAsync("Boulder CO", "de", Ct);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ABlankQuerySendsNothing()
    {
        Assert.Empty(await Create().GeocodeAsync(" \t ", "en", Ct));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task LookupsAreSentAtLeastASecondApart()
    {
        var geocoder = Create();
        await geocoder.GeocodeAsync("Boulder", null, Ct);
        _time.Advance(TimeSpan.FromMilliseconds(200));

        var second = geocoder.GeocodeAsync("Denver", null, Ct);
        _time.Advance(TimeSpan.FromMilliseconds(790));
        Assert.False(second.IsCompleted);
        Assert.Single(_handler.Requests);

        _time.Advance(TimeSpan.FromMilliseconds(10));
        await second;

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), _handler.Requests[1].At - _handler.Requests[0].At);
    }

    [Fact]
    public async Task ANewerLookupReplacesOneThatIsWaiting()
    {
        var geocoder = Create();
        await geocoder.GeocodeAsync("Boulder", null, Ct);

        var waiting = geocoder.GeocodeAsync("Denver", null, Ct);
        var newer = geocoder.GeocodeAsync("Golden", null, Ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        Assert.Equal(NominatimGeocoder.SupersededMessage, ex.Message);
        _time.Advance(TimeSpan.FromSeconds(1));
        await newer;
        Assert.Equal(["Boulder", "Golden"], _handler.Requests.Select(r => r.Query));
    }

    [Fact]
    public async Task ACancelledWaitLetsTheNextLookupWait()
    {
        var geocoder = Create();
        await geocoder.GeocodeAsync("Boulder", null, Ct);
        using var cts = new CancellationTokenSource();

        var cancelled = geocoder.GeocodeAsync("Denver", null, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var next = geocoder.GeocodeAsync("Golden", null, Ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        await next;
        Assert.Equal(["Boulder", "Golden"], _handler.Requests.Select(r => r.Query));
    }

    [Fact]
    public async Task ACachedAnswerDoesNotDisplaceAWaitingLookup()
    {
        var geocoder = Create();
        await geocoder.GeocodeAsync("Boulder", null, Ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        await geocoder.GeocodeAsync("Denver", null, Ct);

        // A cached answer doesn't wait, and doesn't replace the lookup that is waiting.
        var waiting = geocoder.GeocodeAsync("Golden", null, Ct);
        var cached = await geocoder.GeocodeAsync("Boulder", null, Ct);

        Assert.NotEmpty(cached);
        _time.Advance(TimeSpan.FromSeconds(1));
        await waiting;
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusalPausesLookupsForAMinute(HttpStatusCode status)
    {
        var geocoder = Create();
        _handler.Respond = _ => Json("{}", status);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            geocoder.GeocodeAsync("Boulder", null, Ct)
        );
        Assert.Equal(NominatimGeocoder.BlockedMessage, refused.Message);

        _handler.Respond = _ => Json(Boulder);
        _time.Advance(TimeSpan.FromSeconds(59));
        var paused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            geocoder.GeocodeAsync("Denver", null, Ct)
        );
        Assert.Equal(NominatimGeocoder.BlockedMessage, paused.Message);
        Assert.Single(_handler.Requests);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEmpty(await geocoder.GeocodeAsync("Denver", null, Ct));
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Contains(
            _logger.Entries,
            e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains(
                    ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal
                )
        );
    }

    public static TheoryData<string, Func<HttpRequestMessage, HttpResponseMessage>> Failures() =>
        new()
        {
            { "returned an error (500)", _ => Json("oops", HttpStatusCode.InternalServerError) },
            { NominatimGeocoder.UnreadableMessage, _ => Json("{ not json") },
            { NominatimGeocoder.UnreachableMessage, _ => throw new HttpRequestException("no route") },
            { NominatimGeocoder.UnreachableMessage, _ => throw new TaskCanceledException("timed out") },
        };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task FailuresAreReportedForTheUserAndNotCached(
        string message,
        Func<HttpRequestMessage, HttpResponseMessage> respond
    )
    {
        var geocoder = Create();
        _handler.Respond = respond;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => geocoder.GeocodeAsync("Boulder", null, Ct));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);

        _handler.Respond = _ => Json(Boulder);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEmpty(await geocoder.GeocodeAsync("Boulder", null, Ct));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task QueriesAndResultsAreNeverLoggedAboveDebug()
    {
        var geocoder = Create();
        await geocoder.GeocodeAsync("Secret Street 1", null, Ct);
        _handler.Respond = _ => Json("oops", HttpStatusCode.BadGateway);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => geocoder.GeocodeAsync("Hidden Road", null, Ct));

        Assert.NotEmpty(_logger.Entries);
        Assert.All(
            _logger.Entries,
            e =>
            {
                Assert.DoesNotContain("Secret", e.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Hidden", e.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Boulder", e.Message, StringComparison.OrdinalIgnoreCase);
            }
        );
    }

    [Fact]
    public async Task TheCacheKeepsTheMostRecentQueries()
    {
        var geocoder = Create();
        for (var i = 0; i <= NominatimGeocoder.CacheCapacity; i++)
        {
            await geocoder.GeocodeAsync($"Place {i}", null, Ct);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await geocoder.GeocodeAsync($"Place {NominatimGeocoder.CacheCapacity}", null, Ct);
        Assert.Equal(NominatimGeocoder.CacheCapacity + 1, _handler.Requests.Count);
        await geocoder.GeocodeAsync("Place 0", null, Ct); // the oldest was dropped
        Assert.Equal(NominatimGeocoder.CacheCapacity + 2, _handler.Requests.Count);
    }

    [Fact]
    public void ThePublicServerLinksItsUsagePolicy() =>
        Assert.Equal(
            new GeocodingInfo(true, "nominatim.openstreetmap.org", GeocodingOptions.PublicNominatimPolicy),
            Create().Info
        );

    [Fact]
    public async Task ASelfHostedServerIsUsedAndLinksOnlyItsOwnPolicy()
    {
        var geocoder = Create(new GeocodingOptions { Endpoint = "http://geo.example:8080/nominatim" });
        Assert.Equal(new GeocodingInfo(true, "geo.example", null), geocoder.Info);

        await geocoder.GeocodeAsync("Boulder", null, Ct);
        Assert.StartsWith(
            "http://geo.example:8080/nominatim/search?q=Boulder&",
            Assert.Single(_handler.Requests).Uri,
            StringComparison.Ordinal
        );

        var withPolicy = Create(
            new GeocodingOptions { Endpoint = "https://geo.example/", UsagePolicyUrl = " https://geo.example/terms " }
        );
        Assert.Equal(new GeocodingInfo(true, "geo.example", "https://geo.example/terms"), withPolicy.Info);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ftp://geo.example")]
    [InlineData("not a url")]
    public async Task AnEmptyOrUnusableEndpointTurnsLookupOff(string? endpoint)
    {
        var geocoder = Create(new GeocodingOptions { Endpoint = endpoint });

        Assert.Equal(GeocodingInfo.Disabled, geocoder.Info);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => geocoder.GeocodeAsync("Boulder", null, Ct));
        Assert.Equal(NominatimGeocoder.DisabledMessage, ex.Message);
        Assert.Empty(_handler.Requests);
    }

    internal sealed record SentRequest(
        string Uri,
        string Query,
        string? UserAgent,
        string? Language,
        DateTimeOffset At
    );

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<SentRequest> _requests = new();

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.OK);

        public TimeProvider Time { get; set; } = TimeProvider.System;

        public List<SentRequest> Requests => [.. _requests];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var uri = request.RequestUri!;
            var query = Uri.UnescapeDataString(uri.Query.Split('&')[0]["?q=".Length..]);
            _requests.Enqueue(
                new SentRequest(
                    uri.AbsoluteUri,
                    query,
                    request.Headers.TryGetValues("User-Agent", out var ua) ? string.Join(' ', ua) : null,
                    request.Headers.TryGetValues("Accept-Language", out var lang) ? string.Join(',', lang) : null,
                    Time.GetUtcNow()
                )
            );
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class ListLogger : ILogger<NominatimGeocoder>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
