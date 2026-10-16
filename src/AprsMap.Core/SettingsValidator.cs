// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>Checks settings before the engine stores or uses them.</summary>
internal static class SettingsValidator
{
    /// <summary>
    /// Throws <see cref="EngineArgumentException"/> when <paramref name="settings"/> is unusable, with a code the UI
    /// translates (docs/features/F0.9-localization.md) and an English message.
    /// </summary>
    public static void Validate(AppSettings settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Profiles is null || settings.Units is null)
        {
            throw Fail(EngineErrorCodes.SettingsIncomplete, "Settings are incomplete.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id))
            {
                throw Fail(EngineErrorCodes.ProfileIdDuplicate, "Every profile needs its own id.");
            }
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                throw Fail(EngineErrorCodes.ProfileNameMissing, "Every profile needs a name.");
            }
            if (!names.Add(profile.Name.Trim()))
            {
                throw Fail(
                    EngineErrorCodes.ProfileNameDuplicate,
                    $"There is more than one profile named '{profile.Name}'.",
                    profile.Name
                );
            }
            if (profile.Latitude is < -90 or > 90 || profile.Longitude is < -180 or > 180)
            {
                throw Fail(
                    EngineErrorCodes.ProfilePositionOffGlobe,
                    $"Profile '{profile.Name}': the location is off the globe.",
                    profile.Name
                );
            }
            if (profile.Latitude.HasValue != profile.Longitude.HasValue)
            {
                throw Fail(
                    EngineErrorCodes.ProfilePositionIncomplete,
                    $"Profile '{profile.Name}': a location needs both latitude and longitude.",
                    profile.Name
                );
            }

            try
            {
                AprsIsPacketSourceFactory.ClientOptions(profile, FilterComposer.Compose(profile, []), time);
            }
            catch (AprsValidationException ex)
            {
                throw new EngineArgumentException(AprsErrors.From(ex, profile.Name), ex);
            }
            catch (ArgumentException ex)
            {
                // A check APRS.NET doesn't code (yet): still refused, with its English text.
                throw Fail(EngineErrorCodes.Invalid, $"Profile '{profile.Name}': {ex.Message}", profile.Name, ex);
            }
        }

        if (
            settings.Aging is { } aging
            && (
                aging.StaleAfterMinutes < 1
                || aging.RemoveAfterMinutes is { } remove && remove <= aging.StaleAfterMinutes
            )
        )
        {
            throw Fail(
                EngineErrorCodes.StationAgingInvalid,
                "Stations go stale after a minute or more, and are removed only after they're stale."
            );
        }

        var active = settings.ActiveProfileId;
        if (settings.Profiles.Count == 0 ? active is not null : !ids.Contains(active ?? string.Empty))
        {
            throw Fail(EngineErrorCodes.ActiveProfileMissing, "The active profile must be one of the profiles.");
        }
    }

    private static EngineArgumentException Fail(
        string code,
        string message,
        string? profile = null,
        Exception? inner = null
    ) =>
        new(
            new EngineError(
                code,
                message,
                profile is null ? null : new Dictionary<string, string> { [EngineErrorArgs.Profile] = profile }
            ),
            inner
        );
}
