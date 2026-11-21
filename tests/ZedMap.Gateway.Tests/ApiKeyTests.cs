// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ZedMap.Client.Remote;
using ZedMap.Contracts;
using ZedMap.Server;

namespace ZedMap.Gateway.Tests;

/// <summary>An operator gateway with an operator port, for the API key's tests.</summary>
public sealed class ApiKeyGatewayFactory : OperatorGatewayFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder
            .UseSetting(
                "Gateway:OperatorPort",
                OperatorPortGatewayFactory.OperatorPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            )
            .ConfigureTestServices(s => s.AddSingleton<IStartupFilter, LocalPortFilter>());
    }
}

/// <summary>The gateway's API keys, one per app (docs/features/F1.38-gateway-api-key.md, acceptance tests 1–6).</summary>
public sealed class ApiKeyTests(ApiKeyGatewayFactory gateway) : IClassFixture<ApiKeyGatewayFactory>
{
    private const int Admin = OperatorPortGatewayFactory.OperatorPort;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The signed-in operator, on the admin port.</summary>
    private async Task<RemoteEngineService> OperatorAsync()
    {
        var cookie = await SignInAsync();
        var remote = gateway.CreateRemote(cookie, localPort: Admin);
        await remote.StartAsync(Ct);
        return remote;
    }

    private async Task<string> SignInAsync()
    {
        using var browser = gateway.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthPaths.SignIn)
        {
            Content = JsonContent.Create(
                new SignInRequest(OperatorGatewayFactory.Name, OperatorGatewayFactory.Password),
                EngineJsonContext.Default.SignInRequest
            ),
        };
        request.Headers.Add(LocalPortFilter.Header, "8081");
        using var response = await browser.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    /// <summary>A new key for an app, created by the operator.</summary>
    private async Task<string> SaveNewKeyAsync(ApiKeyAccess access = ApiKeyAccess.Operator, string? name = null)
    {
        await using var op = await OperatorAsync();
        return (await op.CreateApiKeyAsync(name ?? "App " + Guid.NewGuid().ToString("N")[..8], access, Ct)).Key;
    }

    private async Task<RemoteEngineService> AppAsync(string? key, int? port = Admin, bool https = true)
    {
        var remote = gateway.CreateRemote(localPort: port, apiKey: key, https: https);
        await remote.StartAsync(Ct);
        return remote;
    }

