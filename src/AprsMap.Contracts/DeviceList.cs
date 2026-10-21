// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>How often the device list is checked for a newer one (docs/features/F4.16-device-list.md, behavior 4).</summary>
public enum DeviceListSchedule
{
    /// <summary>Never by itself; Update now still checks.</summary>
    Off,

    /// <summary>Once a day.</summary>
    Daily,

    /// <summary>Once a week (the default, decision 2).</summary>
    Weekly,
}

/// <summary>The device list's settings (docs/features/F4.16-device-list.md, behaviors 4, 10).</summary>
/// <param name="Schedule">How often to check; weekly by default.</param>
/// <param name="Url">
/// Where to fetch it from, on the desktop (Settings › Data › Advanced): null for the configured address (the APRS
/// Foundation's by default), empty to never download. A gateway ignores it: its address is the environment's.
/// </param>
/// <param name="LastChecked">When the last check ran, so a restart doesn't check again. Set by the engine.</param>
/// <param name="ETag">The downloaded list's entity tag, so an unchanged list costs one <c>304</c>. Set by the engine.</param>
public sealed record DeviceListSettings(
    DeviceListSchedule Schedule = DeviceListSchedule.Weekly,
    string? Url = null,
    DateTimeOffset? LastChecked = null,
    string? ETag = null
)
{
    /// <summary>Weekly, from the configured address.</summary>
    public static DeviceListSettings Default { get; } = new();

    /// <summary>How long a check waits for the next, by <see cref="Schedule"/>; null when off.</summary>
    public TimeSpan? Interval =>
        Schedule switch
        {
            DeviceListSchedule.Daily => TimeSpan.FromDays(1),
            DeviceListSchedule.Weekly => TimeSpan.FromDays(7),
            _ => null,
        };
}

/// <summary>Which list names stations' equipment (docs/features/F4.16-device-list.md, behaviors 1, 2).</summary>
public enum DeviceListOrigin
{
    /// <summary>The one built into APRS.NET.</summary>
    BuiltIn,

    /// <summary>One downloaded and checked, newer than the built-in one.</summary>
    Downloaded,
}

/// <summary>The device list in use and its checks (docs/features/F4.16-device-list.md, behaviors 9, 11, 12).</summary>
/// <param name="Origin">Built in or downloaded.</param>
/// <param name="GeneratedAt">When the list in use was generated; null when it doesn't say.</param>
/// <param name="Commit">The aprs-deviceid commit it came from, when known (the built-in list's).</param>
/// <param name="Tocalls">How many TOCALLs and patterns it has.</param>
/// <param name="LastChecked">When the last check ran; null before the first.</param>
/// <param name="LastError">Why the last check found nothing usable; null when it worked or found it unchanged.</param>
/// <param name="CanDownload">Whether an address is set: false hides Update now and the schedule (behavior 10).</param>
/// <param name="Url">Where it's fetched from; null when it isn't.</param>
/// <param name="Checking">Whether a check is running.</param>
/// <param name="UrlIsSetting">Whether the address can be changed in Settings (the desktop), not only in configuration.</param>
public sealed record DeviceListStatus(
    DeviceListOrigin Origin,
    DateTimeOffset? GeneratedAt,
    string? Commit,
    int Tocalls,
    DateTimeOffset? LastChecked = null,
    EngineError? LastError = null,
    bool CanDownload = true,
    string? Url = null,
    bool Checking = false,
    bool UrlIsSetting = false
);
