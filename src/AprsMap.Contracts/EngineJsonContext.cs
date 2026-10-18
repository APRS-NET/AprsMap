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
[JsonSerializable(typeof(IReadOnlyList<PacketView>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<TncTypeInfo>))]
[JsonSerializable(typeof(TrafficEntry))]
[JsonSerializable(typeof(DestinationInfo))]
[JsonSerializable(typeof(BulletinView))]
[JsonSerializable(typeof(CaptureQuery))]
[JsonSerializable(typeof(CaptureView))]
[JsonSerializable(typeof(CaptureExport))]
[JsonSerializable(typeof(CaptureExportResult))]
[JsonSerializable(typeof(TncCommand))]
[JsonSerializable(typeof(TestSendResult))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(IReadOnlyList<ChatMessage>))]
[JsonSerializable(typeof(IReadOnlyList<Conversation>))]
[JsonSerializable(typeof(SendVia))]
[JsonSerializable(typeof(MessageRetries))]
[JsonSerializable(typeof(PacketFilter))]
[JsonSerializable(typeof(StationBatch))]
[JsonSerializable(typeof(StationDetail))]
[JsonSerializable(typeof(RouteQuery))]
[JsonSerializable(typeof(TelemetryView))]
[JsonSerializable(typeof(IReadOnlyList<NetworkLink>))]
[JsonSerializable(typeof(Dashboard))]
[JsonSerializable(typeof(DashboardRange))]
[JsonSerializable(typeof(ClearCounts))]
[JsonSerializable(typeof(StationFilter))]
[JsonSerializable(typeof(Toggle))]
[JsonSerializable(typeof(ViewFacets))]
[JsonSerializable(typeof(ClearScope))]
[JsonSerializable(typeof(PacketRoute))]
[JsonSerializable(typeof(IReadOnlyList<PacketRoute>))]
[JsonSerializable(typeof(StationQuery))]
[JsonSerializable(typeof(MapViewReport))]
[JsonSerializable(typeof(StationPage))]
[JsonSerializable(typeof(IReadOnlyList<TileProviderInfo>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GeocodingInfo))]
[JsonSerializable(typeof(EngineError))]
[JsonSerializable(typeof(IReadOnlyList<ServerPreset>))]
[JsonSerializable(typeof(LocatorInfo))]
[JsonSerializable(typeof(IReadOnlyList<GeocodeResult>))]
public sealed partial class EngineJsonContext : JsonSerializerContext;
