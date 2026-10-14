// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>Checks settings before the engine stores or uses them.</summary>
internal static class SettingsValidator
{
    /// <summary>Throws <see cref="ArgumentException"/> with a user-readable reason when <paramref name="settings"/> is unusable.</summary>
    public static void Validate(AppSettings settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Profiles is null || settings.Units is null)
        {
            throw new ArgumentException("Settings are incomplete.", nameof(settings));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id))
            {
                throw new ArgumentException("Every profile needs its own id.", nameof(settings));
            }
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                throw new ArgumentException("Every profile needs a name.", nameof(settings));
            }
            if (!names.Add(profile.Name.Trim()))
            {
                throw new ArgumentException(
                    $"There is more than one profile named '{profile.Name}'.",
                    nameof(settings)
                );
            }
            if (profile.Latitude is < -90 or > 90 || profile.Longitude is < -180 or > 180)
            {
                throw new ArgumentException(
                    $"Profile '{profile.Name}': the location is off the globe.",
                    nameof(settings)
                );
            }
            if (profile.Latitude.HasValue != profile.Longitude.HasValue)
            {
                throw new ArgumentException(
                    $"Profile '{profile.Name}': a location needs both latitude and longitude.",
                    nameof(settings)
                );
            }

            try
            {
                AprsIsLineSourceFactory.ClientOptions(profile, time);
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException($"Profile '{profile.Name}': {ex.Message}", nameof(settings), ex);
            }
        }

        var active = settings.ActiveProfileId;
        if (settings.Profiles.Count == 0 ? active is not null : !ids.Contains(active ?? string.Empty))
        {
            throw new ArgumentException("The active profile must be one of the profiles.", nameof(settings));
        }
    }
}
