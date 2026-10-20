// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Core;
using AprsMap.Tiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AprsMap.Hosting;

/// <summary>Wiring every AprsMap host shares, so the desktop app and the gateway behave the same.</summary>
public static class HostingExtensions
{
    /// <summary>
    /// APRS.NET's meters and activity sources, by wildcard: today <c>AprsNet.AprsIsTcpClient</c> (lines received,
    /// connection attempts, login results, dropped lines, idle timeouts), and any APRS.NET package added later.
    /// </summary>
    public const string AprsNetTelemetry = "AprsNet.*";

    /// <summary>The environment variable that switches OpenTelemetry export on (same convention as APRS.NET).</summary>
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Adds the engine, bound to the <c>Engine</c> and <c>Geocoding</c> configuration sections.</summary>
    public static IHostApplicationBuilder AddAprsMapEngine(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddAprsMapEngine();
        builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection(EngineOptions.SectionName));
        builder.Services.Configure<GeocodingOptions>(builder.Configuration.GetSection(GeocodingOptions.SectionName));
        return builder;
    }

    /// <summary>
    /// Adds the map's tile service (docs/features/F1.4-map.md), bound to the <c>Tiles</c> configuration section: in
    /// process on the desktop, behind <c>/tiles</c> on the gateway.
    /// </summary>
    public static IHostApplicationBuilder AddAprsMapTiles(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddAprsMapTiles();
        builder.Services.Configure<TilesOptions>(builder.Configuration.GetSection(TilesOptions.SectionName));
        // Downloads follow the station's Internet and Map tiles toggles (F1.15).
        builder.Services.AddSingleton<ITileDownloads>(provider => new EngineTileDownloads(
            provider.GetRequiredService<Engine>()
        ));
        // Where tiles are cached, for Help › About (docs/features/F1.31-files.md).
        builder.Services.AddSingleton<IStorageFolderSource>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TilesOptions>>();
            return new StorageFolderSource(() =>
            {
                var configured = options.Value.CacheFolder;
                return
                [
                    StorageFolderSource.At(
                        StorageFolderKind.Tiles,
                        string.IsNullOrWhiteSpace(configured) ? TileLocation.UserFolder(Brand.Slug) : configured,
                        !string.IsNullOrWhiteSpace(configured)
                    ),
                ];
            });
        });
        return builder;
    }

    /// <summary>Tile downloads as the engine's toggles allow (F1.15).</summary>
    private sealed class EngineTileDownloads(Engine engine) : ITileDownloads
    {
        public bool Allowed => engine.DownloadsTiles;
    }

    /// <summary>
    /// Logging and telemetry (docs/features/F1.32-logging.md): the levels from Settings › Logging on top of
    /// <c>appsettings.json</c> (the environment and command line winning), the log file, and OTLP export to the targets
    /// saved in the settings and to <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>. With no target, nothing is sent anywhere.
    /// Targets are read when the app starts; a change to them applies after a restart (behavior 2).
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="component">The component name, appended to the product slug for <c>service.name</c>.</param>
    /// <param name="tracing">Extra tracing setup, such as ASP.NET Core instrumentation in the gateway.</param>
    /// <param name="metrics">Extra metrics setup.</param>
    /// <param name="logFile">Whether the log file is on unless turned off: the desktop, not a gateway (behavior 7).</param>
    public static IHostApplicationBuilder AddAprsMapTelemetry(
        this IHostApplicationBuilder builder,
        string component,
        Action<TracerProviderBuilder>? tracing = null,
        Action<MeterProviderBuilder>? metrics = null,
        bool logFile = true
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        var state = AddLogging(builder, logFile);
        var environment = IsOtlpConfigured(builder.Configuration);
        var targets = state.StartupTargets.Where(t => t.On).ToList();
        bool Any(OtlpSignals signal) => environment || targets.Any(t => t.Signals.HasFlag(signal));
        if (!Any(OtlpSignals.Logs) && !Any(OtlpSignals.Metrics) && !Any(OtlpSignals.Traces))
        {
            return builder;
        }

        var serviceName = $"{Brand.Slug}-{component}";
        var otel = builder
            .Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: Brand.Version));
        if (Any(OtlpSignals.Metrics))
        {
            otel.WithMetrics(m =>
            {
                m.AddMeter(EngineTelemetry.Name, AprsNetTelemetry)
                    .AddRuntimeInstrumentation()
                    .AddHttpClientInstrumentation();
                metrics?.Invoke(m);
                if (environment)
                {
                    m.AddOtlpExporter(); // OTEL_EXPORTER_OTLP_* (behavior 14)
                }
                foreach (var target in targets.Where(t => t.Signals.HasFlag(OtlpSignals.Metrics)))
                {
                    m.AddOtlpExporter(ExporterName(target), o => Apply(o, target, "metrics"));
                }
            });
        }
        if (Any(OtlpSignals.Traces))
        {
            otel.WithTracing(t =>
            {
                t.AddSource(EngineTelemetry.Name, AprsNetTelemetry).AddHttpClientInstrumentation();
                tracing?.Invoke(t);
                if (environment)
                {
                    t.AddOtlpExporter();
                }
                foreach (var target in targets.Where(x => x.Signals.HasFlag(OtlpSignals.Traces)))
                {
                    t.AddOtlpExporter(ExporterName(target), o => Apply(o, target, "traces"));
                }
            });
        }
        if (Any(OtlpSignals.Logs))
        {
            otel.WithLogging(
                l =>
                {
                    if (environment)
                    {
                        l.AddOtlpExporter();
                    }
                    foreach (var target in targets.Where(x => x.Signals.HasFlag(OtlpSignals.Logs)))
                    {
                        l.AddOtlpExporter(ExporterName(target), o => Apply(o, target, "logs"));
                    }
                },
                o =>
                {
                    o.IncludeFormattedMessage = true;
                    o.IncludeScopes = true;
                }
            );
        }
        return builder;
    }

    /// <summary>Each target's exporters get their own options, by name.</summary>
    private static string ExporterName(OtlpTarget target) => "aprsmap-target-" + target.Id;

    /// <summary>
    /// A target's endpoint, protocol and headers (behavior 13). Over HTTP, an endpoint without a path gets the signal's
    /// (<c>/v1/logs</c> and so on), as the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable would.
    /// </summary>
    internal static void Apply(OtlpExporterOptions options, OtlpTarget target, string signal)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(target);
        var http = target.Protocol == OtlpProtocol.HttpProtobuf;
        options.Protocol = http ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        var endpoint = new Uri(target.Endpoint, UriKind.Absolute);
        if (http && endpoint.AbsolutePath is "" or "/")
        {
            endpoint = new Uri(endpoint, "v1/" + signal);
        }
        options.Endpoint = endpoint;
        var headers = target.HeaderList.Where(h => !string.IsNullOrEmpty(h.Value)).ToList();
        if (headers.Count > 0)
        {
            options.Headers = string.Join(',', headers.Select(h => $"{h.Name}={Uri.EscapeDataString(h.Value!)}"));
        }
    }

    /// <summary>
    /// The shared <see cref="LoggingState"/>, with what only the host knows: the stored settings as the app starts (so the
    /// level rules and the file are right from the first line), the default folder, what the environment sets, and the
    /// OTLP targets the app starts with. Adds the level rules and the log file.
    /// </summary>
    private static LoggingState AddLogging(IHostApplicationBuilder builder, bool logFile)
    {
        var engine =
            builder.Configuration.GetSection(EngineOptions.SectionName).Get<EngineOptions>() ?? new EngineOptions();
        var state = new LoggingState
        {
            DefaultFolder = Path.Combine(SettingsLocation.DataFolder(engine), "logs"),
            FileOnByDefault = logFile,
        };
        try
        {
            var stored = SettingsLocation
                .CreateStore(engine)
                .LoadAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            state.Update(stored?.EffectiveLogging ?? LoggingSettings.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Unreadable settings: the engine says so when it loads them; logging starts from the defaults.
        }
        state.StartupTargets = state.Current.TargetList;
        state.EnvironmentEndpoint = IsOtlpConfigured(builder.Configuration)
            ? builder.Configuration[OtlpEndpointVariable]
            : null;
        state.EnvironmentLevel = SetOutside(builder.Configuration, "Logging:LogLevel:Default");
        state.EnvironmentAreas =
        [
            .. Enum.GetValues<LogArea>()
                .Where(a =>
                    LogAreas.Categories(a).Any(c => SetOutside(builder.Configuration, "Logging:LogLevel:" + c))
                ),
        ];

        builder.Services.RemoveAll<LoggingState>();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton<IConfigureOptions<LoggerFilterOptions>>(new LevelRules(state));
        builder.Services.AddSingleton<IOptionsChangeTokenSource<LoggerFilterOptions>>(new LevelRuleChanges(state));
        // The log folder, for Help › About, while the log file is on (docs/features/F1.31-files.md, behavior 5).
        builder.Services.AddSingleton<IStorageFolderSource>(
            new StorageFolderSource(() =>
                state.FileOn
                    ?
                    [
                        StorageFolderSource.At(
                            StorageFolderKind.Logs,
                            state.Folder,
                            !string.IsNullOrWhiteSpace(state.Current.EffectiveFile.Folder)
                        ),
                    ]
                    : []
            )
        );
        builder.Services.AddSingleton<ILoggerProvider>(sp => new FileLoggerProvider(
            state,
            sp.GetService<TimeProvider>() ?? TimeProvider.System
        ));
        return state;
    }

    /// <summary>Whether the environment or the command line sets <paramref name="key"/>, which then wins (behavior 1).</summary>
    private static bool SetOutside(IConfiguration configuration, string key) =>
        configuration is IConfigurationRoot root
        && root.Providers.Any(p =>
            p is EnvironmentVariablesConfigurationProvider or CommandLineConfigurationProvider && p.TryGet(key, out _)
        );

    /// <summary>
    /// The overall level and each area's as .NET level rules (behaviors 3, 4), added after <c>appsettings.json</c>'s so
    /// they win over it, and left out where the environment sets the level, so it wins over them.
    /// </summary>
    private sealed class LevelRules(LoggingState state) : IConfigureOptions<LoggerFilterOptions>
    {
        public void Configure(LoggerFilterOptions options)
        {
            var settings = state.Current;
            if (!state.EnvironmentLevel)
            {
                options.Rules.Add(new LoggerFilterRule(null, null, LogAreas.ToLogLevel(settings.Level), null));
            }
            foreach (var area in Enum.GetValues<LogArea>())
            {
                if (settings.OwnLevel(area) is not { } level || state.EnvironmentAreas.Contains(area))
                {
                    continue; // same as overall, or the environment's
                }
                foreach (var category in LogAreas.Categories(area))
                {
                    options.Rules.Add(new LoggerFilterRule(null, category, LogAreas.ToLogLevel(level), null));
                }
            }
        }
    }

    /// <summary>Tells .NET's logging the rules changed, so a new level applies at once, without a restart (behavior 2).</summary>
    private sealed class LevelRuleChanges : IOptionsChangeTokenSource<LoggerFilterOptions>, IDisposable
    {
        private CancellationTokenSource _changed = new();

        public LevelRuleChanges(LoggingState state) =>
            // The old source is cancelled, not disposed: .NET's logging may still be registering on its token.
            state.Changed += (_, _) => Interlocked.Exchange(ref _changed, new CancellationTokenSource()).Cancel();

        public string Name => Options.DefaultName;

        public IChangeToken GetChangeToken() => new CancellationChangeToken(Volatile.Read(ref _changed).Token);

        public void Dispose() => _changed.Dispose();
    }

    /// <summary>Whether an OTLP endpoint is configured (environment variable or configuration key).</summary>
    public static bool IsOtlpConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration?[OtlpEndpointVariable]);
}
