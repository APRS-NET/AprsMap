// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AprsMap.Tiles;

/// <summary>How a tile request was answered.</summary>
public enum TileOutcome
{
    /// <summary>Fresh on disk; no network.</summary>
    Hit,

    /// <summary>Expired, but within the server's stale-while-revalidate window: served now, revalidated behind.</summary>
    StaleRevalidating,

    /// <summary>Expired; the server said 304 Not Modified, so the copy was kept and its expiry renewed.</summary>
    Revalidated,

    /// <summary>Expired; the server sent a new tile.</summary>
    Replaced,

    /// <summary>Not on disk; downloaded.</summary>
    Miss,

    /// <summary>Expired, the network failed, and the server's stale-if-error window allows the old copy.</summary>
    Stale,

    /// <summary>No tile: not on disk (or too old to serve) and the network failed.</summary>
    Failed,
}

/// <summary>A tile, and when it stops being fresh.</summary>
/// <param name="Data">The image, or null when <paramref name="Outcome"/> is <see cref="TileOutcome.Failed"/>.</param>
/// <param name="Outcome">How it was answered.</param>
/// <param name="Expires">When it stops being fresh.</param>
/// <param name="ETag">The server's entity tag, for the browser's own revalidation.</param>
public sealed record TileResult(byte[]? Data, TileOutcome Outcome, DateTimeOffset Expires, string? ETag);

/// <summary>
/// A disk cache in front of one tile server that follows the OpenStreetMap tile usage policy
/// (https://operations.osmfoundation.org/policies/tiles/), as designed in spike 4 (docs/spikes/tile-cache.md):
/// <list type="bullet">
/// <item>Cache-Control <c>max-age</c> minus <c>Age</c>, else Expires, else 7 days; never <c>no-cache</c> requests;</item>
/// <item>expired tiles revalidated with If-None-Match and If-Modified-Since;</item>
/// <item>stale tiles served only within the server's <c>stale-while-revalidate</c> and <c>stale-if-error</c> windows;</item>
/// <item>at most a few requests in flight, one download for concurrent requests of the same tile, the caller's
/// Referer passed through, and only tiles callers ask for (no prefetching);</item>
/// <item>a size cap with least-recently-used purging.</item>
/// </list>
/// The User-Agent is the <see cref="HttpClient"/>'s, set by <see cref="TilesServiceCollectionExtensions"/> from branding.
/// </summary>
internal sealed partial class TileCache : IDisposable
{
    private readonly TileProviderOptions _provider;
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly TimeSpan _defaultLifetime;
    private readonly Func<HttpClient> _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _upstream;
    private readonly ConcurrentDictionary<string, Lazy<Task<TileResult>>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _revalidating = new(StringComparer.Ordinal);
    private readonly Lock _indexGate = new();
    private Dictionary<string, (long Size, DateTimeOffset Used)>? _index;
    private long _bytes;

    public TileCache(
        TileProviderOptions provider,
        string root,
        long maxBytes,
        int maxConcurrentRequests,
        TimeSpan defaultLifetime,
        Func<HttpClient> http,
        TimeProvider time,
        ILogger logger
    )
    {
        _provider = provider;
        _root = root;
        _maxBytes = maxBytes;
        _defaultLifetime = defaultLifetime;
        _http = http;
        _time = time;
        _logger = logger;
        _upstream = new SemaphoreSlim(maxConcurrentRequests);
    }

