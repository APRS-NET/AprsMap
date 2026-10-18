// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Http.Json;
using AprsMap.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AprsMap.Gateway.Tests;

/// <summary>A gateway with an operator, an operator port (8081) and a title.</summary>
public sealed class OperatorPortGatewayFactory : OperatorGatewayFactory
{
    public const int OperatorPort = 8081;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder
            .UseSetting(
                "Gateway:OperatorPort",
                OperatorPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            )
            .UseSetting("Gateway:Title", "What N0GATE hears")
            .ConfigureTestServices(s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, LocalPortFilter>());
    }
}

/// <summary>The public and operator ports (docs/features/F1.26-public-console.md, behaviors 1–4, 10; F2.9 behavior 8.2).</summary>
public sealed class OperatorPortTests(OperatorPortGatewayFactory gateway) : IClassFixture<OperatorPortGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> SignIn(int? port)
    {
        using var browser = gateway.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthPaths.SignIn)
        {
            Content = JsonContent.Create(
                new SignInRequest(OperatorGatewayFactory.Name, OperatorGatewayFactory.Password),
                EngineJsonContext.Default.SignInRequest
            ),
        };
        if (port is { } p)
        {
            request.Headers.Add(LocalPortFilter.Header, p.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return await browser.SendAsync(request, Ct);
    }

    private async Task<AuthInfo> Me(string? cookie, int? port)
    {
        using var browser = gateway.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthPaths.Me);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }
        if (port is { } p)
        {
            request.Headers.Add(LocalPortFilter.Header, p.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        using var response = await browser.SendAsync(request, Ct);
        return (await response.Content.ReadFromJsonAsync(EngineJsonContext.Default.AuthInfo, Ct))!;
    }

    [Fact]
    public async Task ThePublicPortNeverOffersSignIn()
    {
        using var refused = await SignIn(port: null);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(
            EngineErrorCodes.AuthNotOfferedHere,
            (await refused.Content.ReadFromJsonAsync(EngineJsonContext.Default.EngineError, Ct))!.Code
        );
        Assert.Equal(AuthInfo.Viewer, await Me(null, port: null));
        Assert.True((await Me(null, OperatorPortGatewayFactory.OperatorPort)).SignInOffered);
    }

    [Fact]
    public async Task ASessionFromTheOperatorPortIsIgnoredOnThePublicPort()
    {
        using var signedIn = await SignIn(OperatorPortGatewayFactory.OperatorPort);
        Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);
        var cookie = signedIn.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        Assert.True((await Me(cookie, OperatorPortGatewayFactory.OperatorPort)).Operator);
        Assert.False((await Me(cookie, port: null)).Operator);

        await using var publicPort = gateway.CreateRemote(cookie);
        await publicPort.StartAsync(Ct);
        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => publicPort.SendPositionAsync(null, Ct));
        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);

        await using var operatorPort = gateway.CreateRemote(cookie, localPort: OperatorPortGatewayFactory.OperatorPort);
        await operatorPort.StartAsync(Ct);
        var engineSays = await Assert.ThrowsAnyAsync<Exception>(() => operatorPort.SendPositionAsync(null, Ct));
        Assert.NotEqual(EngineErrorCodes.OperatorOnly, (engineSays as IHasEngineError)?.EngineError.Code);
    }

    [Fact]
    public async Task TheTitleComesWithTheStatus()
    {
        await using var remote = gateway.CreateRemote();
        await remote.StartAsync(Ct);

        Assert.Equal("What N0GATE hears", (await remote.GetStatusAsync(Ct)).Title);
    }
}
