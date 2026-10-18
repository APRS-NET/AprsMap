// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The update check (docs/features/F4.15-updates.md, acceptance tests 1–4).</summary>
public sealed class UpdateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Slug = Brand.Slug;

    /// <summary>Answers the releases API from a script and records what it was asked.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new(HttpStatusCode.OK);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(Answer(request));
        }
    }

    private static string Release(string tag, bool draft = false, params string[] assets) =>
        $$"""
            {"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"published_at":"2026-10-20T12:00:00Z",
             "html_url":"https://github.com/APRS-NET/AprsMap/releases/tag/{{tag}}","body":"Notes for {{tag}}",
             "assets":[{{string.Join(
                ',',
                assets.Select(a =>
                    $$"""{"name":"{{a}}","browser_download_url":"https://example.org/{{a}}","size":10}"""
                )
            )}}]}
            """;

    private static HttpResponseMessage Json(string body, string? etag = "\"one\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }
        return response;
    }

    private static UpdateOptions Archive => new() { Kind = InstallKind.Archive, RuntimeId = "linux-x64" };

    [Theory]
    [InlineData("0.4.0-alpha.10", "0.4.0-alpha.9")]
    [InlineData("0.4.0", "0.4.0-rc.1")]
    [InlineData("0.5.0-alpha.1", "0.4.9")]
    [InlineData("0.4.0-beta", "0.4.0-alpha.9")]
    [InlineData("0.4.0-alpha.1", "0.4.0-alpha")]
    public void VersionsFollowSemVerPrecedence(string newer, string older)
    {
        Assert.True(SemVersion.TryParse(newer, out var a));
        Assert.True(SemVersion.TryParse("v" + older + "+build.7", out var b));
        Assert.True(a.CompareTo(b) > 0);
        Assert.True(b.CompareTo(a) < 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.4")]
    [InlineData("0.4.x")]
    [InlineData("0.4.0-")]
    [InlineData("latest")]
    public void WhatIsntSemVerIsntAVersion(string text) => Assert.False(SemVersion.TryParse(text, out _));

    [Fact]
    public void TheNewestPublishedReadyReleaseOnTheChannelIsOffered()
    {
        GitHubRelease Parse(string json) =>
            System.Text.Json.JsonSerializer.Deserialize("[" + json + "]", GitHubJson.Default.ListGitHubRelease)![0];
        var archive = (string v) => $"{Slug}-v{v}-linux-x64.tar.gz";
        var releases = new[]
        {
            Parse(Release("v0.6.0-alpha.1", draft: true, archive("0.6.0-alpha.1"))), // a draft
            Parse(Release("v0.5.0-alpha.2")), // not ready: no files yet
            Parse(Release("v0.5.0-alpha.1", false, archive("0.5.0-alpha.1"))),
            Parse(Release("v0.4.0", false, archive("0.4.0"))),
            Parse(Release("nightly", false, archive("nightly"))), // a bad tag
            Parse(Release("v0.3.0", false, archive("0.3.0"))), // older
        };

        var pre = UpdateChecker.Pick(releases, "0.4.0-alpha.5", UpdateChannel.PreReleases, null, Archive);
        Assert.Equal("0.5.0-alpha.1", pre!.Version);
        Assert.Equal(archive("0.5.0-alpha.1"), Assert.Single(pre.Files).Name);
        Assert.Equal("Notes for v0.5.0-alpha.1", pre.Notes);

        Assert.Equal(
            "0.4.0",
            UpdateChecker.Pick(releases, "0.4.0-alpha.5", UpdateChannel.Stable, null, Archive)!.Version
        );
        Assert.Null(UpdateChecker.Pick(releases, "0.4.0", UpdateChannel.Stable, null, Archive)); // nothing newer, stable
        // Skipped: nothing until a newer version than the one skipped (behavior 9.3).
        Assert.Null(UpdateChecker.Pick(releases, "0.4.0-alpha.5", UpdateChannel.PreReleases, "0.5.0-alpha.1", Archive));

        var gateway = UpdateChecker.Pick(
            [Parse(Release("v0.5.0-alpha.1", false, $"{Slug}-v0.5.0-alpha.1-image.txt"))],
            "0.4.0",
            UpdateChannel.PreReleases,
            null,
            new UpdateOptions { Kind = InstallKind.Gateway }
        );
        Assert.EndsWith($"/{Slug}:0.5.0-alpha.1", gateway!.ImageTag, StringComparison.Ordinal);
    }

    private static (Engine Engine, FakeGitHub GitHub, InMemorySettingsStore Store, FakeTimeProvider Time) Create(
        bool check = true
    )
    {
        var github = new FakeGitHub();
        var store = InMemorySettingsStore.WithProfile();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T12:00:00Z", null));
        var engine = new Engine(
            Options.Create(
                new EngineOptions
                {
                    Updates = new UpdateOptions { Check = check, RuntimeId = "linux-x64" },
                }
            ),
            new EngineTests.FakeSourceFactory(),
            store,
            time,
            NullLogger<Engine>.Instance
        )
        {
            UpdateHttp = new HttpClient(github),
            RunningVersion = "0.4.0-alpha.5",
        };
        return (engine, github, store, time);
    }

    [Fact]
    public async Task CheckNowAsksOnceAnonymouslyAndKeepsTheTime()
    {
        var (engine, github, store, time) = Create();
        github.Answer = _ =>
            Json("[" + Release("v0.5.0-alpha.1", false, $"{Slug}-v0.5.0-alpha.1-linux-x64.tar.gz") + "]");

        var status = await engine.CheckForUpdateAsync(Ct);

        Assert.Equal("0.5.0-alpha.1", status.Update!.Version);
        Assert.Equal(UpdateChannel.PreReleases, status.Channel); // a pre-release build's own
        Assert.Equal(time.GetUtcNow(), (await store.LoadAsync(Ct))!.EffectiveUpdates.LastChecked);
        var request = Assert.Single(github.Requests);
        Assert.EndsWith(
            "/repos/APRS-NET/AprsMap/releases?per_page=20",
            request.RequestUri!.ToString(),
            StringComparison.Ordinal
        );
        Assert.StartsWith(Slug + "/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Null(request.Headers.Authorization);

        github.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        Assert.Equal("0.5.0-alpha.1", (await engine.CheckForUpdateAsync(Ct)).Update!.Version); // 304 keeps it
        Assert.Equal("\"one\"", github.Requests[^1].Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task AFailedCheckSaysWhyAndKeepsWhatWasFound()
    {
        var (engine, github, _, _) = Create();
        github.Answer = _ =>
            Json("[" + Release("v0.5.0-alpha.1", false, $"{Slug}-v0.5.0-alpha.1-linux-x64.tar.gz") + "]");
        await engine.CheckForUpdateAsync(Ct);

        github.Answer = _ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.Add("x-ratelimit-remaining", "0");
            return limited;
        };
        var limitedStatus = await engine.CheckForUpdateAsync(Ct);
        Assert.Equal(EngineErrorCodes.UpdatesRateLimited, limitedStatus.LastError!.Code);
        Assert.Equal("0.5.0-alpha.1", limitedStatus.Update!.Version);

        github.Answer = _ => Json("{ not json", etag: null);
        Assert.Equal(EngineErrorCodes.UpdatesUnexpectedAnswer, (await engine.CheckForUpdateAsync(Ct)).LastError!.Code);
        github.Answer = _ => throw new HttpRequestException("offline");
        Assert.Equal(EngineErrorCodes.UpdatesUnreachable, (await engine.CheckForUpdateAsync(Ct)).LastError!.Code);
    }

    [Fact]
    public async Task SkippingAVersionHidesItUntilANewerOne()
    {
        var (engine, github, _, _) = Create();
        github.Answer = _ =>
            Json("[" + Release("v0.5.0-alpha.1", false, $"{Slug}-v0.5.0-alpha.1-linux-x64.tar.gz") + "]");
        await engine.CheckForUpdateAsync(Ct);

        await engine.SkipUpdateAsync("0.5.0-alpha.1", Ct);

        Assert.Null((await engine.GetUpdateStatusAsync(Ct)).Update);
        Assert.Null((await engine.CheckForUpdateAsync(Ct)).Update);
        github.Answer = _ =>
            Json("[" + Release("v0.5.0-alpha.2", false, $"{Slug}-v0.5.0-alpha.2-linux-x64.tar.gz") + "]", "\"two\"");
        Assert.Equal("0.5.0-alpha.2", (await engine.CheckForUpdateAsync(Ct)).Update!.Version);
    }

    [Fact]
    public async Task TheScheduleChecksAMinuteAfterStartThenDaily()
    {
        var (engine, github, _, time) = Create();
        github.Answer = _ => Json("[]");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = engine.RunUpdatesAsync(cts.Token);

        await Settle();
        Assert.Empty(github.Requests);
        time.Advance(Engine.FirstUpdateCheck);
        await WaitUntil(() => github.Requests.Count == 1);
        time.Advance(TimeSpan.FromHours(23));
        await Settle();
        Assert.Single(github.Requests);
        time.Advance(TimeSpan.FromHours(1));
        await WaitUntil(() => github.Requests.Count == 2);

        await cts.CancelAsync();
        await running;
    }

    [Fact]
    public async Task WithTheCheckTurnedOffWhereTheEngineStartsNothingIsAsked()
    {
        var (engine, github, _, time) = Create(check: false);

        await engine.RunUpdatesAsync(Ct); // returns at once
        var status = await engine.CheckForUpdateAsync(Ct);

        Assert.True(status.CheckDisabled);
        Assert.Empty(github.Requests);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Yield();
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Ct);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
        }
        Assert.True(condition());
    }
}