    /// <summary>Gets a tile. <paramref name="referer"/> goes upstream unchanged (the policy forbids stripping it).</summary>
    public Task<TileResult> GetAsync(int z, int x, int y, string? referer, CancellationToken cancellationToken)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{z}/{x}/{y}");
        // One download per tile however many ask; a caller giving up doesn't cancel it for the others.
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<TileResult>>(() => LoadAsync(z, x, y, referer)));
        return AwaitAsync(key, lazy, cancellationToken);
    }

    public void Dispose() => _upstream.Dispose();

    private async Task<TileResult> AwaitAsync(
        string key,
        Lazy<Task<TileResult>> lazy,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lazy.Value.IsCompleted)
            {
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<TileResult>>>(key, lazy));
            }
        }
    }

    private async Task<TileResult> LoadAsync(int z, int x, int y, string? referer)
    {
        var (path, metaPath) = Paths(z, x, y);
        var meta = await ReadMetaAsync(path, metaPath).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (meta is not null)
        {
            if (meta.Expires > now)
            {
                return await FromDiskAsync(path, TileOutcome.Hit, meta).ConfigureAwait(false);
            }
            if (meta.StaleWhileRevalidateUntil > now)
            {
                StartRevalidation(z, x, y, referer);
                return await FromDiskAsync(path, TileOutcome.StaleRevalidating, meta).ConfigureAwait(false);
            }
        }
        return await FetchAsync(z, x, y, referer, path, metaPath, meta).ConfigureAwait(false);
    }

    private void StartRevalidation(int z, int x, int y, string? referer)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{z}/{x}/{y}");
        _revalidating.GetOrAdd(
            key,
            _ =>
                Task.Run(async () =>
                {
                    try
                    {
                        var (path, metaPath) = Paths(z, x, y);
                        var meta = await ReadMetaAsync(path, metaPath).ConfigureAwait(false);
                        await FetchAsync(z, x, y, referer, path, metaPath, meta).ConfigureAwait(false);
                    }
                    finally
                    {
                        _revalidating.TryRemove(key, out Task? _);
                    }
                })
        );
    }

    private async Task<TileResult> FetchAsync(
        int z,
        int x,
        int y,
        string? referer,
        string path,
        string metaPath,
        TileMeta? meta
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _provider.TileUri(z, x, y));
        if (referer is not null && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            request.Headers.Referrer = refererUri;
        }
        if (meta?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }
        if (meta?.LastModified is { } lastModified)
        {
            request.Headers.IfModifiedSince = lastModified;
        }

        await _upstream.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await _http().SendAsync(request).ConfigureAwait(false);
            var fresh = Freshness(response, meta);
            if (response.StatusCode == HttpStatusCode.NotModified && meta is not null)
            {
                await WriteMetaAsync(metaPath, fresh).ConfigureAwait(false);
                return await FromDiskAsync(path, TileOutcome.Revalidated, fresh).ConfigureAwait(false);
            }

            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, data).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            await WriteMetaAsync(metaPath, fresh).ConfigureAwait(false);
            Track(path, data.Length);
            return new TileResult(
                data,
                meta is null ? TileOutcome.Miss : TileOutcome.Replaced,
                fresh.Expires,
                fresh.ETag
            );
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            LogFetchFailed(_provider.Name, z, x, y, ex);
            if (meta is not null && meta.StaleIfErrorUntil > _time.GetUtcNow())
            {
                return await FromDiskAsync(path, TileOutcome.Stale, meta).ConfigureAwait(false);
            }
            return new TileResult(null, TileOutcome.Failed, _time.GetUtcNow(), null);
        }
        finally
        {
            _upstream.Release();
        }
    }

    /// <summary>The new expiry and stale windows from the response's headers (or the policy's 7 days).</summary>
    private TileMeta Freshness(HttpResponseMessage response, TileMeta? previous)
    {
        var now = _time.GetUtcNow();
        var cacheControl = response.Headers.CacheControl;
        var age = response.Headers.Age ?? TimeSpan.Zero;
        var expires = cacheControl?.MaxAge is { } maxAge
            ? now + maxAge - age
            : response.Content.Headers.Expires ?? now + _defaultLifetime;
        var etag = response.Headers.ETag?.ToString() ?? previous?.ETag;
        var lastModified = response.Content.Headers.LastModified ?? previous?.LastModified;
        return new TileMeta(
            expires,
            etag,
            lastModified,
            expires + Extension(cacheControl, "stale-while-revalidate"),
            expires + Extension(cacheControl, "stale-if-error")
        );
    }

    private static TimeSpan Extension(CacheControlHeaderValue? cacheControl, string name) =>
        cacheControl?.Extensions.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            is { Value: { } value }
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

    private (string Path, string MetaPath) Paths(int z, int x, int y)
    {
        var extension = _provider.ImageType switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };
        var path = Path.Combine(
            _root,
            z.ToString(CultureInfo.InvariantCulture),
            x.ToString(CultureInfo.InvariantCulture),
            y.ToString(CultureInfo.InvariantCulture) + extension
        );
        return (path, path + ".json");
    }

    private async Task<TileResult> FromDiskAsync(string path, TileOutcome outcome, TileMeta meta)
    {
        var data = await ReadSharedAsync(path).ConfigureAwait(false);
        Touch(path);
        return new TileResult(data, outcome, meta.Expires, meta.ETag);
    }

    /// <summary>
    /// Reads a tile while a refresh may be replacing it. On Windows a replace and a read of the same file can collide
    /// for a moment, so the read lets the file be replaced under it and tries again a few times on a sharing violation.
    /// </summary>
    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    useAsync: true
                );
                var data = new byte[stream.Length];
                await stream.ReadExactlyAsync(data).ConfigureAwait(false);
                return data;
            }
            catch (IOException ex) when (attempt < 5 && ex is not FileNotFoundException)
            {
                await Task.Yield();
            }
        }
    }

    private static async Task<TileMeta?> ReadMetaAsync(string path, string metaPath)
    {
        if (!File.Exists(path) || !File.Exists(metaPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(metaPath).ConfigureAwait(false),
                TileMetaContext.Default.TileMeta
            );
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null; // as if not cached: fetched again
        }
    }

    private static Task WriteMetaAsync(string metaPath, TileMeta meta) =>
        File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, TileMetaContext.Default.TileMeta));

    // The size index: built from the folder on first use, then kept up to date. Used time is in memory only (the file's
    // write time to start with), which is enough to purge the tiles nobody has looked at for longest.
    private void Touch(string path)
    {
        lock (_indexGate)
        {
            var index = Index();
            if (index.TryGetValue(path, out var entry))
            {
                index[path] = (entry.Size, _time.GetUtcNow());
            }
        }
    }

    private void Track(string path, long size)
    {
        lock (_indexGate)
        {
            var index = Index();
            if (index.TryGetValue(path, out var old))
            {
                _bytes -= old.Size;
            }
            index[path] = (size, _time.GetUtcNow());
            _bytes += size;
            if (_bytes > _maxBytes)
            {
                Purge(index);
            }
        }
    }

    /// <summary>Deletes the least recently used tiles until the cache is down to 90 % of its cap.</summary>
    private void Purge(Dictionary<string, (long Size, DateTimeOffset Used)> index)
    {
        var target = _maxBytes * 9 / 10;
        foreach (var (path, entry) in index.OrderBy(e => e.Value.Used).ToList())
        {
            if (_bytes <= target)
            {
                break;
            }
            try
            {
                File.Delete(path);
                File.Delete(path + ".json");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // in use: try the next one
            }
            index.Remove(path);
            _bytes -= entry.Size;
        }
        LogPurged(_provider.Name, _bytes);
    }

    private Dictionary<string, (long Size, DateTimeOffset Used)> Index()
    {
        if (_index is not null)
        {
            return _index;
        }
        _index = new Dictionary<string, (long, DateTimeOffset)>(StringComparer.Ordinal);
        _bytes = 0;
        if (Directory.Exists(_root))
        {
            foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Extension is ".json" or ".tmp")
                {
                    continue;
                }
                _index[file.FullName] = (file.Length, file.LastWriteTimeUtc);
                _bytes += file.Length;
            }
        }
        return _index;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tile {Provider} {Z}/{X}/{Y} could not be fetched")]
    private partial void LogFetchFailed(string provider, int z, int x, int y, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tile cache {Provider} purged to {Bytes} bytes")]
    private partial void LogPurged(string provider, long bytes);
}

/// <summary>What the cache keeps next to each tile.</summary>
internal sealed record TileMeta(
    DateTimeOffset Expires,
    string? ETag,
    DateTimeOffset? LastModified,
    DateTimeOffset StaleWhileRevalidateUntil,
    DateTimeOffset StaleIfErrorUntil
);

[JsonSerializable(typeof(TileMeta))]
internal sealed partial class TileMetaContext : JsonSerializerContext;
