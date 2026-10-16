// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Gateway;
using AprsMap.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder
    .AddAprsMapEngine()
    .AddAprsMapTiles()
    .AddAprsMapTelemetry(
        "gateway",
        tracing: t => t.AddAspNetCoreInstrumentation(),
        metrics: m => m.AddAspNetCoreInstrumentation()
    );
builder
    .Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, EngineJsonContext.Default));
builder.Services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.SectionName);
var gateway = GatewayHttp.Options(builder.Configuration);
builder.Services.AddGatewayProxies(gateway);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseGatewayFront(gateway);

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
