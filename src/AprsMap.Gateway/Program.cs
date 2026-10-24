// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Hosting;
using AprsMap.Server;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// aprsmap-gateway operator add|password|remove|list NAME: sets up who may sign in (F2.9 behavior 1), then exits.
if (args is ["operator", .. var command])
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();
    return OperatorCommand.Run(command, GatewayData.Folder(configuration), OperatorCommand.AskPassword, Console.Out);
}

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetValue<int?>("Gateway:OperatorPort") is { } operatorPort)
{
    // The operator port listens beside the main one (F1.26 behavior 2).
    var ports = builder.Configuration["HTTP_PORTS"] is { Length: > 0 } main ? main : "8080";
    builder.WebHost.UseSetting(
        "HTTP_PORTS",
        string.Join(';', ports, operatorPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
    );
}
builder
    .AddAprsMapEngine()
    .AddAprsMapTiles()
    .AddAprsMapTelemetry(
        "gateway",
        tracing: t => t.AddAspNetCoreInstrumentation(),
        metrics: m => m.AddAspNetCoreInstrumentation().AddMeter(Viewers.MeterName),
        logFile: false // its log is the container's output unless the operator turns the file on (F1.32 behavior 7)
    );
builder.Services.AddSingleton<Viewers>();
builder.Services.AddSingleton<ViewerFilter>();
builder
    .Services.AddSignalR(o =>
    {
        o.AddFilter<ViewerFilter>();
        o.AddFilter<OperatorFilter>();
        o.MaximumReceiveMessageSize = 64 * 1024; // settings are the largest call (F1.14 behavior 5)
    })
    .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, EngineJsonContext.Default));
builder.Services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.SectionName);

// A gateway is told an update is ready when its image is pushed (F4.15 behavior 4.3).
builder.Services.PostConfigure<AprsMap.Core.EngineOptions>(o => o.Updates.Kind = AprsMap.Core.InstallKind.Gateway);
var gateway = GatewayHttp.Options(builder.Configuration);
builder.Services.AddGatewayProxies(gateway);
builder.Services.AddHealthChecks();
builder.Services.AddOperatorSignIn(GatewayData.Folder(builder.Configuration));

var app = builder.Build();
app.UseGatewayFront(gateway);
app.UseOperatorSignIn();
app.UseViewerGate();

// The browser app (AprsMap.Browser's publish output) is copied into wwwroot by the Dockerfile.
app.UseBrowserApp();

app.MapHub<EngineHubEndpoint>(EngineHub.Path);
app.MapOperatorSignIn();
app.MapTiles();
app.MapHealthChecks("/healthz");

// AGPL-3.0 section 13: users interacting over a network are offered the corresponding source.
app.MapGet("/source", () => Results.Redirect(Brand.SourceUrl));

app.Run();
return 0;

/// <summary>Entry point; public so integration tests can host the gateway in memory.</summary>
public partial class Program;
