// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics.Metrics;
using AprsMap.Core;
using AprsMap.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AprsMap.Gateway.Tests;

public class TelemetryTests
{
    private static (IHost Host, List<Metric> Exported) Build(bool otlp)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        // Never the real user folders: the settings are read here as the host starts (F1.32), so a temp folder.
        builder.Configuration["Engine:SettingsFolder"] = Path.Combine(
            Path.GetTempPath(),
            "aprsmap-telemetry-" + Guid.NewGuid().ToString("N")
        );
        if (otlp)
        {
            // Never reached: the in-memory exporter below is what the test reads.
            builder.Configuration[HostingExtensions.OtlpEndpointVariable] = "http://127.0.0.1:9";
        }
        builder.AddAprsMapTelemetry("test", logFile: false);
        var exported = new List<Metric>();
        builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddInMemoryExporter(exported));
        return (builder.Build(), exported);
    }

    [Theory]
    [InlineData("AprsNet.AprsIsTcpClient", "aprsnet.aprsis.lines.received")]
    [InlineData(EngineTelemetry.Name, "aprsmap.test.counter")]
    public void APRSNETsAndTheEnginesMetricsAreExported(string meterName, string counter)
    {
        var (host, exported) = Build(otlp: true);
        using (host)
        {
            var provider = host.Services.GetRequiredService<MeterProvider>();
            using var meter = new Meter(meterName);
            meter.CreateCounter<long>(counter).Add(3);

            provider.ForceFlush();

            Assert.Contains(exported, m => m.Name == counter);
        }
    }

    [Fact]
    public void WithoutAnOtlpEndpointNothingIsSetUp()
    {
        var (host, _) = Build(otlp: false);
        using (host)
        {
            // AddAprsMapTelemetry added nothing; only the test's own exporter hook exists, with no meters.
            Assert.False(
                HostingExtensions.IsOtlpConfigured(
                    host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
                )
            );
        }
    }
}