    /// <summary>Whether <paramref name="remote"/> may do what only the operator may.</summary>
    private static async Task<bool> IsOperatorAsync(RemoteEngineService remote)
    {
        try
        {
            await remote.GetApiKeysAsync(Ct);
            return true;
        }
        catch (EngineAccessException ex) when (ex.EngineError.Code == EngineErrorCodes.OperatorOnly)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> MeAsync(string key, int port = Admin, string scheme = "https")
    {
        using var browser = gateway.Browser(scheme);
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthPaths.Me);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add(LocalPortFilter.Header, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await browser.SendAsync(request, Ct);
    }

    [Fact]
    public async Task ANewKeyIs43UrlSafeCharactersShownOnceAndOnlyItsHashIsKept()
    {
        await using var op = await OperatorAsync();

        var created = await op.CreateApiKeyAsync("  Derek's laptop ", ApiKeyAccess.Operator, Ct);

        Assert.Matches("^[A-Za-z0-9_-]{43}$", created.Key);
        Assert.Equal("Derek's laptop", created.Info.Name);
        var listed = Assert.Single(await op.GetApiKeysAsync(Ct), k => k.Id == created.Info.Id);
        Assert.Equal((ApiKeyAccess.Operator, (DateTimeOffset?)null), (listed.Access, listed.LastUsedAt));
        var path = Path.Combine(gateway.SettingsFolder, ApiKeyStore.FileName);
        Assert.DoesNotContain(created.Key, await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.DoesNotContain(gateway.Logs.Messages, m => m.Contains(created.Key, StringComparison.Ordinal));
        Assert.Contains(gateway.Logs.Messages, m =>
            m.Contains("created the API key Derek's laptop", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task AKeyNeedsANameOfItsOwn()
    {
        await using var op = await OperatorAsync();
        await op.CreateApiKeyAsync("Shack PC", ApiKeyAccess.Operator, Ct);

        foreach (var name in new[] { " ", "shack pc", new string('n', ApiKeys.MaxNameLength + 1) })
        {
            var refused = await Assert.ThrowsAsync<EngineArgumentException>(() =>
                op.CreateApiKeyAsync(name, ApiKeyAccess.Operator, Ct)
            );
            Assert.Equal(EngineErrorCodes.ApiKeyName, refused.EngineError.Code);
        }
    }

    [Fact]
    public async Task TheOperatorCanTestAKeyWithoutSavingIt()
    {
        // #447
        var key = await SaveNewKeyAsync();
        await using var op = await OperatorAsync();
        var before = await op.GetApiKeysAsync(Ct);

        Assert.Equal(ApiKeyCheck.Accepted, await op.CheckApiKeyAsync(key, Ct));
        Assert.Equal(ApiKeyCheck.Accepted, await op.CheckApiKeyAsync($" {key} ", Ct)); // as pasted
        Assert.Equal(ApiKeyCheck.Refused, await op.CheckApiKeyAsync(ApiKeyStore.Generate(), Ct));
        Assert.Equal(before.Count, (await op.GetApiKeysAsync(Ct)).Count); // nothing changed
        await using var viewer = await AppAsync(null);
        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.CheckApiKeyAsync(key, Ct));
        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);
    }

    [Fact]
    public async Task AnAppWithAnOperatorKeyOnTheAdminPortIsTheOperator()
    {
        var key = await SaveNewKeyAsync();

        await using var withKey = await AppAsync(key);
        await using var without = await AppAsync(null);
        await using var wrongKey = await AppAsync(ApiKeyStore.Generate());
        await using var viewerPort = await AppAsync(key, port: null);
        await using var plainHttp = await AppAsync(key, https: false);

        Assert.True(await IsOperatorAsync(withKey));
        Assert.False((await withKey.GetSettingsAsync(Ct)).ReadOnly); // it may change settings
        Assert.True((await withKey.GetHistoryStatusAsync(Ct)).Available); // and see the history (F1.41)
        var engineSays = await Assert.ThrowsAnyAsync<Exception>(() => withKey.SendPositionAsync(null, Ct));
        Assert.NotEqual(EngineErrorCodes.OperatorOnly, (engineSays as IHasEngineError)?.EngineError.Code);
        Assert.False(await IsOperatorAsync(without));
        Assert.False(await IsOperatorAsync(wrongKey));
        Assert.False(await IsOperatorAsync(viewerPort)); // the public port ignores it
        Assert.False(await IsOperatorAsync(plainHttp)); // plain HTTP from another machine
        Assert.True((await viewerPort.GetSettingsAsync(Ct)).ReadOnly);
    }

    [Fact]
    public async Task AWatchOnlyKeyOnlyWatches()
    {
        var key = await SaveNewKeyAsync(ApiKeyAccess.Watch, "Wall display");

        await using var app = await AppAsync(key);
        using var me = await MeAsync(key);

        Assert.False(await IsOperatorAsync(app));
        Assert.True((await app.GetSettingsAsync(Ct)).ReadOnly);
        var info = await me.Content.ReadFromJsonAsync(EngineJsonContext.Default.AuthInfo, Ct);
        Assert.Equal((false, "Wall display"), (info!.Operator, info.Name));
    }

    [Fact]
    public async Task AnAppIsToldWhetherTheGatewayTookItsKey()
    {
        var key = await SaveNewKeyAsync(name: "Phone");

        using var accepted = await MeAsync(key);
        using var refused = await MeAsync(ApiKeyStore.Generate());
        using var viewerPort = await MeAsync(key, port: 8080);

        var me = await accepted.Content.ReadFromJsonAsync(EngineJsonContext.Default.AuthInfo, Ct);
        Assert.Equal((true, "API key: Phone", true), (me!.Operator, me.Name, me.ApiKey));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(
            EngineErrorCodes.ApiKeyRefused,
            (await refused.Content.ReadFromJsonAsync(EngineJsonContext.Default.EngineError, Ct))!.Code
        );
        Assert.Equal(
            AuthInfo.Viewer,
            await viewerPort.Content.ReadFromJsonAsync(EngineJsonContext.Default.AuthInfo, Ct)
        );
        Assert.Contains(gateway.Logs.Messages, m =>
            m.StartsWith("API key Phone accepted from", StringComparison.Ordinal)
        );
        Assert.Contains(gateway.Logs.Messages, m => m.Contains("wrong key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OverPlainHttpFromAnotherComputerTheAppIsToldWhyNotThatTheKeyIsWrong()
    {
        var key = await SaveNewKeyAsync();

        using var response = await MeAsync(key, scheme: "http"); // the test server's caller isn't loopback

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            EngineErrorCodes.AuthNeedsHttps,
            (await response.Content.ReadFromJsonAsync(EngineJsonContext.Default.EngineError, Ct))!.Code
        );
    }

    [Fact]
    public async Task TheOperatorSeesWhenAndWhereEachKeyWasLastUsedAndWhetherItsConnected()
    {
        // Behavior 9: which key a device has, and whether revoking it disconnects something.
        await using var op = await OperatorAsync();
        var created = await op.CreateApiKeyAsync(
            "Laptop " + Guid.NewGuid().ToString("N")[..6],
            ApiKeyAccess.Operator,
            Ct
        );
        await using var app = await AppAsync(created.Key);
        Assert.Equal(0, (await app.GetApiKeysAsync(Ct)).Single(k => k.Id == created.Info.Id).InUse); // not itself

        var listed = (await op.GetApiKeysAsync(Ct)).Single(k => k.Id == created.Info.Id);

        Assert.Equal(1, listed.InUse);
        Assert.NotNull(listed.LastUsedAt);
        Assert.False(string.IsNullOrEmpty(listed.LastUsedFrom));
    }

    [Fact]
    public async Task RevokingOneAppsKeyDisconnectsItAndLeavesTheOthersWorking()
    {
        // Behavior 1: let a device in, or shut one out, without breaking the rest.
        await using var op = await OperatorAsync();
        var laptop = await op.CreateApiKeyAsync(
            "Laptop " + Guid.NewGuid().ToString("N")[..6],
            ApiKeyAccess.Operator,
            Ct
        );
        var phone = await op.CreateApiKeyAsync("Phone " + Guid.NewGuid().ToString("N")[..6], ApiKeyAccess.Operator, Ct);
        await using var laptopApp = await AppAsync(laptop.Key);
        await using var phoneApp = await AppAsync(phone.Key);
        Assert.True(await IsOperatorAsync(laptopApp));

        await op.RevokeApiKeyAsync(laptop.Info.Id, Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => laptopApp.GetApiKeysAsync(Ct));
        await using var again = await AppAsync(laptop.Key);
        Assert.False(await IsOperatorAsync(again));
        Assert.True(await IsOperatorAsync(phoneApp)); // still connected
        Assert.DoesNotContain(await op.GetApiKeysAsync(Ct), k => k.Id == laptop.Info.Id);
        Assert.Contains(gateway.Logs.Messages, m => m.Contains("revoked the API key Laptop", StringComparison.Ordinal));
    }
}

/// <summary>A gateway's single key from before per-app keys (F1.38 behavior 10), on a gateway of its own.</summary>
public sealed class LegacyApiKeyTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("zedmap-keys").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void TheEarlierKeyKeepsWorkingAsExistingKeyUntilItsRevoked()
    {
        var old = ApiKeyStore.Generate();
        File.WriteAllText(
            Path.Combine(_folder, ApiKeyStore.LegacyFileName),
            $$"""{"hash":"{{PasswordHashing.Hash(old)}}","setAt":"2026-10-26T12:00:00+00:00"}"""
        );
        var store = new ApiKeyStore(_folder, TimeProvider.System);

        var existing = Assert.Single(store.List(null));
        Assert.Equal((ApiKeyStore.LegacyName, ApiKeyAccess.Operator), (existing.Name, existing.Access));
        Assert.Equal(existing.Id, store.Check(old)?.Id);
        Assert.Null(store.Check(ApiKeyStore.Generate()));
        Assert.False(File.Exists(Path.Combine(_folder, ApiKeyStore.LegacyFileName)));

        var again = new ApiKeyStore(_folder, TimeProvider.System); // after a restart
        Assert.Equal(existing.Id, again.Check(old)?.Id);
        again.Revoke(existing.Id);
        Assert.Null(new ApiKeyStore(_folder, TimeProvider.System).Check(old));
    }
}

/// <summary>
/// The connection test (#419), on a gateway of its own: its wrong key counts toward the address's wait, which the other
/// tests' sign-ins mustn't run into.
/// </summary>
public sealed class ConnectionTestTests(ApiKeyGatewayFactory gateway) : IClassFixture<ApiKeyGatewayFactory>
{
    private const int Admin = OperatorPortGatewayFactory.OperatorPort;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<GatewayTest> TestAsync(string? key, int port = Admin, string scheme = "https")
    {
        using var browser = gateway.Browser(scheme);
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthPaths.Test);
        if (key is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
        request.Headers.Add(LocalPortFilter.Header, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await browser.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(EngineJsonContext.Default.GatewayTest, Ct))!;
    }

    [Fact]
    public async Task TheConnectionTestSaysTheVersionAndWhatTheKeyComesTo()
    {
        // #419: an app tests the address and key before saving them.
        var key = gateway.Services.GetRequiredService<ApiKeyStore>().Create("Test", ApiKeyAccess.Operator).Key;

        var accepted = await TestAsync(key);

        Assert.Equal(Brand.Slug, accepted.Product);
        Assert.Equal(EngineHub.ContractVersion, accepted.ContractVersion);
        Assert.False(string.IsNullOrEmpty(accepted.Version));
        Assert.Equal(ApiKeyCheck.Accepted, accepted.Key);
        Assert.Equal(ApiKeyCheck.None, (await TestAsync(null)).Key);
        Assert.Equal(ApiKeyCheck.Refused, (await TestAsync(ApiKeyStore.Generate())).Key);
        Assert.Equal(ApiKeyCheck.NeedsHttps, (await TestAsync(key, scheme: "http")).Key);
        Assert.Equal(ApiKeyCheck.NotAdminPort, (await TestAsync(key, port: 8080)).Key);
    }
}

/// <summary>Guessing the key (F1.38 behavior 7), on a gateway of its own so the wait doesn't reach other tests.</summary>
public sealed class ApiKeyGuessingTests(ApiKeyGatewayFactory gateway) : IClassFixture<ApiKeyGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WrongKeysMakeTheAddressWaitButNotEveryone()
    {
        var store = gateway.Services.GetRequiredService<ApiKeyStore>();
        var key = store.Create("Guessed", ApiKeyAccess.Operator).Key;

        for (var i = 0; i < SignInLimiter.AddressLimit; i++)
        {
            using var wrong = await MeAsync(ApiKeyStore.Generate());
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        using var waiting = await MeAsync(key);

        // Even the right key waits, from that address, and the app is told it's a wait, not a wrong key (#450).
        Assert.Equal(HttpStatusCode.TooManyRequests, waiting.StatusCode);
        var error = await waiting.Content.ReadFromJsonAsync(EngineJsonContext.Default.EngineError, Ct);
        Assert.Equal(EngineErrorCodes.AuthTooManyTries, error!.Code);
        Assert.Equal("15", error.Arg(EngineErrorArgs.Minutes));
        Assert.Contains(gateway.Logs.Messages, m => m.Contains("too many tries", StringComparison.Ordinal));
        var test = await TestAsync(key);
        Assert.Equal((ApiKeyCheck.Waiting, 15), (test.Key, test.WaitMinutes));
        var limiter = gateway.Services.GetRequiredService<SignInLimiter>();
        Assert.Null(limiter.Wait("192.0.2.1")); // nor is the key locked for other addresses
    }

    private async Task<GatewayTest> TestAsync(string key)
    {
        using var browser = gateway.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthPaths.Test);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add(LocalPortFilter.Header, "8081");
        using var response = await browser.SendAsync(request, Ct);
        return (await response.Content.ReadFromJsonAsync(EngineJsonContext.Default.GatewayTest, Ct))!;
    }

    private async Task<HttpResponseMessage> MeAsync(string key)
    {
        using var browser = gateway.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthPaths.Me);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add(LocalPortFilter.Header, "8081");
        return await browser.SendAsync(request, Ct);
    }
}

/// <summary>An app with an old key isn't guessing (#450), on a gateway of its own so no wait reaches other tests.</summary>
public sealed class OldApiKeyTests(ApiKeyGatewayFactory gateway) : IClassFixture<ApiKeyGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAppRetryingAnOldKeyDoesNotLockItsComputerOut()
    {
        var store = gateway.Services.GetRequiredService<ApiKeyStore>();
        var old = ApiKeyStore.Generate();
        store.Create("Current", ApiKeyAccess.Operator);

        for (var i = 0; i < SignInLimiter.AddressLimit * 2; i++)
        {
            using var refused = await MeAsync(old); // started again and again with the key it was given last week
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        var key = store.Create("New", ApiKeyAccess.Operator).Key;
        using var accepted = await MeAsync(key);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, gateway.Services.GetRequiredService<SignInLimiter>().FailedToday); // counted once
        Assert.Equal(10, gateway.Logs.Messages.Count(m => m.Contains("wrong key", StringComparison.Ordinal))); // all logged
    }

    private async Task<HttpResponseMessage> MeAsync(string key)
    {
        using var browser = gateway.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthPaths.Me);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add(LocalPortFilter.Header, "8081");
        return await browser.SendAsync(request, Ct);
    }
}
