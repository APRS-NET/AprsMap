// SPDX-License-Identifier: MIT
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AprsMap.Contracts;

/// <summary>
/// Source-generated JSON metadata for every type that crosses <see cref="EngineHub"/>. Published WebAssembly builds turn
/// off reflection-based System.Text.Json, so the browser app can only serialize types listed here. Add each new
/// <see cref="IEngineService"/> parameter, return and stream item type (a test checks this). The generated code is
/// excluded from coverage: it is the source generator's, not ours, and its branches would swamp the gate.
/// </summary>
[ExcludeFromCodeCoverage]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EngineStatus))]
[JsonSerializable(typeof(PacketView))]
[JsonSerializable(typeof(StationBatch))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GeocodingInfo))]
[JsonSerializable(typeof(EngineError))]
[JsonSerializable(typeof(IReadOnlyList<GeocodeResult>))]
public sealed partial class EngineJsonContext : JsonSerializerContext;
