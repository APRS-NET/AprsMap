// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// A tile provider the map can use (docs/features/F1.4-map.md, behavior 11), as the desktop app or the gateway
/// configures it. The URL isn't included: the UI asks its tile source for tiles by provider key.
/// </summary>
/// <param name="Key">The provider's key, as configured (for example <c>osm</c>).</param>
/// <param name="Name">What the viewer sees in Settings › Map.</param>
/// <param name="Attribution">The text the provider requires on the map (for example "© OpenStreetMap contributors").</param>
/// <param name="AttributionUrl">Where the attribution links to.</param>
/// <param name="MinZoom">The lowest zoom level the provider serves.</param>
/// <param name="MaxZoom">The highest zoom level the provider serves.</param>
/// <param name="ImageType">The tiles' media type, such as <c>image/png</c>.</param>
public sealed record TileProviderInfo(
    string Key,
    string Name,
    string Attribution,
    string? AttributionUrl,
    int MinZoom,
    int MaxZoom,
    string ImageType
);
