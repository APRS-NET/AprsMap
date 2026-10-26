// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Gateway;
using AprsMap.Hosting;
using AprsMap.Server;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// aprsmap-gateway operator add|password|remove|list NAME: sets up who may sign in (F2.9 behavior 1), then exits.
if (args is ["operator", .. var command])
{
    var settings = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true);
    if (TomlConfiguration.PathFrom(args, Environment.GetEnvironmentVariable) is { } tomlPath)
    {
        settings.AddToml(tomlPath); // the service's file (F1.37 behavior 1)
    }
    var configuration = settings.AddEnvironmentVariables().Build();
    return OperatorCommand.Run(
        TomlConfiguration.WithoutConfig(command), // --config is the gateway's, not the command's
        GatewayData.Folder(configuration),
        OperatorCommand.AskPassword,
        Console.Out
    );
}

var builder = WebApplication.CreateBuilder(args);

// The TOML file (F1.37 behavior 1): after appsettings.json, before environment variables, so those still win.
if (TomlConfiguration.PathFrom(args, Environment.GetEnvironmentVariable) is { } toml)
{
    TomlConfiguration.InsertAfterJsonFiles(
        ((IConfigurationBuilder)builder.Configuration).Sources,
        TomlConfiguration.Read(File.ReadAllText(toml), toml)
    );
}
if (GatewayListen.Urls(builder.Configuration) is { } urls)
{
    // Where it listens (F1.37 behavior 2): the published app, and the admin app on its own address.
    builder.WebHost.UseUrls([.. urls.Urls]);
    if (urls.OperatorPort is { } adminPort)
    {
        builder.Configuration["Gateway:OperatorPort"] = adminPort.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
    }
}
else if (builder.Configuration.GetValue<int?>("Gateway:OperatorPort") is { } operatorPort)
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

// A gateway is told an update is ready when its image is pushed (F4.15 behavior 4.3); the Linux service, whose unit
// says so, when its tarball is attached, and it updates itself from here (F1.40 behaviors 1, 3).
builder.Services.PostConfigure<AprsMap.Core.EngineOptions>(o =>
{
    if (o.Updates.Kind != AprsMap.Core.InstallKind.Service)
    {
        o.Updates.Kind = AprsMap.Core.InstallKind.Gateway;
    }
    o.Updates.RuntimeId ??= System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
});
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
