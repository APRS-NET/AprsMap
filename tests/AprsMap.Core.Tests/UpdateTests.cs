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
    public async Task OnStartThenOnceInEachTwoHourWindowAtARandomMoment()
    {
        var (engine, github, store, time) = Create();
        // Checked a minute ago, before a restart: the start still checks (behavior 1).
        await store.SaveAsync(
            (await store.LoadAsync(Ct))! with
            {
                Updates = new UpdateSettings(LastChecked: time.GetUtcNow().AddMinutes(-1)),
            },
            Ct
        );
        engine.UpdateJitter = () => 0.25; // half an hour into each two-hour window
        github.Answer = _ => Json("[]");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = engine.RunUpdatesAsync(cts.Token);

        await Settle();
        time.Advance(Engine.FirstUpdateCheck);
        await WaitUntil(() => github.Requests.Count == 1); // on start
        time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(29));
        await Settle();
        Assert.Single(github.Requests);
        time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntil(() => github.Requests.Count == 2); // 2 h 30 min after start
        engine.UpdateJitter = () => 0.9;
        time.Advance(TimeSpan.FromHours(1.5) + TimeSpan.FromHours(1.8));
        await WaitUntil(() => github.Requests.Count == 3); // 4 h 48 min: the next window, at its own random moment

        await cts.CancelAsync();
        await running;
    }

    [Fact]
    public async Task WithCheckForUpdatesOffNothingIsAskedOrOfferedUnlessCheckNow()
    {
        var (engine, github, _, time) = Create();
        github.Answer = _ =>
            Json("[" + Release("v0.5.0-alpha.1", false, $"{Slug}-v0.5.0-alpha.1-linux-x64.tar.gz") + "]");
        await engine.CheckUpdatesNowAsync(Ct); // found before the setting went off
        var settings = await engine.GetSettingsAsync(Ct);
        await engine.SaveSettingsAsync(
            settings with
            {
                Updates = settings.EffectiveUpdates with { Check = false },
            },
            Ct
        );
        var status = await engine.GetUpdateStatusAsync(Ct);
        Assert.True(status.CheckOff);
        Assert.Null(status.Update); // nothing offered with the setting off (behavior 6)

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = engine.RunUpdatesAsync(cts.Token);
        time.Advance(Engine.FirstUpdateCheck + TimeSpan.FromHours(6));
        await Settle();
        Assert.Single(github.Requests); // only the check before
        await cts.CancelAsync();
        await running;

        Assert.Equal("0.5.0-alpha.1", (await engine.CheckForUpdateAsync(Ct)).Update?.Version); // Check now still works
    }

    [Fact]
    public async Task AFailedCheckIsInTheStatusAndTheNextOneIsAtTheUsualTime()
    {
        var (engine, github, _, _) = Create();
        github.Answer = _ => throw new HttpRequestException("No route to host");

        var status = await engine.CheckForUpdateAsync(Ct);

        Assert.Equal(EngineErrorCodes.UpdatesUnreachable, status.LastError?.Code);
        Assert.NotNull(status.LastChecked);
        Assert.Null(status.Update);
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

    [Fact]
    public void AServiceReleaseIsReadyWithItsTarballAndChecksumBoth()
    {
        UpdateInfo? Pick(params string[] assets) =>
            UpdateChecker.Pick(
                [
                    System.Text.Json.JsonSerializer.Deserialize(
                        "[" + Release("v0.9.0", false, assets) + "]",
                        GitHubJson.Default.ListGitHubRelease
                    )![0],
                ],
                "0.8.0",
                UpdateChannel.PreReleases,
                null,
                new UpdateOptions { Kind = InstallKind.Service, RuntimeId = "linux-arm64" }
            );
        var tarball = $"{Slug}-gateway-v0.9.0-linux-arm64.tar.gz";

        Assert.Null(Pick(tarball)); // the checksum isn't up yet
        Assert.Null(Pick(tarball + ".sha256"));
        Assert.Null(Pick($"{Slug}-gateway-v0.9.0-linux-x64.tar.gz", $"{Slug}-gateway-v0.9.0-linux-x64.tar.gz.sha256"));
        var ready = Pick(tarball, tarball + ".sha256", $"{Slug}-v0.9.0-image.txt");
        Assert.Equal([tarball, tarball + ".sha256"], ready!.Files.Select(f => f.Name));
        Assert.Null(ready.ImageTag); // no docker pull for the service
    }

    /// <summary>A service gateway's engine (F1.40), its data in <paramref name="data"/>, offered 0.9.0.</summary>
    private static async Task<(Engine Engine, FakeTimeProvider Time)> ServiceAsync(DirectoryInfo data)
    {
        var github = new FakeGitHub
        {
            Answer = _ =>
                Json(
                    "["
                        + Release(
                            "v0.9.0",
                            false,
                            $"{Slug}-gateway-v0.9.0-linux-x64.tar.gz",
                            $"{Slug}-gateway-v0.9.0-linux-x64.tar.gz.sha256"
                        )
                        + "]"
                ),
        };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var engine = new Engine(
            Options.Create(
                new EngineOptions
                {
                    DataFolder = data.FullName,
                    Updates = new UpdateOptions { Kind = InstallKind.Service, RuntimeId = "linux-x64" },
                }
            ),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            time,
            NullLogger<Engine>.Instance
        )
        {
            UpdateHttp = new HttpClient(github),
            RunningVersion = "0.8.0",
        };
        await engine.CheckForUpdateAsync(Ct);
        return (engine, time);
    }

    [Fact]
    public async Task UpdateAndRestartOnTheServiceWritesTheVersionOnlyAndFollowsTheUpdater()
    {
        var data = Directory.CreateTempSubdirectory("aprsmap-service-");
        try
        {
            var (engine, _) = await ServiceAsync(data);
            using (engine)
            {
                Assert.True((await engine.GetUpdateStatusAsync(Ct)).CanInstall);

                await engine.InstallUpdateAsync("0.9.0", Ct);

                var request = Path.Combine(data.FullName, Engine.UpdateRequestFile);
                Assert.Equal("""{"version":"0.9.0"}""", await File.ReadAllTextAsync(request, Ct));
                Assert.Equal(
                    new ServiceInstall("0.9.0", ServiceInstallStep.Requested),
                    (await engine.GetUpdateStatusAsync(Ct)).Install
                );
                var busy = await Assert.ThrowsAsync<EngineOperationException>(() =>
                    engine.InstallUpdateAsync("0.9.0", Ct)
                );
                Assert.Equal(EngineErrorCodes.UpdateNotOffered, busy.EngineError.Code);

                // The updater picks it up and reports, as aprsmap-gateway-update.sh writes it.
                File.Delete(request);
                var report = Path.Combine(data.FullName, Engine.UpdateStatusFile);
                await File.WriteAllTextAsync(report, """{"version":"0.9.0","step":"downloading","reason":""}""", Ct);
                Assert.Equal(ServiceInstallStep.Downloading, (await engine.GetUpdateStatusAsync(Ct)).Install!.Step);
                await File.WriteAllTextAsync(
                    report,
                    """{"version":"0.9.0","step":"failed","reason":"the download doesn't match its checksum"}""",
                    Ct
                );
                var failed = (await engine.GetUpdateStatusAsync(Ct)).Install!;
                Assert.Equal(
                    new ServiceInstall("0.9.0", ServiceInstallStep.Failed, "the download doesn't match its checksum"),
                    failed
                );
                Assert.False(File.Exists(report)); // read once, kept until the next start
                Assert.Equal(failed, (await engine.GetUpdateStatusAsync(Ct)).Install);
            }
        }
        finally
        {
            data.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OnlyTheOfferedVersionOnTheServiceIsInstalledAndARequestNeverPickedUpIsDropped()
    {
        var data = Directory.CreateTempSubdirectory("aprsmap-service-");
        try
        {
            var (engine, time) = await ServiceAsync(data);
            using (engine)
            {
                var other = await Assert.ThrowsAsync<EngineOperationException>(() =>
                    engine.InstallUpdateAsync("0.8.5", Ct)
                );
                Assert.Equal(EngineErrorCodes.UpdateNotOffered, other.EngineError.Code);

                await engine.InstallUpdateAsync("0.9.0", Ct);
                File.SetLastWriteTimeUtc(
                    Path.Combine(data.FullName, Engine.UpdateRequestFile),
                    time.GetUtcNow().UtcDateTime - Engine.StaleRequest - TimeSpan.FromMinutes(1)
                );

                var stale = (await engine.GetUpdateStatusAsync(Ct)).Install!;
                Assert.Equal(ServiceInstallStep.Failed, stale.Step);
                Assert.Contains("aprsmap-gateway-update.path", stale.Reason, StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(data.FullName, Engine.UpdateRequestFile)));
            }
        }
        finally
        {
            data.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ADesktopOrContainerEngineNeitherInstallsNorRestarts()
    {
        var (engine, _, _, _) = Create();
        using (engine)
        {
            Assert.False((await engine.GetUpdateStatusAsync(Ct)).CanInstall);
            var install = await Assert.ThrowsAsync<EngineAccessException>(() => engine.InstallUpdateAsync("0.5.0", Ct));
            var restart = await Assert.ThrowsAsync<EngineAccessException>(() => engine.RestartGatewayAsync(Ct));

            Assert.Equal(EngineErrorCodes.GatewayNotService, install.EngineError.Code);
            Assert.Equal(EngineErrorCodes.GatewayNotService, restart.EngineError.Code);
        }
    }
}
