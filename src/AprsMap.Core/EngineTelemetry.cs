// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AprsMap.Core;

/// <summary>
/// The engine's traces and metrics, published through System.Diagnostics. The hosts (desktop app, gateway) decide
/// whether to export them with OpenTelemetry; the engine itself has no exporter dependency.
/// </summary>
public static class EngineTelemetry
{
    /// <summary>The name of the engine's <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string Name = "AprsMap.Core";

    internal static readonly ActivitySource ActivitySource = new(Name);

    internal static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> PacketsReceived = Meter.CreateCounter<long>(
        "aprsmap.packets.received",
        unit: "{packet}",
        description: "APRS packets received, tagged by packet type."
    );

    internal static readonly Counter<long> ConnectionFailures = Meter.CreateCounter<long>(
        "aprsmap.connection.failures",
        unit: "{failure}",
        description: "Failed APRS-IS connection attempts."
    );
}
