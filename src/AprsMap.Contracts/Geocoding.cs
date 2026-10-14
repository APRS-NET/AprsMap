// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>One match from an address or postal-code lookup.</summary>
/// <param name="DisplayName">The place as the lookup service names it, in the requested language.</param>
/// <param name="Latitude">Latitude in degrees.</param>
/// <param name="Longitude">Longitude in degrees.</param>
public sealed record GeocodeResult(string DisplayName, double Latitude, double Longitude);

/// <summary>Whether address lookup is available to this caller, and where queries go (shown before the first lookup).</summary>
/// <param name="Enabled">False when lookup is turned off, or this caller may not use it (a gateway without operator sign-in).</param>
/// <param name="Host">The lookup server's host name, for the "your text is sent to …" notice; null when disabled.</param>
/// <param name="UsagePolicyUrl">The server's usage policy, linked from that notice; null when there is none to show.</param>
public sealed record GeocodingInfo(bool Enabled, string? Host, string? UsagePolicyUrl)
{
    /// <summary>Lookup is not available.</summary>
    public static GeocodingInfo Disabled { get; } = new(false, null, null);
}
