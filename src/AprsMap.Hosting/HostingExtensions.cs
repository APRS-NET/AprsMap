// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AprsMap.Hosting;

/// <summary>Wiring every AprsMap host shares, so the desktop app and the gateway behave the same.</summary>
public static class HostingExtensions
{
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
    /// Exports logs, metrics and traces over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; otherwise does
    /// nothing, so a default install sends no telemetry anywhere. Standard <c>OTEL_*</c> variables configure the rest.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="component">The component name, appended to the product slug for <c>service.name</c>.</param>
    /// <param name="tracing">Extra tracing setup, such as ASP.NET Core instrumentation in the gateway.</param>
    /// <param name="metrics">Extra metrics setup.</param>
    public static IHostApplicationBuilder AddAprsMapTelemetry(
        this IHostApplicationBuilder builder,
        string component,
        Action<TracerProviderBuilder>? tracing = null,
        Action<MeterProviderBuilder>? metrics = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!IsOtlpConfigured(builder.Configuration))
        {
            return builder;
        }

        var serviceName = $"{Brand.Slug}-{component}";
        builder
            .Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: Brand.Version))
            .WithMetrics(m =>
            {
                m.AddMeter(EngineTelemetry.Name).AddRuntimeInstrumentation().AddHttpClientInstrumentation();
                metrics?.Invoke(m);
            })
            .WithTracing(t =>
            {
                t.AddSource(EngineTelemetry.Name).AddHttpClientInstrumentation();
                tracing?.Invoke(t);
            })
            .WithLogging(
                configureBuilder: null,
                configureOptions: o =>
                {
                    o.IncludeFormattedMessage = true;
                    o.IncludeScopes = true;
                }
            )
            .UseOtlpExporter();
        return builder;
    }

    /// <summary>Whether an OTLP endpoint is configured (environment variable or configuration key).</summary>
    public static bool IsOtlpConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration?[OtlpEndpointVariable]);
}
