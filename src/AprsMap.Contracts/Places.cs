// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>An APRS-IS server a user can pick by region, from APRS.NET's list of aprs2.net rotates.</summary>
/// <param name="Key">Stable key (<c>rotate</c>, <c>noam</c>…); the UI looks up a translated region name with it.</param>
/// <param name="Name">The region name in English, for when there is no translation.</param>
/// <param name="Server">The server as <c>host:port</c>, the form <see cref="StationProfile.Server"/> stores.</param>
public sealed record ServerPreset(string Key, string Name, string Server);

/// <summary>A Maidenhead locator and the position the UI uses for it: the centre of its square.</summary>
/// <param name="Locator">The locator in its written form, for example <c>FN31pr</c>.</param>
/// <param name="Latitude">The centre's latitude in degrees.</param>
/// <param name="Longitude">The centre's longitude in degrees.</param>
public sealed record LocatorInfo(string Locator, double Latitude, double Longitude);
