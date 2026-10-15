// SPDX-License-Identifier: AGPL-3.0-only
namespace AprsMap.UI.Platform;

/// <summary>Where the viewer's device is, from the browser's geolocation API (desktop: after F3.6).</summary>
/// <param name="Latitude">Latitude in degrees.</param>
/// <param name="Longitude">Longitude in degrees.</param>
/// <param name="AccuracyMetres">How far off it may be, when the device says.</param>
public sealed record DeviceFix(double Latitude, double Longitude, double? AccuracyMetres);

/// <summary>Why the device couldn't give its location.</summary>
public enum DeviceLocationError
{
    /// <summary>The user (or the browser's settings) refused.</summary>
    Denied,

    /// <summary>The device couldn't work out where it is.</summary>
    Unavailable,

    /// <summary>It took too long.</summary>
    Timeout,

    /// <summary>The page isn't a secure context (https or localhost), so the browser won't share its location.</summary>
    Insecure,

    /// <summary>This browser has no geolocation.</summary>
    Unsupported,
}

/// <summary>Thrown by <see cref="IDeviceLocation.GetAsync"/> with the reason.</summary>
public sealed class DeviceLocationException(DeviceLocationError error, string? detail = null)
    : Exception(detail ?? error.ToString())
{
    /// <summary>Why the location couldn't be had.</summary>
    public DeviceLocationError Error { get; } = error;
}

/// <summary>
/// The viewer's own location (docs/features/F0.5-first-run-wizard.md, behavior 9.4). A platform service each app
/// supplies, like the clipboard, not an engine capability: on the web it's the browser's location, which may not be the
/// station's. Null where there is none, and the wizard then doesn't offer it.
/// </summary>
public interface IDeviceLocation
{
    /// <summary>Asks for the location; the platform may ask the user first.</summary>
    /// <exception cref="DeviceLocationException">No location; <see cref="DeviceLocationException.Error"/> says why.</exception>
    Task<DeviceFix> GetAsync(CancellationToken cancellationToken = default);
}
