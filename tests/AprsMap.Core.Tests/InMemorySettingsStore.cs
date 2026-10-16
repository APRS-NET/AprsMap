// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsMap.Core;

namespace AprsMap.Core.Tests;

/// <summary>Settings kept in memory, so tests never touch the user's real settings folder.</summary>
internal sealed class InMemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings? Stored { get; private set; } = initial;

    public int Saves { get; private set; }

    public Task<AppSettings?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Stored = settings;
        Saves++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        Stored = null;
        return Task.CompletedTask;
    }

    /// <summary>The APRS-IS source the test profiles use.</summary>
    public static SourceSettings Rotate { get; } =
        new("rotate", "rotate.aprs2.net", SourceKind.AprsIs, "rotate.aprs2.net:14580");

    public static StationProfile Profile(string callsign, string? id = null) =>
        new(id ?? callsign, callsign, callsign, -1, [Rotate.Id], null, null);

    /// <summary>One active profile, as after first-run setup.</summary>
    public static InMemorySettingsStore WithProfile(string callsign = "N0TEST")
    {
        var profile = Profile(callsign);
        return new(new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [Rotate]));
    }
}
