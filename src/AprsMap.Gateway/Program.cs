// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Gateway;
using AprsMap.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder
    .AddAprsMapEngine()
    .AddAprsMapTelemetry(
        "gateway",
        tracing: t => t.AddAspNetCoreInstrumentation(),
        metrics: m => m.AddAspNetCoreInstrumentation()
    );
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();

var app = builder.Build();

// The browser app (AprsMap.Browser's publish output) is copied into wwwroot by the Dockerfile.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".dat"] = "application/octet-stream";
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.MapHub<EngineHubEndpoint>(EngineHub.Path);
app.MapHealthChecks("/healthz");

// AGPL-3.0 section 13: users interacting over a network are offered the corresponding source.
app.MapGet("/source", () => Results.Redirect(Brand.SourceUrl));

app.Run();

/// <summary>Entry point; public so integration tests can host the gateway in memory.</summary>
public partial class Program;
