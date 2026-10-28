// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Http.Json;
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using AprsMap.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AprsMap.Gateway.Tests;

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

/// <summary>The gateway's API key (docs/features/F1.38-gateway-api-key.md, acceptance tests 1–4).</summary>
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

    /// <summary>A new key, saved by the operator.</summary>
    private async Task<string> SaveNewKeyAsync()
    {
        await using var op = await OperatorAsync();
        var key = await op.GenerateApiKeyAsync(Ct);
        await op.SaveApiKeyAsync(key, Ct);
        return key;
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
            await remote.GetApiKeyStatusAsync(Ct);
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
    public async Task AGeneratedKeyIs43UrlSafeCharactersAndOnlyItsHashIsKept()
    {
        await using var op = await OperatorAsync();

        var key = await op.GenerateApiKeyAsync(Ct);
        var status = await op.SaveApiKeyAsync(key, Ct);

        Assert.Matches("^[A-Za-z0-9_-]{43}$", key);
        Assert.True(status.Set);
        Assert.Equal(status, await op.GetApiKeyStatusAsync(Ct));
        var path = Path.Combine(gateway.SettingsFolder, ApiKeyStore.FileName);
        Assert.DoesNotContain(key, await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.DoesNotContain(gateway.Logs.Messages, m => m.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOperatorCanTestAKeyAgainstTheCurrentOneWithoutSavingIt()
    {
        // #447
        await using var op = await OperatorAsync();
        await op.RemoveApiKeyAsync(Ct);
        Assert.Equal(ApiKeyCheck.None, await op.CheckApiKeyAsync(ApiKeyStore.Generate(), Ct));

        var key = await SaveNewKeyAsync();

        Assert.Equal(ApiKeyCheck.Accepted, await op.CheckApiKeyAsync(key, Ct));
        Assert.Equal(ApiKeyCheck.Accepted, await op.CheckApiKeyAsync($" {key} ", Ct)); // as pasted
        Assert.Equal(ApiKeyCheck.Refused, await op.CheckApiKeyAsync(ApiKeyStore.Generate(), Ct));
        Assert.True((await op.GetApiKeyStatusAsync(Ct)).Set); // nothing changed
        await using var viewer = await AppAsync(null);
        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.CheckApiKeyAsync(key, Ct));
        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);
    }

    [Fact]
    public async Task AShortKeyIsRefused()
    {
        await using var op = await OperatorAsync();

        var refused = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            op.SaveApiKeyAsync(new string('k', ApiKeys.MinLength - 1), Ct)
        );

        Assert.Equal(EngineErrorCodes.ApiKeyTooShort, refused.EngineError.Code);
    }

    [Fact]
    public async Task AnAppWithTheKeyOnTheAdminPortIsTheOperator()
    {
        var key = await SaveNewKeyAsync();

        await using var withKey = await AppAsync(key);
        await using var without = await AppAsync(null);
        await using var wrongKey = await AppAsync(ApiKeyStore.Generate());
        await using var viewerPort = await AppAsync(key, port: null);
        await using var plainHttp = await AppAsync(key, https: false);

        Assert.True(await IsOperatorAsync(withKey));
        Assert.False((await withKey.GetSettingsAsync(Ct)).ReadOnly); // it may change settings
        var engineSays = await Assert.ThrowsAnyAsync<Exception>(() => withKey.SendPositionAsync(null, Ct));
        Assert.NotEqual(EngineErrorCodes.OperatorOnly, (engineSays as IHasEngineError)?.EngineError.Code);
        Assert.False(await IsOperatorAsync(without));
        Assert.False(await IsOperatorAsync(wrongKey));
        Assert.False(await IsOperatorAsync(viewerPort)); // the public port ignores it
        Assert.False(await IsOperatorAsync(plainHttp)); // plain HTTP from another machine
        Assert.True((await viewerPort.GetSettingsAsync(Ct)).ReadOnly);
    }

    [Fact]
    public async Task AnAppIsToldWhetherTheGatewayTookItsKey()
    {
        var key = await SaveNewKeyAsync();

        using var accepted = await MeAsync(key);
        using var refused = await MeAsync(ApiKeyStore.Generate());
        using var viewerPort = await MeAsync(key, port: 8080);

        var me = await accepted.Content.ReadFromJsonAsync(EngineJsonContext.Default.AuthInfo, Ct);
        Assert.Equal((true, ApiKeyGate.OperatorName, true), (me!.Operator, me.Name, me.ApiKey));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(
            EngineErrorCodes.ApiKeyRefused,
            (await refused.Content.ReadFromJsonAsync(EngineJsonContext.Default.EngineError, Ct))!.Code
        );
        Assert.Equal(
            AuthInfo.Viewer,
            await viewerPort.Content.ReadFromJsonAsync(EngineJsonContext.Default.AuthInfo, Ct)
        );
        Assert.Contains(gateway.Logs.Messages, m => m.StartsWith("API key accepted from", StringComparison.Ordinal));
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
    public async Task TheOperatorSeesHowManyOtherAppsUseTheKey()
    {
        // #450: replacing a key in use locks those apps out, so Gateway access says how many there are.
        var key = await SaveNewKeyAsync();
        await using var app = await AppAsync(key);
        Assert.Equal(0, (await app.GetApiKeyStatusAsync(Ct)).InUse); // not counting itself
        await using var op = await OperatorAsync();

        Assert.Equal(1, (await op.GetApiKeyStatusAsync(Ct)).InUse);
    }

    [Fact]
    public async Task ANewKeyRefusesTheOldOneAtItsNextCall()
    {
        var old = await SaveNewKeyAsync();
        await using var app = await AppAsync(old);
        Assert.True(await IsOperatorAsync(app));

        var replacement = await SaveNewKeyAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => app.GetApiKeyStatusAsync(Ct));
        await using var again = await AppAsync(old);
        Assert.False(await IsOperatorAsync(again));
        await using var current = await AppAsync(replacement);
        Assert.True(await IsOperatorAsync(current));
    }

    [Fact]
    public async Task RemovingTheKeyTurnsApiAccessOff()
    {
        var key = await SaveNewKeyAsync();
        await using (var op = await OperatorAsync())
        {
            await op.RemoveApiKeyAsync(Ct);
            Assert.Equal(ApiKeyStatus.None, await op.GetApiKeyStatusAsync(Ct));
        }

        await using var app = await AppAsync(key);

        Assert.False(await IsOperatorAsync(app));
        Assert.False(File.Exists(Path.Combine(gateway.SettingsFolder, ApiKeyStore.FileName)));
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
        var key = ApiKeyStore.Generate();
        gateway.Services.GetRequiredService<ApiKeyStore>().Save(key);

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
        var key = ApiKeyStore.Generate();
        store.Save(key);

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
        Assert.Null(limiter.Wait("192.0.2.1", ApiKeyGate.OperatorName)); // nor is the key locked for other addresses
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
        store.Save(ApiKeyStore.Generate());

        for (var i = 0; i < SignInLimiter.AddressLimit * 2; i++)
        {
            using var refused = await MeAsync(old); // started again and again with the key it was given last week
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        var key = ApiKeyStore.Generate();
        store.Save(key);
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
