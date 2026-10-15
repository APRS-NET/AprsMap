// SPDX-License-Identifier: MIT
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AprsMap.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AprsMap.Core;

/// <summary>Address lookup settings, bound from the <c>Geocoding</c> configuration section.</summary>
public sealed class GeocodingOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Geocoding";

    /// <summary>The OpenStreetMap Foundation's public Nominatim server, the default.</summary>
    public const string PublicNominatim = "https://nominatim.openstreetmap.org";

    /// <summary>The public Nominatim server's usage policy.</summary>
    public const string PublicNominatimPolicy = "https://operations.osmfoundation.org/policies/nominatim/";

    /// <summary>
    /// The Nominatim server's base URL, for example a self-hosted one. Empty turns address lookup off.
    /// </summary>
    public string? Endpoint { get; set; } = PublicNominatim;

    /// <summary>
    /// The server's usage policy, linked before the first lookup. When empty, the public server's policy is used for
    /// the public server and no link is shown for others.
    /// </summary>
    public string? UsagePolicyUrl { get; set; }

    /// <summary>How long to wait for the server before giving up on a lookup.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Turns an address or postal code into coordinates. The engine's <see cref="IEngineService.GeocodeAsync"/>.</summary>
public interface IGeocoder
{
    /// <summary>Whether lookup is on, and where queries go.</summary>
    GeocodingInfo Info { get; }

    /// <inheritdoc cref="IEngineService.GeocodeAsync"/>
    Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(
        string query,
        string? language,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Looks addresses up with Nominatim, following its usage policy (docs/features/F0.5-first-run-wizard.md, behaviors
/// 13–22): one request at a time per second at most, a waiting lookup replaced by a newer one, the app's own
/// User-Agent, results cached in memory for the life of the process, and a minute's pause after the server refuses.
/// </summary>
public sealed partial class NominatimGeocoder : IGeocoder
{
    /// <summary>The <see cref="IHttpClientFactory"/> client name.</summary>
    public const string HttpClientName = nameof(NominatimGeocoder);

    /// <summary>The shortest time between two requests (the policy's "maximum of 1 request per second").</summary>
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long to stop sending after the server answers 429 or 403.</summary>
    internal static readonly TimeSpan BackOff = TimeSpan.FromSeconds(60);

    /// <summary>The most results a lookup asks for.</summary>
    internal const int MaxResults = 5;

    /// <summary>The most queries kept in the cache; the oldest go first.</summary>
    internal const int CacheCapacity = 1000;

    internal const string DisabledMessage = "Address lookup is turned off.";
    internal const string BlockedMessage =
        "The address lookup server is refusing requests for now. Try again in a minute, or use another way.";
    internal const string SupersededMessage = "A newer lookup replaced this one.";
    internal const string UnreachableMessage =
        "Couldn't reach the address lookup server. Check the connection, or use another way.";
    internal const string UnreadableMessage = "The address lookup server sent a reply that couldn't be read.";

    private static readonly char[] Whitespace = [' ', '\t', '\r', '\n', ' '];

    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly ILogger<NominatimGeocoder> _logger;
    private readonly Uri? _endpoint;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IReadOnlyList<GeocodeResult>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private CancellationTokenSource? _waiter;

    /// <summary>Creates the geocoder.</summary>
    public NominatimGeocoder(
        IHttpClientFactory clients,
        IOptions<GeocodingOptions> options,
        TimeProvider time,
        ILogger<NominatimGeocoder> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _clients = clients;
        _time = time;
        _logger = logger;

        var endpoint = options.Value.Endpoint?.Trim();
        if (string.IsNullOrEmpty(endpoint))
        {
            Info = GeocodingInfo.Disabled;
            return;
        }
        if (
            !Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        )
        {
            LogEndpointUnusable(endpoint);
            Info = GeocodingInfo.Disabled;
            return;
        }

        _endpoint = uri;
        var policy = string.IsNullOrWhiteSpace(options.Value.UsagePolicyUrl)
            ? (
                uri.Host == new Uri(GeocodingOptions.PublicNominatim).Host
                    ? GeocodingOptions.PublicNominatimPolicy
                    : null
            )
            : options.Value.UsagePolicyUrl.Trim();
        Info = new GeocodingInfo(true, uri.Host, policy);
    }

    /// <inheritdoc />
    public GeocodingInfo Info { get; }

    /// <summary>The User-Agent every request carries: the product, its version and where its source is.</summary>
    internal static string UserAgent =>
        string.IsNullOrEmpty(Brand.SourceUrl)
            ? $"{Brand.Slug}/{Brand.Version}"
            : $"{Brand.Slug}/{Brand.Version} (+{Brand.SourceUrl})";

    /// <inheritdoc />
    public async Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(
        string query,
        string? language,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_endpoint is null)
        {
            throw Fail(EngineErrorCodes.GeocodingDisabled, DisabledMessage);
        }

        var text = string.Join(' ', query.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return [];
        }
        language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        var key = text.ToUpperInvariant() + "\n" + language?.ToUpperInvariant();

        CancellationTokenSource? waiter = null;
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (_gate)
                {
                    if (waiter is not null && !ReferenceEquals(_waiter, waiter))
                    {
                        throw Fail(EngineErrorCodes.GeocodingSuperseded, SupersededMessage);
                    }
                    if (_cache.TryGetValue(key, out var cached))
                    {
                        return cached;
                    }

                    var now = _time.GetUtcNow();
                    if (now < _blockedUntil)
                    {
                        throw Fail(EngineErrorCodes.GeocodingRefused, BlockedMessage);
                    }

                    wait = _nextAllowed - now;
                    if (wait <= TimeSpan.Zero)
                    {
                        _nextAllowed = now + MinInterval;
                        break;
                    }
                    if (waiter is null)
                    {
                        // Only one lookup waits; a newer one takes its place.
                        _waiter?.Cancel();
                        _waiter = waiter = new CancellationTokenSource();
                    }
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, waiter.Token);
                try
                {
                    await Task.Delay(wait, _time, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw Fail(EngineErrorCodes.GeocodingSuperseded, SupersededMessage);
                }
            }
        }
        finally
        {
            if (waiter is not null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_waiter, waiter))
                    {
                        _waiter = null;
                    }
                }
                // Safe: a newer lookup only cancels the waiter while it is still _waiter, under the lock.
                waiter.Dispose();
            }
        }

        return await SendAsync(text, key, language, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GeocodeResult>> SendAsync(
        string text,
        string key,
        string? language,
        CancellationToken cancellationToken
    )
    {
        var uri = new Uri(
            _endpoint!,
            $"search?q={Uri.EscapeDataString(text)}&format=jsonv2&limit={MaxResults.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (language is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", language);
        }

        var client = _clients.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(ex);
            throw Fail(EngineErrorCodes.GeocodingUnreachable, UnreachableMessage, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(ex); // the client's timeout
            throw Fail(EngineErrorCodes.GeocodingUnreachable, UnreachableMessage, ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                lock (_gate)
                {
                    _blockedUntil = _time.GetUtcNow() + BackOff;
                }
                LogRefused((int)response.StatusCode, BackOff);
                throw Fail(EngineErrorCodes.GeocodingRefused, BlockedMessage);
            }
            if (!response.IsSuccessStatusCode)
            {
                LogHttpError((int)response.StatusCode);
                throw new EngineOperationException(
                    new EngineError(
                        EngineErrorCodes.GeocodingHttpError,
                        $"The address lookup server returned an error ({(int)response.StatusCode}). Try again later, or use another way.",
                        new Dictionary<string, string>
                        {
                            [EngineErrorArgs.Status] = ((int)response.StatusCode).ToString(
                                System.Globalization.CultureInfo.InvariantCulture
                            ),
                        }
                    )
                );
            }

            List<NominatimPlace>? places;
            try
            {
                places = await response
                    .Content.ReadFromJsonAsync(NominatimJsonContext.Default.ListNominatimPlace, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                LogUnreadable(ex);
                throw Fail(EngineErrorCodes.GeocodingUnreadable, UnreadableMessage, ex);
            }

            GeocodeResult[] results =
            [
                .. (places ?? []).Select(p => p.ToResult()).OfType<GeocodeResult>().Take(MaxResults),
            ];
            lock (_gate)
            {
                if (_cache.TryAdd(key, results))
                {
                    _cacheOrder.Enqueue(key);
                    while (_cacheOrder.Count > CacheCapacity)
                    {
                        _cache.Remove(_cacheOrder.Dequeue());
                    }
                }
            }
            LogResults(results.Length);
            return results;
        }
    }

    internal static EngineOperationException Fail(string code, string message, Exception? inner = null) =>
        new(new EngineError(code, message), inner);

    // Queries and results are never logged above Debug (behavior 22); these messages carry neither.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Address lookup returned {Count} results")]
    private partial void LogResults(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Address lookup server unreachable")]
    private partial void LogUnreachable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Address lookup server returned HTTP {Status}")]
    private partial void LogHttpError(int status);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Address lookup server refused a request (HTTP {Status}); pausing lookups for {BackOff}"
    )]
    private partial void LogRefused(int status, TimeSpan backOff);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Address lookup server sent an unreadable reply")]
    private partial void LogUnreadable(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Geocoding:Endpoint '{Endpoint}' is not an http or https URL; address lookup is off"
    )]
    private partial void LogEndpointUnusable(string endpoint);
}

