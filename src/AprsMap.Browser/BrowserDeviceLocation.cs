// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using AprsMap.UI.Platform;

namespace AprsMap.Browser;

/// <summary>This browser's location, from the geolocation API (getPosition in main.js).</summary>
internal sealed partial class BrowserDeviceLocation : IDeviceLocation
{
    /// <inheritdoc />
    public async Task<DeviceFix> GetAsync(CancellationToken cancellationToken = default)
    {
        string fix;
        try
        {
            fix = await GetPosition().WaitAsync(cancellationToken);
        }
        catch (JSException ex)
        {
            throw new DeviceLocationException(Reason(ex.Message), ex.Message);
        }
        return Parse(fix);
    }

    /// <summary>"lat,lon,accuracy" from main.js; accuracy may be empty.</summary>
    internal static DeviceFix Parse(string fix)
    {
        var parts = fix.Split(',');
        double Read(int i) => double.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
        return new DeviceFix(Read(0), Read(1), parts.Length > 2 && parts[2].Length > 0 ? Read(2) : null);
    }

    /// <summary>The reason from main.js's "geo:…" rejection.</summary>
    internal static DeviceLocationError Reason(string message) =>
        message switch
        {
            _ when message.Contains("geo:denied", StringComparison.Ordinal) => DeviceLocationError.Denied,
            _ when message.Contains("geo:timeout", StringComparison.Ordinal) => DeviceLocationError.Timeout,
            _ when message.Contains("geo:insecure", StringComparison.Ordinal) => DeviceLocationError.Insecure,
            _ when message.Contains("geo:unsupported", StringComparison.Ordinal) => DeviceLocationError.Unsupported,
            _ => DeviceLocationError.Unavailable,
        };

    [JSImport("getPosition", "main.js")]
    private static partial Task<string> GetPosition();
}
