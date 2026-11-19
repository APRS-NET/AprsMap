// SPDX-License-Identifier: MIT
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZedMap.Contracts;

namespace ZedMap.Core;

/// <summary>A release as GitHub's releases API lists it (the fields the update check reads).</summary>
internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets
);

/// <summary>A file attached to a release.</summary>
internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long Size
);

[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class GitHubJson : JsonSerializerContext;

/// <summary>
/// The update check (docs/features/F4.15-updates.md, behaviors 2–4): one anonymous request to the GitHub releases API
/// for the repository in <see cref="Brand.SourceUrl"/>, with the product and version as the User-Agent and the last
/// answer's ETag, so an unchanged list costs nothing. No callsign, location, setting or identifier is sent.
/// </summary>
internal sealed class UpdateChecker(HttpClient http, UpdateOptions options)
{
    private string? _etag;
    private List<GitHubRelease>? _releases;

    /// <summary>The repository, as <c>owner/name</c>, from the source URL.</summary>
    internal static string? Repository(string sourceUrl) =>
        Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) && uri.Host == "github.com"
            ? string.Join('/', uri.AbsolutePath.Trim('/').Split('/').Take(2)) is { } path
            && path.Count(c => c == '/') == 1
                ? path
                : null
            : null;

    /// <summary>The release list, or why it couldn't be had; a 304 keeps the last answer.</summary>
    public async Task<(IReadOnlyList<GitHubRelease>? Releases, EngineError? Error)> FetchAsync(
        CancellationToken cancellationToken
    )
    {
        if (Repository(Brand.SourceUrl) is not { } repository)
        {
            return (
                null,
                new EngineError(
                    EngineErrorCodes.UpdatesUnexpectedAnswer,
                    "The source address isn't a GitHub repository."
                )
            );
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(options.ApiBase, $"repos/{repository}/releases?per_page=20")
        );
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(Brand.Slug, Brand.Version.Split('+')[0]));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (_etag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && _releases is not null)
            {
                return (_releases, null);
            }
            if (
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && response.Headers.TryGetValues("x-ratelimit-remaining", out var left)
                && left.FirstOrDefault() == "0"
            )
            {
                return (
                    null,
                    new EngineError(
                        EngineErrorCodes.UpdatesRateLimited,
                        "GitHub's limit for anonymous requests was reached."
                    )
                );
            }
            if (!response.IsSuccessStatusCode)
            {
                return (
                    null,
                    new EngineError(
                        EngineErrorCodes.UpdatesUnreachable,
                        $"GitHub answered {(int)response.StatusCode}.",
                        new Dictionary<string, string>
                        {
                            [EngineErrorArgs.Status] = ((int)response.StatusCode).ToString(
                                System.Globalization.CultureInfo.InvariantCulture
                            ),
                        }
                    )
                );
            }
            var releases = await response
                .Content.ReadFromJsonAsync(GitHubJson.Default.ListGitHubRelease, cancellationToken)
                .ConfigureAwait(false);
            if (releases is null)
            {
                return (null, new EngineError(EngineErrorCodes.UpdatesUnexpectedAnswer, "GitHub's answer was empty."));
            }
            _releases = releases;
            _etag = response.Headers.ETag?.ToString();
            return (releases, null);
        }
        catch (JsonException)
        {
            return (
                null,
                new EngineError(EngineErrorCodes.UpdatesUnexpectedAnswer, "GitHub's answer couldn't be read.")
            );
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (
                null,
                new EngineError(EngineErrorCodes.UpdatesUnreachable, "GitHub couldn't be reached: " + ex.Message)
            );
        }
    }

    /// <summary>
    /// The newest release that counts (behavior 3): published, tagged <c>v</c> and a SemVer version, newer than
    /// <paramref name="running"/>, on <paramref name="channel"/>, not <paramref name="skipped"/>, and ready for this
    /// install (behavior 4).
    /// </summary>
    public static UpdateInfo? Pick(
        IEnumerable<GitHubRelease> releases,
        string running,
        UpdateChannel channel,
        string? skipped,
        UpdateOptions options
    )
    {
        if (!SemVersion.TryParse(running, out var current))
        {
            return null;
        }
        SemVersion? skip = SemVersion.TryParse(skipped, out var s) ? s : null;
        var candidates = releases
            .Where(r => !r.Draft && r.TagName is { } tag && tag.StartsWith('v') && r.HtmlUrl is not null)
            .Select(r => (Release: r, Ok: SemVersion.TryParse(r.TagName, out var v), Version: v))
            .Where(c => c.Ok && c.Version.CompareTo(current) > 0)
            .Where(c => channel == UpdateChannel.PreReleases || !c.Version.IsPreRelease)
            .Where(c => skip is null || c.Version.CompareTo(skip) > 0)
            .OrderByDescending(c => c.Version);
        foreach (var (release, _, version) in candidates)
        {
            if (Ready(release, version, options) is { } files)
            {
                return new UpdateInfo(
                    version.ToString(),
                    release.PublishedAt ?? DateTimeOffset.MinValue,
                    new Uri(release.HtmlUrl!),
                    release.Body ?? string.Empty,
                    options.Kind == InstallKind.Gateway ? $"{ImageName()}:{version}" : null,
                    files
                );
            }
        }
        return null;
    }

    /// <summary>The most What's new lists (F4.15 behavior 11c): an install many versions behind sees the latest.</summary>
    public const int WhatsNewMost = 10;

    /// <summary>
    /// What's new (F4.15 behavior 11c): the published releases after <paramref name="last"/> up to
    /// <paramref name="running"/>, newest first; empty when <paramref name="last"/> isn't older than it.
    /// </summary>
    public static IReadOnlyList<UpdateInfo> Since(IEnumerable<GitHubRelease> releases, string? last, string running)
    {
        if (
            !SemVersion.TryParse(last, out var from)
            || !SemVersion.TryParse(running, out var to)
            || from.CompareTo(to) >= 0
        )
        {
            return [];
        }
        return
        [
            .. releases
                .Where(r => !r.Draft && r.TagName is { } tag && tag.StartsWith('v') && r.HtmlUrl is not null)
                .Select(r => (Release: r, Ok: SemVersion.TryParse(r.TagName, out var v), Version: v))
                .Where(c => c.Ok && c.Version.CompareTo(from) > 0 && c.Version.CompareTo(to) <= 0)
                .OrderByDescending(c => c.Version)
                .Take(WhatsNewMost)
                .Select(c => new UpdateInfo(
                    c.Version.ToString(),
                    c.Release.PublishedAt ?? DateTimeOffset.MinValue,
                    new Uri(c.Release.HtmlUrl!),
                    c.Release.Body ?? string.Empty,
                    null,
                    []
                )),
        ];
    }

    /// <summary>The container image, from the repository's owner and the product: <c>ghcr.io/owner/slug</c>.</summary>
    private static string ImageName() =>
        $"ghcr.io/{(Repository(Brand.SourceUrl) ?? "unknown/unknown").Split('/')[0].ToLowerInvariant()}/{Brand.Slug}";

    /// <summary>
    /// This install's files, when they're attached (behavior 4): the archive for its runtime, its installer feed, or the
    /// image marker; null while the release workflow is still uploading.
    /// </summary>
    /// <remarks>
    /// Files are named by the slug; during a rename they may use the next one (docs/branding.md), which this install
    /// takes when the release has none under its own.
    /// </remarks>
    private static List<UpdateFile>? Ready(GitHubRelease release, SemVersion version, UpdateOptions options) =>
        Brand.ReleaseSlugs.Select(slug => Ready(release, version, options, slug)).FirstOrDefault(f => f is not null);

    private static List<UpdateFile>? Ready(
        GitHubRelease release,
        SemVersion version,
        UpdateOptions options,
        string slug
    )
    {
        var assets = (release.Assets ?? [])
            .Where(a => a.Name is not null && Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out _))
            .ToList();
        var prefix = $"{slug}-v{version}";
        List<GitHubAsset> wanted = options.Kind switch
        {
            InstallKind.Gateway => [.. assets.Where(a => a.Name == $"{prefix}-image.txt")],
            InstallKind.Installed => [.. assets.Where(a => a.Name == $"releases.{options.RuntimeId}.json")],
            // The tarball and its checksum, both or neither (F1.40 behavior 3).
            InstallKind.Service => assets
                .Where(a =>
                    a.Name == $"{slug}-gateway-v{version}-{options.RuntimeId}.tar.gz"
                    || a.Name == $"{slug}-gateway-v{version}-{options.RuntimeId}.tar.gz.sha256"
                )
                .ToList()
                is { Count: 2 } both
                ? both
                : [],
            _ =>
            [
                .. assets.Where(a =>
                    a.Name == $"{prefix}-{options.RuntimeId}.zip" || a.Name == $"{prefix}-{options.RuntimeId}.tar.gz"
                ),
            ],
        };
        return wanted.Count == 0
            ? null
            : [.. wanted.Select(a => new UpdateFile(a.Name!, new Uri(a.BrowserDownloadUrl!), a.Size))];
    }
}
