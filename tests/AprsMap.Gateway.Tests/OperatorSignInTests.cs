// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Http.Json;
using AprsMap.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Gateway.Tests;

/// <summary>A gateway with one operator, OPERATOR, set up in its data folder, that keeps what it logs.</summary>
public class OperatorGatewayFactory : GatewayFactory
{
    public const string Name = "Operator";
    public const string Password = "correct horse battery";

    public OperatorGatewayFactory()
    {
        Directory.CreateDirectory(SettingsFolder);
        OperatorStore.Save(
            Path.Combine(SettingsFolder, OperatorStore.FileName),
            [new OperatorRecord(Name, PasswordHashing.Hash(Password, iterations: 1000))]
        );
    }

    public LogSink Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(Logs));
    }

    /// <summary>A browser on the gateway over HTTPS, keeping no cookies of its own.</summary>
    public HttpClient Browser(string scheme = "https") =>
        CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri($"{scheme}://localhost/"),
                HandleCookies = false,
                AllowAutoRedirect = false,
            }
        );

    /// <summary>Signs in and returns the cookie to send back.</summary>
    public async Task<string> SignInAsync(CancellationToken cancellationToken)
    {
        using var browser = Browser();
        using var response = await browser.PostAsJsonAsync(
            AuthPaths.SignIn,
            new SignInRequest(Name, Password),
            EngineJsonContext.Default.SignInRequest,
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }
}

/// <summary>An operator gateway that allows sign-in over plain HTTP (#414).</summary>
public sealed class InsecureOperatorGatewayFactory : OperatorGatewayFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Gateway:AllowInsecureSignIn", "true");
    }
}

/// <summary>An operator gateway where settings changes were allowed before operators existed.</summary>
public sealed class TrustedOperatorGatewayFactory : OperatorGatewayFactory
{
    protected override bool AllowSettingsChanges => true;
}

/// <summary>Keeps every message logged, for checking what is and isn't written.</summary>
public sealed class LogSink : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(LogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => sink.Messages.Enqueue(formatter(state, exception));
    }
}

