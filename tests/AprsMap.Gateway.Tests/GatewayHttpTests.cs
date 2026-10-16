// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AprsMap.Gateway.Tests;

/// <summary>A gateway behind a proxy: under <c>/aprs</c>, trusting 10.0.0.2, with a small browser app to serve.</summary>
public sealed class ProxiedGatewayFactory : GatewayFactory
{
    public const string HashedFile = "_framework/app.0123456789.wasm";

    public string WebRoot { get; } = Path.Combine(Path.GetTempPath(), "aprsmap-www-" + Guid.NewGuid().ToString("N"));

    /// <summary>The client address each request seemed to come from, after forwarded headers.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string?> SeenFrom { get; } = new();

    public ProxiedGatewayFactory()
    {
        Directory.CreateDirectory(Path.Combine(WebRoot, "_framework"));
        File.WriteAllText(Path.Combine(WebRoot, "index.html"), "<html>app</html>");
        File.WriteAllBytes(Path.Combine(WebRoot, HashedFile), [0, 0x61, 0x73, 0x6d]);
        File.WriteAllBytes(Path.Combine(WebRoot, HashedFile + ".br"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(WebRoot, "_framework", "dotnet.js"), "export {}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder
            .UseSetting(WebHostDefaults.WebRootKey, WebRoot)
            .UseSetting("Gateway:PathBase", "/aprs")
            .UseSetting("Gateway:KnownProxies:0", "10.0.0.2")
            .ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(new RecordClient(SeenFrom)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(WebRoot))
        {
            Directory.Delete(WebRoot, recursive: true);
        }
    }

    // Records the client address the app ends up with, at the end of the pipeline.
    private sealed class RecordClient(System.Collections.Concurrent.ConcurrentQueue<string?> seen) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                next(app);
                app.Run(context =>
                {
                    seen.Enqueue(context.Connection.RemoteIpAddress?.ToString());
                    return Task.CompletedTask;
                });
            };
    }
}

public sealed class GatewayHttpTests(ProxiedGatewayFactory factory) : IClassFixture<ProxiedGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnderAPathBaseTheAppAndHealthCheckAnswerThereAndNotAtTheRoot()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/aprs/healthz", Ct)).StatusCode);
        Assert.Equal("<html>app</html>", await client.GetStringAsync("/aprs/", Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/healthz", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/", Ct)).StatusCode);
    }

    [Fact]
    public async Task EveryResponseCarriesTheSecurityHeaders()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/aprs/", Ct);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'self'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task HashedFilesAreImmutableAndTheRestRevalidate()
    {
        using var client = factory.CreateClient();

        var hashed = await client.GetAsync("/aprs/" + ProxiedGatewayFactory.HashedFile, Ct);
        var boot = await client.GetAsync("/aprs/_framework/dotnet.js", Ct);
        var index = await client.GetAsync("/aprs/", Ct);

        Assert.Equal("public, max-age=31536000, immutable", hashed.Headers.CacheControl!.ToString());
        Assert.True(boot.Headers.CacheControl!.NoCache);
        Assert.True(index.Headers.CacheControl!.NoCache);
    }

    [Fact]
    public async Task ABrowserThatTakesBrotliGetsThePrecompressedCopy()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/aprs/" + ProxiedGatewayFactory.HashedFile);
        request.Headers.AcceptEncoding.ParseAdd("gzip, br");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal("br", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal("application/wasm", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Contains("Accept-Encoding", response.Headers.Vary);

        var plain = await client.GetByteArrayAsync("/aprs/" + ProxiedGatewayFactory.HashedFile, Ct);
        Assert.Equal([0, 0x61, 0x73, 0x6d], plain);
    }

    [Theory]
    [InlineData("10.0.0.2", "203.0.113.7")] // a known proxy: its forwarded address counts
    [InlineData("10.0.0.9", "10.0.0.9")] // anyone else: ignored
    public async Task ForwardedHeadersAreHonouredFromKnownProxiesOnly(string proxy, string seen)
    {
        await factory.Server.SendAsync(
            context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(proxy);
                context.Request.Path = "/aprs/nothing-here";
                context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
            },
            Ct
        );

        Assert.Contains(seen, factory.SeenFrom);
    }
}