/// <summary>Address lookup when none is configured.</summary>
internal sealed class NoGeocoder : IGeocoder
{
    public static NoGeocoder Instance { get; } = new();

    public GeocodingInfo Info => GeocodingInfo.Disabled;

    public Task<IReadOnlyList<GeocodeResult>> GeocodeAsync(
        string query,
        string? language,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromException<IReadOnlyList<GeocodeResult>>(
            NominatimGeocoder.Fail(EngineErrorCodes.GeocodingDisabled, NominatimGeocoder.DisabledMessage)
        );
}

/// <summary>
/// The fields of a Nominatim <c>jsonv2</c> search result that lookups use. Coordinates arrive as strings; they are read
/// loosely so that one odd entry drops only itself, not the whole reply.
/// </summary>
internal sealed record NominatimPlace(
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("lat")] JsonElement Lat,
    [property: JsonPropertyName("lon")] JsonElement Lon
)
{
    /// <summary>The place as a result, or null when it has no name or no coordinates on the globe.</summary>
    public GeocodeResult? ToResult() =>
        !string.IsNullOrWhiteSpace(DisplayName)
        && Degrees(Lat) is { } latitude and >= -90 and <= 90
        && Degrees(Lon) is { } longitude and >= -180 and <= 180
            ? new GeocodeResult(DisplayName, latitude, longitude)
            : null;

    private static double? Degrees(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String
                when double.TryParse(
                    value.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var degrees
                ) => degrees,
            _ => null,
        };
}

[ExcludeFromCodeCoverage] // generated code
[JsonSerializable(typeof(List<NominatimPlace>))]
internal sealed partial class NominatimJsonContext : JsonSerializerContext;