/// <summary>Operator sign-in (docs/features/F2.9-operator-sign-in.md, acceptance tests 1–10).</summary>
public sealed class OperatorSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> SignIn(HttpClient browser, string name, string password) =>
        browser.PostAsJsonAsync(
            AuthPaths.SignIn,
            new SignInRequest(name, password),
            EngineJsonContext.Default.SignInRequest,
            Ct
        );

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync(EngineJsonContext.Default.EngineError, Ct))?.Code;

    private static async Task<AuthInfo> Me(HttpClient browser, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthPaths.Me);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }
        using var response = await browser.SendAsync(request, Ct);
        return (await response.Content.ReadFromJsonAsync(EngineJsonContext.Default.AuthInfo, Ct))!;
    }

    [Fact]
    public void APasswordIsKeptAsASaltedHashAndCheckedAgainstIt()
    {
        var hash = PasswordHashing.Hash("correct horse battery", iterations: 1000);
        var again = PasswordHashing.Hash("correct horse battery", iterations: 1000);

        Assert.StartsWith("pbkdf2-sha256$1000$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("horse", hash, StringComparison.Ordinal);
        Assert.NotEqual(hash, again); // a new salt each time
        Assert.True(PasswordHashing.Verify("correct horse battery", hash));
        Assert.False(PasswordHashing.Verify("correct horse batterY", hash));
        Assert.False(PasswordHashing.Verify("correct horse battery", null)); // checks a decoy, says no
        Assert.False(PasswordHashing.Verify("anything", "not a hash"));
        Assert.StartsWith("pbkdf2-sha256$600000$", PasswordHashing.Hash("x"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOperatorCommandStoresOnlyAHashAndRefusesShortPasswords()
    {
        var folder = Directory.CreateTempSubdirectory("aprsmap-operators-").FullName;
        try
        {
            var output = new StringWriter();
            Func<string, string?> answers(params string[] typed)
            {
                var queue = new Queue<string>(typed);
                return _ => queue.Dequeue();
            }

            Assert.Equal(
                1,
                OperatorCommand.Run(["add", "derek"], folder, answers("too short", "too short"), output, 1000)
            );
            Assert.Equal(
                1,
                OperatorCommand.Run(
                    ["add", "derek"],
                    folder,
                    answers("long enough pass", "different pass"),
                    output,
                    1000
                )
            );
            Assert.Equal(
                0,
                OperatorCommand.Run(
                    ["add", "derek"],
                    folder,
                    answers("long enough pass", "long enough pass"),
                    output,
                    1000
                )
            );
            Assert.Equal(1, OperatorCommand.Run(["add", "DEREK"], folder, answers(), output, 1000)); // names ignore case

            var file = File.ReadAllText(Path.Combine(folder, OperatorStore.FileName));
            Assert.DoesNotContain("long enough", file, StringComparison.Ordinal);
            var stored = Assert.Single(OperatorStore.Load(Path.Combine(folder, OperatorStore.FileName)));
            Assert.True(PasswordHashing.Verify("long enough pass", stored.PasswordHash));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(folder, OperatorStore.FileName))
                );
            }

            Assert.Equal(
                0,
                OperatorCommand.Run(
                    ["password", "derek"],
                    folder,
                    answers("a new long pass", "a new long pass"),
                    output,
                    1000
                )
            );
            Assert.Equal(0, OperatorCommand.Run(["list"], folder, answers(), output));
            Assert.Equal(0, OperatorCommand.Run(["remove", "derek"], folder, answers(), output));
            Assert.Equal(1, OperatorCommand.Run(["remove", "derek"], folder, answers(), output));
            Assert.Equal(2, OperatorCommand.Run(["frobnicate"], folder, answers(), output));
            Assert.DoesNotContain("long", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task WithNoOperatorSignInIsntOfferedAndTheGatewayBehavesAsBefore()
    {
        await using var gateway = new TrustedGatewayFactory();
        using var browser = gateway.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") }
        );

        using var response = await SignIn(browser, "anyone", "whatever it is");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(EngineErrorCodes.AuthNotOfferedHere, await CodeOf(response));
        Assert.False((await Me(browser, null)).SignInOffered);

        await using var remote = gateway.CreateRemote();
        await remote.StartAsync(Ct);
        await remote.SaveSettingsAsync(await remote.GetSettingsAsync(Ct), Ct); // AllowSettingsChanges still decides
    }

    [Fact]
    public async Task AGoodSignInSetsAStrictSessionCookieAndTheHubThenTakesTheOperatorsCalls()
    {
        await using var gateway = new OperatorGatewayFactory();
        using var browser = gateway.Browser();
        Assert.Equal(new AuthInfo(false, SignInOffered: true), await Me(browser, null));

        using var response = await SignIn(browser, " operator ", OperatorGatewayFactory.Password);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(OperatorSignIn.CookieName + "=", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        var session = cookie.Split(';')[0];
        var me = await Me(browser, session);
        Assert.True(me.Operator);
        Assert.Equal(OperatorGatewayFactory.Name, me.Name);

        await using var signedIn = gateway.CreateRemote(session);
        await signedIn.StartAsync(Ct);
        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);

        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.SendPositionAsync(null, Ct));
        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);
        // The operator gets past the gateway; the engine's own rules then apply (F2.9 behavior 14).
        var engineSays = await Assert.ThrowsAnyAsync<Exception>(() => signedIn.SendPositionAsync(null, Ct));
        Assert.NotEqual(EngineErrorCodes.OperatorOnly, (engineSays as IHasEngineError)?.EngineError.Code);
        Assert.False((await signedIn.GetSettingsAsync(Ct)).ReadOnly);
        Assert.True((await viewer.GetSettingsAsync(Ct)).ReadOnly);
        Assert.Contains(
            gateway.Logs.Messages,
            m => m.StartsWith("Operator Operator: SendPosition(", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task AWrongPasswordAndAnUnknownNameGetTheSameAnswerAndNoPasswordIsLogged()
    {
        await using var gateway = new OperatorGatewayFactory();
        using var browser = gateway.Browser();

        using var wrong = await SignIn(browser, OperatorGatewayFactory.Name, "not the password at all");
        using var unknown = await SignIn(browser, "nobody", "not the password at all");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(Ct), await unknown.Content.ReadAsStringAsync(Ct));
        Assert.Equal(EngineErrorCodes.AuthWrongCredentials, await CodeOf(wrong));
        Assert.Contains(gateway.Logs.Messages, m => m.Contains("Failed sign-in for nobody", StringComparison.Ordinal));
        Assert.DoesNotContain(gateway.Logs.Messages, m => m.Contains("not the password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSixthFailureFromOneAddressWaitsAndIsntChecked()
    {
        await using var gateway = new OperatorGatewayFactory();
        using var browser = gateway.Browser();
        for (var i = 0; i < SignInLimiter.AddressLimit; i++)
        {
            using var failed = await SignIn(browser, OperatorGatewayFactory.Name, "wrong password " + i);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var waiting = await SignIn(browser, OperatorGatewayFactory.Name, OperatorGatewayFactory.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, waiting.StatusCode); // even the right password isn't checked
        var error = await waiting.Content.ReadFromJsonAsync(EngineJsonContext.Default.EngineError, Ct);
        Assert.Equal(EngineErrorCodes.AuthTooManyTries, error!.Code);
        Assert.Equal("15", error.Arg(EngineErrorArgs.Minutes));
        Assert.Equal("900", waiting.Headers.RetryAfter!.ToString());
    }

    [Fact]
    public void TheEleventhFailureForANameWaitsFromEveryAddressAndWaitsEnd()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T12:00:00Z", null));
        var limiter = new SignInLimiter(time);
        for (var i = 0; i < SignInLimiter.NameLimit; i++)
        {
            Assert.Null(limiter.Wait($"10.0.0.{i}", "Operator"));
            limiter.Failed($"10.0.0.{i}", "operator");
        }

        Assert.Equal(TimeSpan.FromHours(1), limiter.Wait("10.0.0.99", "OPERATOR"));
        Assert.Null(limiter.Wait("10.0.0.99", "someone else"));
        Assert.Equal(SignInLimiter.NameLimit, limiter.FailedToday);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Null(limiter.Wait("10.0.0.99", "Operator"));
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, limiter.FailedToday);
    }

    [Fact]
    public async Task OverPlainHttpFromAnotherMachineSignInIsRefused()
    {
        await using var gateway = new OperatorGatewayFactory();
        using var browser = gateway.Browser("http"); // the test server's caller isn't loopback

        using var response = await SignIn(browser, OperatorGatewayFactory.Name, OperatorGatewayFactory.Password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(EngineErrorCodes.AuthNeedsHttps, await CodeOf(response));
        Assert.Equal(new AuthInfo(false, NeedsHttps: true), await Me(browser, null));
    }

    [Fact]
    public async Task WithInsecureSignInAllowedItWorksOverPlainHttpWithAWarning()
    {
        // #414: an explicit setting, for a trusted LAN.
        await using var gateway = new InsecureOperatorGatewayFactory();
        using var browser = gateway.Browser("http"); // the test server's caller isn't loopback
        Assert.Equal(new AuthInfo(false, SignInOffered: true, Unencrypted: true), await Me(browser, null));

        using var response = await SignIn(browser, OperatorGatewayFactory.Name, OperatorGatewayFactory.Password);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(OperatorSignIn.UnencryptedCookieName + "=", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase); // a browser keeps it over http
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.True((await Me(browser, cookie.Split(';')[0])).Operator);
        Assert.Contains(
            gateway.Logs.Messages,
            m => m.StartsWith("Gateway:AllowInsecureSignIn is on", StringComparison.Ordinal)
        );

        // The API key too.
        var key = AprsMap.Server.ApiKeyStore.Generate();
        gateway.Services.GetRequiredService<AprsMap.Server.ApiKeyStore>().Save(key);
        using var me = new HttpRequestMessage(HttpMethod.Get, AuthPaths.Me);
        me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var keyed = await browser.SendAsync(me, Ct);
        Assert.True((await keyed.Content.ReadFromJsonAsync(EngineJsonContext.Default.AuthInfo, Ct))!.ApiKey);
    }

    [Fact]
    public async Task SignOutEndsTheSessionAndItsHubConnection()
    {
        await using var gateway = new OperatorGatewayFactory();
        using var browser = gateway.Browser();
        var session = await gateway.SignInAsync(Ct);
        await using var signedIn = gateway.CreateRemote(session);
        await signedIn.StartAsync(Ct);
        await signedIn.GetConversationsAsync(Ct); // allowed while signed in

        using (var request = new HttpRequestMessage(HttpMethod.Post, AuthPaths.SignOut))
        {
            request.Headers.Add("Cookie", session);
            using var response = await browser.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.False((await Me(browser, session)).Operator);
        await Assert.ThrowsAnyAsync<Exception>(() => signedIn.GetConversationsAsync(Ct));
        Assert.Contains(
            gateway.Logs.Messages,
            m => m.StartsWith("Operator Operator signed out", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task APasswordChangeEndsTheOperatorsSessions()
    {
        await using var gateway = new OperatorGatewayFactory();
        using var browser = gateway.Browser();
        var session = await gateway.SignInAsync(Ct);
        Assert.True((await Me(browser, session)).Operator);

        var path = Path.Combine(gateway.SettingsFolder, OperatorStore.FileName);
        OperatorStore.Save(
            path,
            [new OperatorRecord(OperatorGatewayFactory.Name, PasswordHashing.Hash("a brand new password", 1000))]
        );
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5)); // seen as changed whatever the clock's resolution

        Assert.False((await Me(browser, session)).Operator);
    }

    [Fact]
    public async Task AnotherSitesPagesCantReachTheHubOrSignIn()
    {
        await using var gateway = new OperatorGatewayFactory();
        using var browser = gateway.Browser();

        using var negotiate = new HttpRequestMessage(
            HttpMethod.Post,
            EngineHub.Path.TrimStart('/') + "/negotiate?negotiateVersion=1"
        );
        negotiate.Headers.Add("Origin", "https://evil.example");
        using var refused = await browser.SendAsync(negotiate, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var signIn = new HttpRequestMessage(HttpMethod.Post, AuthPaths.SignIn)
        {
            Content = JsonContent.Create(
                new SignInRequest(OperatorGatewayFactory.Name, OperatorGatewayFactory.Password),
                EngineJsonContext.Default.SignInRequest
            ),
        };
        signIn.Headers.Add("Origin", "https://evil.example");
        using var refusedSignIn = await browser.SendAsync(signIn, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refusedSignIn.StatusCode);

        await using var ownPage = gateway.CreateRemote(origin: "http://localhost");
        await ownPage.StartAsync(Ct); // the gateway's own origin is fine
    }

    [Fact]
    public async Task WithAnOperatorSetUpAllowSettingsChangesIsIgnored()
    {
        await using var gateway = new TrustedOperatorGatewayFactory();
        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);

        var settings = await viewer.GetSettingsAsync(Ct);
        Assert.True(settings.ReadOnly);
        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.SaveSettingsAsync(settings, Ct));
        Assert.Equal(EngineErrorCodes.SettingsReadOnly, refused.EngineError.Code);
    }

    [Fact]
    public void SessionsEndWhenUnusedFor12HoursOrAfter7Days()
    {
        var folder = Directory.CreateTempSubdirectory("aprsmap-sessions-").FullName;
        try
        {
            var options = new OptionsMonitorStub(new GatewayOptions());
            var store = new OperatorStore(folder, options);
            OperatorStore.Save(
                store.FilePath,
                [new OperatorRecord("op", PasswordHashing.Hash("long enough pass", 1000))]
            );
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T12:00:00Z", null));
            using var sessions = new OperatorSessions(store, time);

            var idle = sessions.Start(store.Find("op")!);
            time.Advance(OperatorSessions.IdleTimeout - TimeSpan.FromMinutes(1));
            Assert.NotNull(sessions.Use(idle.Id)); // used: another 12 hours
            time.Advance(OperatorSessions.IdleTimeout);
            Assert.Null(sessions.Use(idle.Id));

            var busy = sessions.Start(store.Find("op")!);
            for (var hours = 0; hours < 7 * 24; hours += 6)
            {
                Assert.NotNull(sessions.Use(busy.Id));
                time.Advance(TimeSpan.FromHours(6));
            }
            Assert.Null(sessions.Use(busy.Id)); // 7 days at most, however much it's used
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class OptionsMonitorStub(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;

        public GatewayOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }
}
