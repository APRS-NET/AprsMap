// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Gateway;
using AprsMap.Hosting;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder
    .AddAprsMapEngine()
    .AddAprsMapTiles()
    .AddAprsMapTelemetry(
        "gateway",
        tracing: t => t.AddAspNetCoreInstrumentation(),
        metrics: m => m.AddAspNetCoreInstrumentation().AddMeter(Viewers.MeterName)
    );
builder.Services.AddSingleton<Viewers>();
builder.Services.AddSingleton<ViewerFilter>();
builder
    .Services.AddSignalR(o =>
    {
        o.AddFilter<ViewerFilter>();
        o.MaximumReceiveMessageSize = 64 * 1024; // settings are the largest call (F1.14 behavior 5)
    })
    .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, EngineJsonContext.Default));
builder.Services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.SectionName);
var gateway = GatewayHttp.Options(builder.Configuration);
builder.Services.AddGatewayProxies(gateway);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseGatewayFront(gateway);
app.UseViewerGate();

// The browser app (AprsMap.Browser's publish output) is copied into wwwroot by the Dockerfile.
app.UseBrowserApp();

app.MapHub<EngineHubEndpoint>(EngineHub.Path);
app.MapTiles();
app.MapHealthChecks("/healthz");

// AGPL-3.0 section 13: users interacting over a network are offered the corresponding source.
app.MapGet("/source", () => Results.Redirect(Brand.SourceUrl));

app.Run();

/// <summary>Entry point; public so integration tests can host the gateway in memory.</summary>
public partial class Program;
